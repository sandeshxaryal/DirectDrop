using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace DirectDrop.App.Services;

public enum HotspotState
{
    Unknown,
    On,
    Off,
    /// <summary>Windows declined the request - typically needs elevation, or Settings must grant permission the first time.</summary>
    Unauthorized,
    /// <summary>No suitable connection profile exists for tethering to attach to (e.g. no network at all).</summary>
    Unsupported,
}

public sealed record HotspotResult(bool Success, HotspotState State, string? UserMessage);

/// <summary>Wi-Fi credentials for the currently-active Mobile Hotspot, so the
/// desktop app can render them as a join-this-Wi-Fi QR code instead of
/// telling the user to go dig through Windows Settings for the network name
/// and password themselves.</summary>
public sealed record HotspotCredentials(string Ssid, string Passphrase);

/// <summary>
/// Controls Windows' built-in Mobile Hotspot via
/// Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager - the
/// same WinRT API the Settings app's Mobile Hotspot toggle itself calls.
/// There is deliberately no fallback to "netsh wlan set hostednetwork":
/// that legacy API creates an ad-hoc-style access point that is a *different
/// feature* from Mobile Hotspot (no automatic ICS/NAT setup, inconsistent
/// driver support, and it is what Microsoft itself has been steering people
/// away from since Windows 10). Using it would satisfy the letter of "make
/// a Wi-Fi network" while working against the spec's actual intent (use the
/// real Mobile Hotspot feature).
///
/// IMPORTANT - honesty about what is and is not proven here:
/// This class calls a real, documented WinRT API correctly, but it has not
/// been exercised on an actual Windows machine as part of building this
/// project (this environment has no Windows host to run it on). There is
/// one specific, documented risk worth knowing about before you rely on it:
/// Microsoft's own docs for CreateFromConnectionProfile state that the
/// calling app "must have the appropriate Wi-Fi control device capability
/// declared in its manifest" (`<DeviceCapability Name="wiFiControl"/>`) or
/// the call throws. That capability declaration is part of the
/// APPX/MSIX package manifest model - it is not something the classic
/// Win32 app.manifest used here (app.manifest, next to this file) can
/// express, because this project builds as an ordinary unpackaged .exe,
/// not an MSIX package. In practice, full-trust classic desktop apps have
/// often been able to call this API anyway (the capability-enforcement
/// model is primarily aimed at sandboxed UWP apps), especially when
/// elevated - but this has genuinely varied across Windows versions and
/// isn't something to take on faith. If TryEnableAsync() below throws or
/// returns Unauthorized/Unsupported on your target machine, that capability
/// gap is the most likely reason, and packaging DirectDrop with an MSIX
/// identity (a "sparse package" is enough - it does not require Store
/// distribution) that declares wiFiControl is the documented fix. Either
/// way, the "open Settings and let the user do it, then detect and
/// continue" fallback in SessionCoordinator is not a lesser path - it's
/// the spec's actual required behavior for exactly this situation. Test
/// the automatic path for real on your target Windows 11 build before
/// relying on it (see README's Test A).
/// </summary>
public sealed class HotspotService
{
    /// <summary>
    /// Whether the profile TryEnableAsync last used as the hotspot's base
    /// connection was Wi-Fi rather than Ethernet. When true, the Mobile
    /// Hotspot's virtual AP and the PC's own internet connection are very
    /// likely sharing one physical Wi-Fi radio, which is the most common
    /// cause of transfer speed decaying over the course of a session (the
    /// radio has to keep time-slicing between being a station and being an
    /// access point, and that contention gets worse as both sides push more
    /// traffic). See TryDisconnectStationWifiAsync for the opt-in fix.
    /// </summary>
    public bool LastEnableUsedWifiUpstream { get; private set; }

