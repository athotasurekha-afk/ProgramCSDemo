using System.Net;
using System.Net.Sockets;

namespace ProgramCSDemo.Services;

internal static class UrlDestinationPolicy
{
    private static readonly (uint Network, uint Mask)[] NonPublicIpv4Ranges =
    [
        Range("0.0.0.0", 8),
        Range("10.0.0.0", 8),
        Range("100.64.0.0", 10),
        Range("127.0.0.0", 8),
        Range("169.254.0.0", 16),
        Range("172.16.0.0", 12),
        Range("192.0.0.0", 24),
        Range("192.0.2.0", 24),
        Range("192.88.99.0", 24),
        Range("192.168.0.0", 16),
        Range("198.18.0.0", 15),
        Range("198.51.100.0", 24),
        Range("203.0.113.0", 24),
        Range("224.0.0.0", 4),
        Range("240.0.0.0", 4)
    ];

    public static bool IsAllowed(Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var host = uri.IdnHost;
        if (host.Length > 1 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        host = host.TrimEnd('.');
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return IPAddress.TryParse(host, out var literalAddress)
                && IsPublicAddress(literalAddress);
        }

        if (host.Length == 0
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.'))
        {
            return false;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return true;
        }

        return IsPublicAddress(address);
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicAddress(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            var value = ((uint)bytes[0] << 24)
                | ((uint)bytes[1] << 16)
                | ((uint)bytes[2] << 8)
                | bytes[3];
            return !NonPublicIpv4Ranges.Any(range => (value & range.Mask) == range.Network);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6
            || address.ScopeId != 0)
        {
            return false;
        }

        var ipv6 = address.GetAddressBytes();
        var isGlobalUnicast = (ipv6[0] & 0xE0) == 0x20;
        var isDocumentationAddress = ipv6[0] == 0x20
            && ipv6[1] == 0x01
            && ipv6[2] == 0x0D
            && ipv6[3] == 0xB8;
        var isTransitionAddress = ipv6[0] == 0x20
            && ipv6[1] == 0x01
            && ipv6[2] == 0x00
            && ipv6[3] == 0x00;
        var isBenchmarkAddress = ipv6[0] == 0x20
            && ipv6[1] == 0x01
            && ipv6[2] == 0x00
            && ipv6[3] == 0x02;
        var isSixToFourAddress = ipv6[0] == 0x20 && ipv6[1] == 0x02;
        return isGlobalUnicast
            && !isDocumentationAddress
            && !isTransitionAddress
            && !isBenchmarkAddress
            && !isSixToFourAddress;
    }

    private static (uint Network, uint Mask) Range(string address, int prefixLength)
    {
        var bytes = IPAddress.Parse(address).GetAddressBytes();
        var network = ((uint)bytes[0] << 24)
            | ((uint)bytes[1] << 16)
            | ((uint)bytes[2] << 8)
            | bytes[3];
        var mask = uint.MaxValue << (32 - prefixLength);
        return (network, mask);
    }
}
