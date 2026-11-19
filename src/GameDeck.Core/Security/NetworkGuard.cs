using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace GameDeck.Core.Security;

/// <summary>Keeps the control server strictly on the local network.</summary>
public static class NetworkGuard
{
    /// <summary>True for loopback, RFC 1918 private, link-local, and IPv6 ULA/link-local addresses.</summary>
    public static bool IsLocalNetworkAddress(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    /// <summary>
    /// DNS-rebinding defence: a malicious web page can point its own hostname at this PC, but it
    /// cannot make the browser send a Host header that is a bare IP, localhost or this machine's name.
    /// </summary>
    public static bool IsAllowedHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var h = host.Trim('[', ']');
        if (IPAddress.TryParse(h, out _)) return true;
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        var machine = Environment.MachineName;
        return h.Equals(machine, StringComparison.OrdinalIgnoreCase)
            || h.Equals(machine + ".local", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>IPv4 LAN addresses of this PC, best guess (has a gateway) first.</summary>
    public static IReadOnlyList<IPAddress> GetLanAddresses()
    {
        var results = new List<(IPAddress Address, int Score)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            var looksVirtual = nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                || nic.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase);

            foreach (var ua in props.UnicastAddresses)
            {
                var a = ua.Address;
                if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                if (!IsLocalNetworkAddress(a) || a.GetAddressBytes()[0] == 169) continue;
                var score = (hasGateway ? 2 : 0) + (looksVirtual ? 0 : 1);
                results.Add((a, score));
            }
        }
        return results.OrderByDescending(r => r.Score).Select(r => r.Address).Distinct().ToList();
    }
}