    /// <summary>
    /// Attempts to turn Mobile Hotspot on. Never throws - always returns a
    /// result the caller can show to the user directly.
    /// </summary>
    public async Task<HotspotResult> TryEnableAsync()
    {
        ConnectionProfile? profile;
        try
        {
            // Prefer a wired (Ethernet) profile as the hotspot's base
            // connection when one is available, even if Wi-Fi is also
            // connected: GetInternetConnectionProfile() just returns
            // whichever profile Windows currently considers "the" internet
            // connection, which on a laptop with both NICs up is often
            // whichever one associated most recently - not necessarily the
            // wired one. Basing the hotspot on Ethernet instead of Wi-Fi
            // means the Wi-Fi radio only ever has to do one job (be the
            // hotspot's AP), instead of splitting time between that and
            // being a station connected elsewhere - which is what causes
            // the "speed keeps decreasing" symptom on Wi-Fi-only setups.
            profile = FindPreferredConnectionProfile() ?? NetworkInformation.GetInternetConnectionProfile();
        }
        catch (Exception ex)
        {
            AppLogger.Error("GetInternetConnectionProfile failed", ex);
            return new HotspotResult(false, HotspotState.Unsupported,
                "Windows could not find a network connection to base Mobile Hotspot on.");
        }

        LastEnableUsedWifiUpstream = profile is not null && IsWifiProfile(profile);

        if (profile is null)
        {
            // This is the offline-PC case the spec explicitly calls out as a
            // required test scenario. Windows can often still create a
            // hotspot with no upstream internet - but the tethering manager
            // needs *some* connection profile to attach to, and if there is
            // truly none at all, only the Settings app itself can proceed.
            return new HotspotResult(false, HotspotState.Unsupported,
                "Windows needs an active network connection (even without internet access) to create a Mobile Hotspot. " +
                "Open Windows Settings and try turning on Mobile Hotspot there.");
        }

        NetworkOperatorTetheringManager manager;
        try
        {
            manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
        }
        catch (Exception ex)
        {
            // See this class's doc comment above re: the wiFiControl device
            // capability - this is the exception that capability gap would
            // surface as.
            AppLogger.Error("CreateFromConnectionProfile failed", ex);
            return new HotspotResult(false, HotspotState.Unauthorized,
                "Windows requires permission to enable Mobile Hotspot.");
        }

        if (manager.TetheringOperationalState == TetheringOperationalState.On)
        {
            AppLogger.Info("Mobile Hotspot was already on.");
            await TryPreferFiveGigahertzAsync(manager);
            return new HotspotResult(true, HotspotState.On, null);
        }

        try
        {
            NetworkOperatorTetheringOperationResult result = await manager.StartTetheringAsync();

            // Enum is Windows.Networking.NetworkOperators.TetheringOperationStatus -
            // verified against Microsoft Learn's published member list (Success,
            // Unknown, MobileBroadbandDeviceOff, WiFiDeviceOff,
            // EntitlementCheckTimeout, EntitlementCheckFailure,
            // OperationInProgress, BluetoothDeviceOff,
            // NetworkLimitedConnectivity, AlreadyOn, RadioRestriction,
            // BandInterference). Note: AlreadyOn isn't available at the
            // TargetPlatformVersion this project builds against (it was
            // added in a later Windows SDK contract than 10.0.19041.0) -
            // confirmed by a real build, not assumed. It's also documented
            // as only returned by the StartTetheringAsync(configuration)
            // overload, which this code doesn't call, so leaving it out of
            // the switch costs nothing here.
            switch (result.Status)
            {
                case TetheringOperationStatus.Success:
                    AppLogger.Info("Mobile Hotspot started successfully.");
                    await TryPreferFiveGigahertzAsync(manager);
                    return new HotspotResult(true, HotspotState.On, null);

                case TetheringOperationStatus.WiFiDeviceOff:
                    return new HotspotResult(false, HotspotState.Off,
                        "This PC's Wi-Fi adapter is turned off. Turn on Wi-Fi and try again.");

                case TetheringOperationStatus.NetworkLimitedConnectivity:
                case TetheringOperationStatus.EntitlementCheckFailure:
                case TetheringOperationStatus.EntitlementCheckTimeout:
                    return new HotspotResult(false, HotspotState.Unauthorized,
                        "Windows requires permission to enable Mobile Hotspot.");

                default:
                    AppLogger.Warn($"StartTetheringAsync returned {result.Status}");
                    return new HotspotResult(false, HotspotState.Unauthorized,
                        "Windows requires permission to enable Mobile Hotspot.");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLogger.Error("StartTetheringAsync unauthorized", ex);
            return new HotspotResult(false, HotspotState.Unauthorized, "Windows requires permission to enable Mobile Hotspot.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("StartTetheringAsync failed", ex);
            return new HotspotResult(false, HotspotState.Unsupported, "Windows could not start Mobile Hotspot.");
        }
    }

    /// <summary>
    /// Turns Mobile Hotspot off. Called on Stop() only if DirectDrop itself
    /// turned it on this session - see README's session-lifetime rule:
    /// never turn off a hotspot the user already had running.
    /// </summary>
    public async Task DisableAsync()
    {
        try
        {
            ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return;

            NetworkOperatorTetheringManager manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            if (manager.TetheringOperationalState == TetheringOperationalState.On)
                await manager.StopTetheringAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("StopTetheringAsync failed", ex);
        }
    }

    public HotspotState GetCurrentState()
    {
        try
        {
            ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return HotspotState.Unknown;

            NetworkOperatorTetheringManager manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            return manager.TetheringOperationalState == TetheringOperationalState.On ? HotspotState.On : HotspotState.Off;
        }
        catch
        {
            return HotspotState.Unknown;
        }
    }

    /// <summary>
    /// Best-effort speed improvement: requests the 5 GHz band for the
    /// Mobile Hotspot's virtual AP instead of leaving it on whatever Windows
    /// chose by default (frequently 2.4 GHz, for maximum device
    /// compatibility - but 2.4 GHz is also the actual ceiling on real-world
    /// transfer speed people run into, independent of anything this app's
    /// HTTP layer does). Not every Wi-Fi adapter's SoftAP mode supports 5
    /// GHz; if it doesn't, ConfigureAccessPointAsync throws or silently
    /// keeps the prior band, and this falls back to leaving Windows'
    /// default alone - never treated as a hard failure. As with the rest of
    /// this class (see the class-level doc comment), this has not been
    /// exercised against real Mobile Hotspot-capable hardware.
    /// </summary>
    private static async Task TryPreferFiveGigahertzAsync(NetworkOperatorTetheringManager manager)
    {
        // Five-gigahertz access-point configuration APIs require Windows 10 2004 (19041).
        // DirectDrop still supports the older minimum OS, so simply skip this optional
        // optimization there instead of calling an API that is unavailable at runtime.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            AppLogger.Info("5 GHz Mobile Hotspot preference skipped: Windows 10 2004 or newer is required.");
            return;
        }

        try
        {
            NetworkOperatorTetheringAccessPointConfiguration config = manager.GetCurrentAccessPointConfiguration();
            if (config.Band == TetheringWiFiBand.FiveGigahertz) return;

            bool supported = await config.IsBandSupportedAsync(TetheringWiFiBand.FiveGigahertz);
            if (!supported)
            {
                AppLogger.Info("This Wi-Fi adapter doesn't support a 5 GHz Mobile Hotspot - staying on its default band.");
                return;
            }

            config.Band = TetheringWiFiBand.FiveGigahertz;
            // ConfigureAccessPointAsync is an IAsyncAction (no result to
            // check) - it either completes or throws.
            await manager.ConfigureAccessPointAsync(config);
            AppLogger.Info("Mobile Hotspot configured for 5 GHz (faster transfers where the adapter supports it).");
        }
        catch (Exception ex)
        {
            // Genuinely non-fatal: worst case, transfers run at whatever
            // speed the default band gives, which is exactly what happened
            // before this method existed.
            AppLogger.Info($"5 GHz Mobile Hotspot band request skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads back the SSID/passphrase Windows is actually using for the
    /// active Mobile Hotspot, so the desktop UI can render a "scan to join
    /// this Wi-Fi" QR code instead of sending the user to hunt through
    /// Windows Settings for the network name and password themselves.
    /// Returns null if there's no active hotspot or the configuration can't
    /// be read (never throws).
    /// </summary>
    public HotspotCredentials? GetCredentials()
    {
        try
        {
            ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return null;

            NetworkOperatorTetheringManager manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            if (manager.TetheringOperationalState != TetheringOperationalState.On) return null;

            NetworkOperatorTetheringAccessPointConfiguration config = manager.GetCurrentAccessPointConfiguration();
            if (string.IsNullOrEmpty(config.Ssid)) return null;

            return new HotspotCredentials(config.Ssid, config.Passphrase ?? "");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not read Mobile Hotspot credentials", ex);
            return null;
        }
    }

    /// <summary>
    /// Best-effort read of how many devices are currently associated with
    /// the Mobile Hotspot's Wi-Fi radio (NetworkOperatorTetheringManager.ClientCount -
    /// a live OS-level count, distinct from DirectDrop's own app-level
    /// single-device gate in SessionState). Used purely for the UI's
    /// "has a phone joined the Wi-Fi yet" pairing-step transition; not
    /// something DirectDrop can set a maximum for (MaxClientCount on this
    /// API is read-only - see SessionState.TryAuthorizeDevice for where the
    /// actual one-device-at-a-time enforcement happens).
    /// Returns null if there's no active hotspot or the count can't be read
    /// (never throws) - callers should treat null as "unknown", not "zero".
    /// </summary>
    public int? GetClientCount()
    {
        try
        {
            ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return null;

            NetworkOperatorTetheringManager manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            if (manager.TetheringOperationalState != TetheringOperationalState.On) return null;

            return (int)manager.ClientCount;
        }
        catch (Exception ex)
        {
            AppLogger.Info($"Could not read Mobile Hotspot client count: {ex.Message}");
            return null;
        }
    }

    /// <summary>Opens the Windows Settings page for Mobile Hotspot directly - the fallback the spec requires.</summary>
    public static void OpenHotspotSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network-mobilehotspot") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error("Could not open Mobile Hotspot settings", ex);
        }
    }

    /// <summary>
    /// Scans every connected profile Windows knows about and returns a wired
    /// one if any exists, so the caller can prefer it over whatever
    /// GetInternetConnectionProfile() would have picked. Returns null (not a
    /// failure - just "no preference to express") if there's no wired
    /// profile, in which case the caller falls back to the default profile.
    /// </summary>
    private static ConnectionProfile? FindPreferredConnectionProfile()
    {
        try
        {
            return NetworkInformation.GetConnectionProfiles()
                .FirstOrDefault(p => p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None
                    && !IsWifiProfile(p));
        }
        catch (Exception ex)
        {
            AppLogger.Info($"Could not enumerate connection profiles to prefer a wired one: {ex.Message}");
            return null;
        }
    }

    private static bool IsWifiProfile(ConnectionProfile profile)
    {
        try
        {
            return profile.NetworkAdapter is not null && profile.IsWlanConnectionProfile;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort, OPT-IN ONLY fix for the case where TryEnableAsync had no
    /// choice but to base the hotspot on Wi-Fi (no Ethernet present): drops
    /// the PC's own Wi-Fi *station* connection (not the whole adapter, not
    /// the Mobile Hotspot's AP) via the Native Wifi API, so the radio stops
    /// splitting time between "connected to the router" and "being an access
    /// point" and can dedicate itself fully to serving the phone.
    ///
    /// HONESTY CHECK, same spirit as this class's header comment: this has
    /// NOT been verified on real hardware. Windows built Mobile Hotspot's ICS
    /// bridging on top of there being an upstream connection profile - the
    /// same reason TryEnableAsync refuses to run with a null profile at all
    /// (see above). It is plausible that disconnecting that profile's station
    /// link tears the whole hotspot down along with it rather than leaving a
    /// contention-free AP running, and that risk is exactly why this method
    /// is never called automatically anywhere in this codebase - it is only
    /// ever meant to be wired to an explicit button the user presses after
    /// being told the tradeoff (see SessionCoordinator /
    /// ContentionRiskFromPcWifi), never invoked as a side effect of starting
    /// a session. Test it for real on your target machine (mid-transfer,
    /// confirm the phone doesn't get dropped) before trusting it in practice.
    /// </summary>
    public Task<bool> TryDisconnectStationWifiAsync()
    {
        return Task.Run(() =>
        {
            IntPtr clientHandle = IntPtr.Zero;
            IntPtr interfaceListPtr = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out clientHandle) != 0) return false;
                if (WlanEnumInterfaces(clientHandle, IntPtr.Zero, out interfaceListPtr) != 0) return false;

                int numberOfItems = Marshal.ReadInt32(interfaceListPtr);
                IntPtr listStart = interfaceListPtr + 8; // skip NumberOfItems + Index (two 4-byte fields)
                int entrySize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();

                bool disconnectedAny = false;
                for (int i = 0; i < numberOfItems; i++)
                {
                    IntPtr entryPtr = listStart + i * entrySize;
                    var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(entryPtr);
                    Guid guid = info.InterfaceGuid;

                    uint result = WlanDisconnect(clientHandle, ref guid, IntPtr.Zero);
                    if (result == 0) disconnectedAny = true;
                }

                if (disconnectedAny)
                    AppLogger.Info("Disconnected this PC's Wi-Fi station connection to remove hotspot radio contention.");
                return disconnectedAny;
            }
            catch (Exception ex)
            {
                AppLogger.Error("TryDisconnectStationWifiAsync failed", ex);
                return false;
            }
            finally
            {
                if (interfaceListPtr != IntPtr.Zero) WlanFreeMemory(interfaceListPtr);
                if (clientHandle != IntPtr.Zero) WlanCloseHandle(clientHandle, IntPtr.Zero);
            }
        });
    }

    // CharSet.Unicode is required here: the real WLAN_INTERFACE_INFO struct's
    // strInterfaceDescription field is WCHAR[256] (UTF-16), and the default
    // marshaling CharSet (Ansi) would both mis-decode it and, more
    // importantly, mis-calculate this struct's total size - which would then
    // throw off every offset computed from entrySize in
    // TryDisconnectStationWifiAsync above.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;
        public uint isState;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanDisconnect(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);
}
