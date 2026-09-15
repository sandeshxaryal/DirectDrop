namespace DirectDrop.App.Services;

/// <summary>
/// Every string here exists because the spec gives explicit "Bad: ... /
/// Good: ..." examples and says errors must be human-readable. Centralizing
/// them means MainWindow never has to improvise copy for a failure path,
/// and it's the one place to edit wording later.
/// </summary>
public static class ErrorMessages
{
    public const string HotspotGeneric =
        "DirectDrop couldn't turn on Mobile Hotspot automatically. " +
        "Turn it on in Windows Settings, then come back here.";

    public const string NoNetworkAdapterFound =
        "DirectDrop couldn't find a network to connect your phone through. " +
        "Make sure Wi-Fi is turned on for this PC, then try again.";

    public const string ServerFailedToStart =
        "DirectDrop couldn't start its local server. " +
        "This is unusual - check the log file (Help > Open Log Folder) for details, or try restarting DirectDrop.";

    public static string PortInUseButRetrying(int triedPort) =>
        $"Port {triedPort} is already being used by something else on this PC. Trying another port…";

    public static string DeviceNotOnDirectDropWifi(string wifiName) =>
        $"Your phone isn't connected to the DirectDrop Wi-Fi network yet. " +
        $"Connect to '{wifiName}' and scan the QR code again.";

    public static string FirewallBlockedWarning(int port) =>
        $"Windows Firewall may be blocking DirectDrop on port {port}. " +
        "If your phone can't connect, check Windows Defender Firewall settings for a 'DirectDrop Local Server' rule on Private networks.";
}
