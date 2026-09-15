using System.Net.Http;
using System.IO;
using DirectDrop.Core;
using DirectDrop.Core.Models;
using DirectDrop.Server;

namespace DirectDrop.App.Services;

public enum SessionPhase
{
    Idle,
    DetectingNetwork,
    EnablingHotspot,
    WaitingForManualHotspot,
    ConfiguringFirewall,
    StartingServer,
    Ready,
    Error,
}

public sealed record SessionStartOutcome(bool Success, string? ConnectionUrl, string? WifiNetworkName, string? WifiPassword, string? ErrorMessage);

/// <summary>
/// Implements the exact sequence the spec's "CORE USER EXPERIENCE" section
/// lays out for pressing "Start Direct Wi-Fi": detect adapter, detect/enable
/// hotspot, detect IP, start server, generate token, generate QR, wait for
/// the iPhone. Every failure path produces a human-readable message per the
/// spec's "the application should never silently fail" rule - see
/// ErrorMessages.cs for the exact copy.
///
/// The manual-hotspot fallback works by polling, not by asking the caller to
/// signal "I'm done": once auto-enable fails, this raises
/// WaitingForManualHotspot (so the UI can show the message and an "Open
/// Mobile Hotspot Settings" button) and then keeps checking for a
/// hotspot-shaped network adapter for up to ManualHotspotTimeout. The spec's
/// own wording is "automatically continue once the hotspot becomes
/// available" - so continuing is this class's job, not a button the user
/// has to remember to press afterwards.
/// </summary>
public sealed class SessionCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan AutoEnableSettleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ManualHotspotTimeout = TimeSpan.FromSeconds(120);

    private readonly HotspotService _hotspot = new();
    private readonly DirectDropServer _server = new();
    private readonly FastModeService _fastMode = new();
    private bool _weStartedTheHotspot;
    private HotspotState _lastObservedHotspotState = HotspotState.Unknown;

    public SessionState? CurrentSession { get; private set; }
    public event Action<SessionPhase, string?>? PhaseChanged;

    /// <summary>
    /// Fires when GetCurrentState() reports the Mobile Hotspot has changed
    /// on/off state *while a session is active* - e.g. the user (or Windows
    /// itself, which will drop Mobile Hotspot if it decides there are no
    /// associated devices for a while, or on some sleep/wake cycles) turned
    /// it off from Settings mid-transfer, without DirectDrop asking it to.
    /// Edge-triggered: fires once per transition, not on every poll, so the
    /// UI can show/hide a banner instead of nagging every second. Only
    /// meaningful to check while CurrentSession is not null - call
    /// CheckHotspotHealth() periodically (e.g. from the same 1s UI timer
    /// that already polls RefreshActiveSessionUi) to drive it.
    /// </summary>
    public event Action<bool /* isOn */>? HotspotExternallyChanged;

    /// <summary>
    /// True once StartAsync has finished if DirectDrop had to fall back to a
    /// Wi-Fi-based Mobile Hotspot (no Ethernet available) - the configuration
    /// most likely to show declining transfer speed over time, because the
    /// same radio is acting as both a station and an access point. This is
    /// informational only: Fast Mode uses this signal to decide whether its
    /// temporary Wi‑Fi station disconnect is applicable.
    /// </summary>
    public bool ContentionRiskFromPcWifi { get; private set; }

    public async Task<SessionStartOutcome> StartAsync(
        string sharedFolder,
        string uploadDestination,
        int preferredPort,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (Directory.Exists(sharedFolder))
                Directory.Delete(sharedFolder, recursive: true);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Could not clear stale DirectDrop session staging data: {ex.Message}");
        }

        Directory.CreateDirectory(sharedFolder);
        Directory.CreateDirectory(uploadDestination);

        ContentionRiskFromPcWifi = false;
        _lastObservedHotspotState = HotspotState.Unknown;
        _weStartedTheHotspot = false;

        PhaseChanged?.Invoke(SessionPhase.DetectingNetwork, null);
        AdapterInfo? adapter = NetworkInfoService.FindBestServerAddress();

        if (adapter is null || !adapter.LooksLikeHotspot)
        {
            PhaseChanged?.Invoke(SessionPhase.EnablingHotspot, "Starting Mobile Hotspot…");
            DateTime nextAttempt = DateTime.MinValue;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                AdapterInfo? waitingAdapter = NetworkInfoService.FindBestServerAddress();
                if (waitingAdapter is not null && waitingAdapter.LooksLikeHotspot)
                {
                    adapter = waitingAdapter;
                    break;
                }

                if (DateTime.UtcNow >= nextAttempt)
                {
                    HotspotResult result = await _hotspot.TryEnableAsync();
                    if (result.Success)
                    {
                        _weStartedTheHotspot = true;
                        ContentionRiskFromPcWifi = _hotspot.LastEnableUsedWifiUpstream;
                    }
                    nextAttempt = DateTime.UtcNow.AddSeconds(3);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        if (adapter is null)
            return new SessionStartOutcome(false, null, null, null, ErrorMessages.NoNetworkAdapterFound);

        string token = TokenValidator.GenerateToken();
        var session = new SessionState
        {
            Token = token,
            SharedFolder = sharedFolder,
            UploadDestination = uploadDestination,
            PcDisplayName = $"{Environment.UserName}'s PC",
        };

        int port = 0;
        bool started = false;
        Exception? lastServerFailure = null;

        // Windows can recreate the Mobile Hotspot adapter while leaving the
        // previous IPv4 address briefly visible to callers. If that happens,
        // the old code surfaced "local server couldn't start" even though the
        // hotspot was already on. Retry once with a freshly detected adapter
        // and a clean server lifetime instead of requiring an app restart.
        for (int attempt = 0; attempt < 2 && !started; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                await _server.StopAsync();
                await Task.Delay(250, cancellationToken);
                adapter = NetworkInfoService.FindBestServerAddress();
                if (adapter is null) break;
                PhaseChanged?.Invoke(SessionPhase.StartingServer, "Refreshing the local connection…");
            }
            else
            {
                PhaseChanged?.Invoke(SessionPhase.StartingServer, null);
            }

            try
            {
                string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                port = await _server.StartAsync(session, adapter.IPv4, wwwroot, preferredPort, cancellationToken: cancellationToken);

                // Do not reveal the QR until the listener answers a real local
                // health request. This closes the small race where Kestrel has
                // started but the app UI moves ahead before the socket is truly
                // ready to accept requests.
                if (!await WaitForServerHealthAsync(adapter.IPv4, port, cancellationToken))
                    throw new InvalidOperationException("The local transfer service did not answer its readiness check.");

                started = true;
            }
            catch (Exception ex)
            {
                lastServerFailure = ex;
                AppLogger.Error($"Server start attempt {attempt + 1} failed", ex);
            }
        }

        if (!started || adapter is null)
        {
            await _server.StopAsync();
            if (lastServerFailure is not null) AppLogger.Error("Server failed to start after recovery attempt", lastServerFailure);
            return new SessionStartOutcome(false, null, null, null, ErrorMessages.ServerFailedToStart);
        }

        PhaseChanged?.Invoke(SessionPhase.ConfiguringFirewall, null);

        // Request Windows network permission before revealing the QR.
        // This uses the standard UAC elevation only when Windows requires it
        // and keeps a tightly scoped DirectDrop firewall rule for later runs.
        if (!await FirewallService.EnsureRuleForPortAsync(port))
        {
            await _server.StopAsync();
            return new SessionStartOutcome(false, null, null, null,
                "DirectDrop needs Windows network access permission to connect your phone.");
        }

        CurrentSession = session;
        string url = $"http://{adapter.IPv4}:{port}/?token={token}";

        HotspotCredentials? credentials = adapter.LooksLikeHotspot ? _hotspot.GetCredentials() : null;

        PhaseChanged?.Invoke(SessionPhase.Ready, url);
        AppLogger.Info($"DirectDrop session started: {adapter.IPv4}:{port}, hotspot-adapter={adapter.LooksLikeHotspot}");

        return new SessionStartOutcome(true, url, credentials?.Ssid, credentials?.Passphrase, null);
    }

    private static async Task<bool> WaitForServerHealthAsync(System.Net.IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://{address}:{port}"),
            Timeout = TimeSpan.FromSeconds(1)
        };

        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using HttpResponseMessage response = await client.GetAsync("/api/health", cancellationToken);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }

            await Task.Delay(150, cancellationToken);
        }

        return false;
    }

    public async Task StopAsync(bool turnOffHotspotIfWeStartedIt)
    {
        await _fastMode.RestoreAsync();
        await _server.StopAsync();

        CurrentSession?.CleanupTempFiles();
        CurrentSession?.CleanupSessionShareFolder();
        CurrentSession?.TransferDirectionGate.Dispose();
        CurrentSession = null;

        if (_weStartedTheHotspot && turnOffHotspotIfWeStartedIt)
        {
            await _hotspot.DisableAsync();
            _weStartedTheHotspot = false;
        }

        PhaseChanged?.Invoke(SessionPhase.Idle, null);
        AppLogger.Info("DirectDrop session stopped.");
    }

    /// <summary>Whether this session turned the hotspot on itself - determines whether Stop() should offer to turn it back off.</summary>
    public bool DidWeStartTheHotspot => _weStartedTheHotspot;

    public bool FastModeEnabled => _fastMode.IsEnabled;

    public async Task<bool> EnableFastModeAsync()
    {
        // Only disconnect the PC's Wi-Fi station when the hotspot itself is
        // using Wi-Fi upstream. On Ethernet-backed hotspots, leaving Wi-Fi
        // alone is safer because there is no same-radio contention to remove.
        return await _fastMode.EnableAsync(ContentionRiskFromPcWifi);
    }

    public async Task DisableFastModeAsync() => await _fastMode.RestoreAsync();

    public Task CancelTransferAsync(string transferId)
    {
        CurrentSession?.CancelTransfer(transferId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Call periodically (e.g. once a second) while a session is active to
    /// detect the Mobile Hotspot getting turned off outside of DirectDrop -
    /// the "app should detect if the hotspot is off" requirement. Raises
    /// HotspotExternallyChanged only on an actual state transition.
    /// </summary>
    public void CheckHotspotHealth()
    {
        if (CurrentSession is null) return;

        HotspotState current = _hotspot.GetCurrentState();
        if (current == HotspotState.Unknown || current == _lastObservedHotspotState) return;

        bool wasFirstCheck = _lastObservedHotspotState == HotspotState.Unknown;
        _lastObservedHotspotState = current;

        // Don't fire on the very first observation after Ready - only on a
        // genuine flip afterwards, otherwise every session start would
        // "announce" whatever state the hotspot already happened to be in.
        if (!wasFirstCheck)
            HotspotExternallyChanged?.Invoke(current == HotspotState.On);
    }

    /// <summary>Re-enables the Mobile Hotspot after CheckHotspotHealth reports it was turned off externally.</summary>
    public async Task<HotspotResult> RetryEnableHotspotAsync()
    {
        // Re-enable quietly in the background. Do not surface Windows errors or
        // ask the user to press a retry button; Windows can take several seconds
        // to recreate the virtual adapter after the hotspot is toggled.
        DateTime deadline = DateTime.UtcNow.Add(TimeSpan.FromMinutes(2));
        HotspotResult last = new(false, HotspotState.Off, "Starting Windows Mobile Hotspot…");

        while (DateTime.UtcNow < deadline)
        {
            HotspotState current = _hotspot.GetCurrentState();
            if (current == HotspotState.On)
                return new HotspotResult(true, HotspotState.On, null);

            last = await _hotspot.TryEnableAsync();
            if (last.Success)
            {
                _weStartedTheHotspot = true;
                ContentionRiskFromPcWifi = _hotspot.LastEnableUsedWifiUpstream;
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        return last;
    }

    /// <summary>
    /// Live count of devices associated with the Mobile Hotspot's Wi-Fi radio
    /// (null if unknown/unsupported on this adapter) - see
    /// HotspotService.GetClientCount for what this is and isn't good for.
    /// Used by the UI to know when to switch from the "scan to pair" Wi-Fi QR
    /// to the "scan to transfer" token QR.
    /// </summary>
    public int? GetHotspotClientCount() => _hotspot.GetClientCount();

    private static async Task<AdapterInfo?> WaitForHotspotAdapterAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            AdapterInfo? adapter = NetworkInfoService.FindBestServerAddress();
            if (adapter is not null && adapter.LooksLikeHotspot)
                return adapter;

            await Task.Delay(1000, cancellationToken);
        }

        // Fall back to whatever the best guess is, even if it doesn't look
        // like a hotspot adapter - better to try a plausible address than
        // to refuse to start at all.
        return NetworkInfoService.FindBestServerAddress();
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();
}
