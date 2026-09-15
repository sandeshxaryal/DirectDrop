using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DirectDrop.Server;

public sealed record AdapterInfo(string Name, string Description, IPAddress IPv4, bool LooksLikeHotspot);

/// <summary>
/// Enumerates active IPv4 network adapters using only BCL APIs
/// (System.Net.NetworkInformation), so this works unchanged on Windows,
/// Linux, and macOS. The one Windows-specific piece of knowledge here is the
/// *heuristic* used to guess which adapter is the Mobile Hotspot's virtual
/// access point - see the comment on <see cref="LooksLikeHotspotAdapter"/>.
/// </summary>
public static class NetworkInfoService
{
    public static IEnumerable<AdapterInfo> GetActiveIPv4Adapters()
    {
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (UnicastIPAddressInformation addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr.Address)) continue;

                yield return new AdapterInfo(ni.Name, ni.Description, addr.Address, LooksLikeHotspotAdapter(ni));
            }
        }
    }

    /// <summary>
    /// Picks the best address to bind and advertise in the QR code:
    /// prefer an adapter that looks like the Mobile Hotspot's virtual AP,
    /// then fall back to any private (RFC1918) address, then to whatever's left.
    /// </summary>
    public static AdapterInfo? FindBestServerAddress()
    {
        var adapters = GetActiveIPv4Adapters().ToList();

        return adapters.FirstOrDefault(a => a.LooksLikeHotspot)
            ?? adapters.FirstOrDefault(a => IsPrivateAddress(a.IPv4))
            ?? adapters.FirstOrDefault();
    }

    /// <summary>
    /// When Windows Mobile Hotspot is active, it creates a virtual adapter -
    /// historically surfaced to NetworkInterface as something named like
    /// "Local Area Connection* N" with description containing "Microsoft
    /// Wi-Fi Direct Virtual Adapter" (the hotspot rides on Wi-Fi Direct
    /// under the hood). This naming has been consistent since Windows 10
    /// but is NOT a documented, guaranteed contract from Microsoft - if a
    /// future Windows build renames it, this heuristic degrades gracefully
    /// to "any private IPv4 address" rather than failing outright.
    /// </summary>
    private static bool LooksLikeHotspotAdapter(NetworkInterface ni) =>
        ni.Description.Contains("Wi-Fi Direct Virtual Adapter", StringComparison.OrdinalIgnoreCase)
        || ni.Name.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivateAddress(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        if (b.Length != 4) return false;

        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168);
    }
}
