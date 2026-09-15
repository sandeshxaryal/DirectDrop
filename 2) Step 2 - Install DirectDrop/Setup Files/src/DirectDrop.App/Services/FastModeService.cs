using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DirectDrop.App.Services;

public sealed record WifiRestoreInfo(string InterfaceName, string Ssid);

/// <summary>
/// Applies only reversible Windows-side optimizations that are useful for a
/// local file-transfer session. The service captures the user's active power
/// plan and, when explicitly requested, the connected Wi-Fi profile before
/// changing anything. RestoreAsync returns those settings on shutdown.
/// Bluetooth is intentionally left untouched: disabling it is not a
/// dependable throughput optimization and can unexpectedly disconnect HID
/// devices such as keyboards and mice.
/// </summary>
public sealed class FastModeService
{
    private Guid? _previousPowerScheme;
    private bool _powerPlanChanged;
    private WifiRestoreInfo? _wifiRestore;
    private bool _wifiDisconnected;

    public bool IsEnabled => _powerPlanChanged || _wifiDisconnected;

    public async Task<bool> EnableAsync(bool disconnectWifiStation)
    {
        if (IsEnabled) return true;

        bool powerOk = await Task.Run(CaptureAndSetHighPerformance);
        if (!powerOk)
            AppLogger.Warn("Fast Mode could not switch the Windows power plan.");

        if (disconnectWifiStation)
        {
            _wifiRestore = await Task.Run(CaptureConnectedWifi);
            if (_wifiRestore is not null)
            {
                _wifiDisconnected = await Task.Run(() => DisconnectWifiInterface(_wifiRestore.InterfaceName));
                if (!_wifiDisconnected)
                    AppLogger.Warn("Fast Mode could not disconnect the PC's Wi-Fi station connection.");
            }
        }

        return IsEnabled;
    }

    public async Task RestoreAsync()
    {
        if (_wifiDisconnected && _wifiRestore is not null)
        {
            try { await Task.Run(() => ReconnectWifi(_wifiRestore)); }
            catch (Exception ex) { AppLogger.Error("Fast Mode Wi-Fi restore failed", ex); }
        }

        if (_powerPlanChanged && _previousPowerScheme is Guid previous)
        {
            try { await Task.Run(() => RunPowerCfg($"/setactive {previous:D}")); }
            catch (Exception ex) { AppLogger.Error("Fast Mode power plan restore failed", ex); }
        }

        _previousPowerScheme = null;
        _powerPlanChanged = false;
        _wifiRestore = null;
        _wifiDisconnected = false;
    }

    private bool CaptureAndSetHighPerformance()
    {
        string? output = RunProcess("powercfg.exe", "/getactivescheme");
        Match match = Regex.Match(output ?? string.Empty,
            @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
        if (!match.Success || !Guid.TryParse(match.Value, out Guid previous))
            return false;

        _previousPowerScheme = previous;
        if (!RunPowerCfg("/setactive SCHEME_MIN"))
        {
            _previousPowerScheme = null;
            return false;
        }

        _powerPlanChanged = true;
        AppLogger.Info($"Fast Mode enabled High Performance power plan; previous scheme={previous:D}.");
        return true;
    }

    private static WifiRestoreInfo? CaptureConnectedWifi()
    {
        string? output = RunProcess("netsh.exe", "wlan show interfaces");
        if (string.IsNullOrWhiteSpace(output)) return null;

        string? interfaceName = null;
        string? ssid = null;
        bool connected = false;

        foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.StartsWith("Name", StringComparison.OrdinalIgnoreCase))
                interfaceName = ValueAfterColon(line);
            else if (line.StartsWith("State", StringComparison.OrdinalIgnoreCase))
                connected = line.Contains("connected", StringComparison.OrdinalIgnoreCase);
            else if (line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase)
                     && !line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                ssid = ValueAfterColon(line);

            if (connected && !string.IsNullOrWhiteSpace(interfaceName) && !string.IsNullOrWhiteSpace(ssid))
                break;
        }

        return connected && !string.IsNullOrWhiteSpace(interfaceName) && !string.IsNullOrWhiteSpace(ssid)
            ? new WifiRestoreInfo(interfaceName, ssid)
            : null;
    }

    private static bool DisconnectWifiInterface(string interfaceName)
    {
        string? output = RunProcessWithArgs("netsh.exe", new[] { "wlan", "disconnect", $"interface={interfaceName}" });
        bool ok = output is not null;
        if (ok) AppLogger.Info($"Fast Mode disconnected Wi-Fi station '{interfaceName}'.");
        return ok;
    }

    private static void ReconnectWifi(WifiRestoreInfo restore)
    {
        string? output = RunProcessWithArgs("netsh.exe", new[]
        {
            "wlan", "connect", $"name={restore.Ssid}", $"interface={restore.InterfaceName}"
        });
        if (output is not null)
            AppLogger.Info($"Fast Mode requested Wi-Fi reconnect for '{restore.Ssid}'.");
    }

    private static bool RunPowerCfg(string args) => RunProcess("powercfg.exe", args) is not null;

    private static string? ValueAfterColon(string line)
    {
        int index = line.IndexOf(':');
        return index < 0 ? null : line[(index + 1)..].Trim();
    }

    private static string? RunProcess(string fileName, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                }
            };
            process.Start();
            string stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Fast Mode command failed: {fileName} {arguments}", ex);
            return null;
        }
    }

    private static string? RunProcessWithArgs(string fileName, IEnumerable<string> arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                }
            };
            foreach (string argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);

            process.Start();
            string stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Fast Mode command failed: {fileName}", ex);
            return null;
        }
    }
}
