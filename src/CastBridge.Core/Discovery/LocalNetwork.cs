using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CastBridge.Core.Discovery;

public sealed record LocalSubnet(string InterfaceName, string Description, IPAddress LocalAddress, IPAddress Network, int PrefixLength, bool IsVirtual)
{
    /// <summary>Addresses of the hosts in this subnet, excluding the network and broadcast address.</summary>
    public IEnumerable<IPAddress> HostAddresses(int max = 254)
    {
        if (Network.AddressFamily != AddressFamily.InterNetwork || PrefixLength < 24)
        {
            // Only sweep /24 (or smaller) ranges; anything bigger would take far too long.
            yield break;
        }

        var bytes = Network.GetAddressBytes();
        var count = PrefixLength == 24 ? 254 : 1 << (32 - PrefixLength);
        for (var i = 1; i <= Math.Min(count, max); i++)
        {
            var candidate = new byte[4];
            bytes.CopyTo(candidate, 0);
            candidate[3] = (byte)i;
            yield return new IPAddress(candidate);
        }
    }
}

public static class LocalNetwork
{
    private static readonly string[] VirtualKeywords =
    {
        "vpn", "wireguard", "openvpn", "tap-", "tap0", "tun", "tailscale", "zerotier", "nordlynx",
        "cisco anyconnect", "forticlient", "proton", "wintun", "hamachi", "hyper-v", "vmware", "virtualbox",
        "loopback", "docker", "bluetooth",
    };

    /// <summary>Every usable IPv4 subnet this machine is on.</summary>
    public static IReadOnlyList<LocalSubnet> GetSubnets(bool includeVirtual = false)
    {
        var result = new List<LocalSubnet>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var isVirtual = nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel || LooksVirtual(nic.Description) || LooksVirtual(nic.Name);
            if (isVirtual && !includeVirtual)
                continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                var prefix = unicast.PrefixLength <= 0 ? 24 : unicast.PrefixLength;
                var network = GetNetworkAddress(unicast.Address, prefix);
                result.Add(new LocalSubnet(nic.Name, nic.Description, unicast.Address, network, prefix, isVirtual));
            }
        }

        return result;
    }

    public static bool LooksVirtual(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return VirtualKeywords.Any(lower.Contains);
    }

    public static IPAddress GetNetworkAddress(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var mask = prefixLength >= 32 ? uint.MaxValue : ~(uint.MaxValue >> prefixLength);
        var value = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        var network = value & mask;
        return new IPAddress(new[] { (byte)(network >> 24), (byte)(network >> 16), (byte)(network >> 8), (byte)network });
    }

    /// <summary>True when both addresses sit in the same /24, which is what Cast discovery requires.</summary>
    public static bool IsSameSubnet(IPAddress a, IPAddress b, int prefixLength = 24)
    {
        if (a.AddressFamily != b.AddressFamily)
            return false;

        return GetNetworkAddress(a, prefixLength).Equals(GetNetworkAddress(b, prefixLength));
    }

    /// <summary>Returns the local address that can reach <paramref name="target"/>, or null when none can.</summary>
    public static IPAddress? FindLocalAddressFor(IPAddress target)
    {
        foreach (var subnet in GetSubnets(includeVirtual: true))
        {
            if (IsSameSubnet(subnet.LocalAddress, target, Math.Min(subnet.PrefixLength, 24)))
                return subnet.LocalAddress;
        }

        return null;
    }
}
