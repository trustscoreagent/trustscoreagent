using System.Net;
using System.Net.Sockets;

namespace TrustScore.Core.Models;

/// <summary>
/// Whether a service id names something on the public internet. A rating on
/// <c>127.0.0.1/control</c> or <c>printer.local</c> describes a machine only its rater can reach:
/// it cannot inform anyone else, so it is refused rather than listed.
/// </summary>
public static class PublicHost
{
    // Names that by definition never resolve on the public internet (RFC 6761, RFC 8375, mDNS,
    // and the .internal TLD reserved for private use).
    private static readonly string[] ReservedSuffixes = [".localhost", ".local", ".internal", ".home.arpa"];

    /// <summary>
    /// True when the host of a normalized service id (see <see cref="ServiceIdentifier.Normalize"/>)
    /// is a public name or a public IP address.
    /// </summary>
    public static bool IsPublic(string normalizedId)
    {
        var host = ServiceIdentifier.ExtractProvider(normalizedId).TrimEnd('.');
        if (host.Length == 0) return false;

        // IPv6 literals keep their brackets through Uri.Host.
        var literal = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        if (IPAddress.TryParse(literal, out var ip)) return !IsReserved(ip);

        if (!host.Contains('.')) return false; // "localhost", "intranet", "my-server"
        if (host == "localhost" || ReservedSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)))
            return false;
        return true;
    }

    /// <summary>
    /// Loopback, private, link-local (including cloud metadata), CGNAT, multicast and reserved
    /// ranges, plus NAT64 forms that could map back into them.
    /// </summary>
    public static bool IsReserved(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return true;                                   // 0.0.0.0/8
            if (b[0] == 10) return true;                                  // 10.0.0.0/8
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;    // 100.64.0.0/10 CGNAT
            if (b[0] == 127) return true;                                 // loopback
            if (b[0] == 169 && b[1] == 254) return true;                 // link-local + cloud metadata
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;     // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;                 // 192.168.0.0/16
            if (b[0] >= 224) return true;                                 // multicast + reserved
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback)) return true;
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xfe) == 0xfc) return true;                       // fc00::/7 unique local
            // 64:ff9b::/96 NAT64: block so 64:ff9b::a9fe:a9fe (→169.254.169.254) can't reach metadata.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b &&
                b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 &&
                b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0) return true;
            return false;
        }

        return true; // unknown address family → block
    }
}
