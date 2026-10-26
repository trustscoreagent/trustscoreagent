using System.Net;
using System.Net.Sockets;

namespace TrustScore.Api.Middleware;

/// <summary>
/// The caller identity per-IP limits are keyed on. An IPv4 address is one caller. An IPv6
/// address is not: a single subscriber is routinely given a /56 or a /64, so keying on the full
/// address would hand anyone billions of fresh buckets. IPv6 callers are grouped by their /56.
/// </summary>
public static class ClientAddress
{
    internal const int Ipv6PrefixBits = 56;

    public static string Bucket(IPAddress? address)
    {
        if (address is null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();

        var bytes = address.GetAddressBytes();
        const int keep = Ipv6PrefixBits / 8;
        for (var i = keep; i < bytes.Length; i++)
            bytes[i] = 0;
        return $"{new IPAddress(bytes)}/{Ipv6PrefixBits}";
    }
}
