using System.Net;
using System.Net.Sockets;
using System.Text;
using CastBridge.Core.Cast;
using CastBridge.Core.Discovery;

namespace CastBridge.Core.Diagnostics;

public sealed record DiagnosticItem(string Title, string Detail, DiagnosticSeverity Severity);

public enum DiagnosticSeverity
{
    Ok,
    Info,
    Warning,
    Problem,
}

/// <summary>
/// Explains why casting might not work: which networks this PC is on, whether the speakers are on
/// the same subnet, whether a VPN is in the way, and whether the media server is reachable.
/// Most "the speaker cannot play my file" cases are one of these, not a bug in the app.
/// </summary>
public static class NetworkDiagnostics
{
    public static IReadOnlyList<DiagnosticItem> Run(IReadOnlyList<CastDevice> devices, int? mediaServerPort)
    {
        var items = new List<DiagnosticItem>();
        var subnets = LocalNetwork.GetSubnets(includeVirtual: true);

        items.Add(new DiagnosticItem(
            "This PC's network interfaces",
            subnets.Count == 0
                ? "No active network interface was found."
                : string.Join(Environment.NewLine, subnets.Select(s =>
                    $"• {s.LocalAddress}/{s.PrefixLength} on \"{s.InterfaceName}\"{(s.IsVirtual ? " (virtual/VPN)" : string.Empty)}")),
            subnets.Count == 0 ? DiagnosticSeverity.Problem : DiagnosticSeverity.Ok));

        var virtuals = subnets.Where(s => s.IsVirtual).ToArray();
        if (virtuals.Length > 0)
        {
            items.Add(new DiagnosticItem(
                "VPN or virtual adapters detected",
                "Cast discovery and playback only work on the real LAN. If a VPN is active, the speakers may be " +
                "unreachable even though the app sees them. Turn the VPN off while casting, or exclude the local " +
                "subnet from the VPN route.\n" +
                string.Join(Environment.NewLine, virtuals.Select(v => $"• {v.InterfaceName} ({v.Description})")),
                DiagnosticSeverity.Warning));
        }

        var physical = subnets.Where(s => !s.IsVirtual).ToArray();
        if (devices.Count > 0 && physical.Length > 0)
        {
            var unreachable = new List<string>();
            foreach (var device in devices)
            {
                if (!IPAddress.TryParse(device.IpAddress, out var ip))
                    continue;

                if (!physical.Any(s => LocalNetwork.IsSameSubnet(s.LocalAddress, ip, Math.Min(s.PrefixLength, 24))))
                    unreachable.Add($"{device.Name} ({device.IpAddress})");
            }

            items.Add(unreachable.Count == 0
                ? new DiagnosticItem("Speakers on the same subnet", "Every discovered speaker shares a /24 with this PC.", DiagnosticSeverity.Ok)
                : new DiagnosticItem(
                    "Speakers on another subnet",
                    "These devices are not in the same /24 as any real network adapter, so casting will fail: " +
                    string.Join(", ", unreachable) +
                    "\nThe usual cause is the PC being on a different Wi-Fi band/SSID, a guest network, or a VPN.",
                    DiagnosticSeverity.Problem));
        }

        if (mediaServerPort is { } port)
        {
            var reachable = IsPortListening(port);
            items.Add(new DiagnosticItem(
                "Media server",
                reachable
                    ? $"Listening on TCP {port} on all interfaces. Speakers fetch your music from this port."
                    : $"Nothing is listening on TCP {port}. Start the app's media server before casting.",
                reachable ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning));

            items.Add(new DiagnosticItem(
                "Windows Firewall",
                "Inbound connections from the speakers must be allowed, otherwise tracks load and immediately stop. " +
                "Use the \"Allow through Windows Firewall\" button, or run this in an elevated PowerShell:\n" +
                FirewallHelper.BuildRuleCommand(port, "CastBridge media server"),
                DiagnosticSeverity.Info));
        }

        var manualOnly = devices.Where(d => d.IsManual).ToArray();
        if (manualOnly.Length > 0)
        {
            items.Add(new DiagnosticItem(
                "Devices added by address",
                $"{manualOnly.Length} device(s) were added manually, which usually means automatic discovery is blocked on this network.",
                DiagnosticSeverity.Info));
        }

        if (devices.Count == 0)
        {
            items.Add(new DiagnosticItem(
                "No speakers found",
                "Check that the speaker is powered on, on the same Wi-Fi network, and that this PC's network profile is " +
                "\"Private\" (multicast discovery is blocked on Public networks). You can also add a device by IP address.",
                DiagnosticSeverity.Problem));
        }

        return items;
    }

    public static string Format(IReadOnlyList<DiagnosticItem> items)
    {
        var builder = new StringBuilder();
        foreach (var item in items)
        {
            builder.Append('[').Append(item.Severity.ToString().ToUpperInvariant()).Append("] ").Append(item.Title).AppendLine();
            foreach (var line in item.Detail.Split('\n'))
                builder.Append("    ").AppendLine(line.TrimEnd('\r'));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static bool IsPortListening(int port)
    {
        try
        {
            var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            return listeners.Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Builds the firewall command the user can approve; the app never elevates by itself.</summary>
public static class FirewallHelper
{
    public static string BuildRuleCommand(int port, string ruleName, string protocol = "TCP") =>
        $"New-NetFirewallRule -DisplayName \"{ruleName}\" -Direction Inbound -Action Allow " +
        $"-Protocol {protocol} -LocalPort {port} -Profile Private,Domain";

    public static async Task<bool> TryAddRuleElevatedAsync(int port, string ruleName, CancellationToken cancellationToken = default)
    {
        var script = BuildRuleCommand(port, ruleName);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            UseShellExecute = true,
            Verb = "runas", // shows the standard Windows elevation prompt
            CreateNoWindow = true,
        };

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
                return false;

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch
        {
            // The user declined the prompt, or PowerShell is unavailable.
            return false;
        }
    }
}
