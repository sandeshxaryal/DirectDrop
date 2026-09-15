using System.Diagnostics;

namespace DirectDrop.App.Services;

/// <summary>
/// Maintains one narrowly scoped inbound TCP firewall rule for DirectDrop.
/// The app itself runs as a normal user; when Windows requires elevation,
/// netsh is launched with the standard Windows UAC "runas" verb.
/// The rule is intentionally persistent for the selected DirectDrop port so
/// the user does not have to approve networking on every session. The
/// installer removes the rule when DirectDrop is uninstalled/upgraded.
/// </summary>
public static class FirewallService
{
    private const string RuleName = "DirectDrop Local Server";

    public static async Task<bool> EnsureRuleForPortAsync(int port)
    {
        // Remove any stale rule first so an old port cannot remain open.
        await RemoveRuleAsync();

        string arguments =
            $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow " +
            $"protocol=TCP localport={port} profile=private,public,domain " +
            "description=\"Local-only DirectDrop file transfer server\"";

        var result = await RunElevatedNetshAsync(arguments);
        if (!result.success)
        {
            AppLogger.Warn($"Could not create firewall rule for port {port}: {result.output}");
            return false;
        }

        AppLogger.Info($"Firewall rule created for port {port} (Private/Public/Domain profiles).");
        return true;
    }

    public static async Task RemoveRuleAsync()
    {
        var result = await RunElevatedNetshAsync($"advfirewall firewall delete rule name=\"{RuleName}\"");
        _ = result;
    }

    private static async Task<(bool success, string output)> RunElevatedNetshAsync(string arguments)
    {
        try
        {
            // UAC is the explicit Windows security gate for changing firewall policy.
            // It is shown only when Windows says elevation is required.
            var psi = new ProcessStartInfo("netsh.exe")
            {
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(psi);
            if (process is null) return (false, "netsh.exe could not be started.");

            await process.WaitForExitAsync();
            return (process.ExitCode == 0, process.ExitCode == 0 ? "" : $"netsh exit code {process.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Firewall permission was declined by the user.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Elevated netsh invocation failed", ex);
            return (false, ex.Message);
        }
    }
}
