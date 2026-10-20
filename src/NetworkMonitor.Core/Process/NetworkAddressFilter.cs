using System.Net;

namespace NetworkMonitor.Core.Process;

/// <summary>
/// Decides whether a remote address belongs to "local noise" that should not enter the
/// per-process ledger: loopback, RFC1918 private ranges, link-local (APIPA / fe80::),
/// unique-local IPv6, unspecified, broadcast and multicast/any-local groups.
/// IPv4-mapped IPv6 addresses are unwrapped and judged by the IPv4 rules.
/// </summary>
internal static class NetworkAddressFilter
{
    /// <summary>Returns true when traffic to/from <paramref name="address"/> is local noise.</summary>
    public static bool IsNoise(ReadOnlySpan<byte> address)
    {
        if (address.Length == 4)
            return IsNoiseV4(address);

        if (address.Length == 16)
            return IsNoiseV6(address);

        return true;
    }

    public static bool IsNoise(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        Span<byte> buffer = stackalloc byte[16];
        return address.TryWriteBytes(buffer, out int written) && IsNoise(buffer[..written]);
    }

    private static bool IsNoiseV4(ReadOnlySpan<byte> a)
    {
        byte b0 = a[0];
        byte b1 = a[1];

        if (b0 == 0) return true;                        // 0.0.0.0/8  (unspecified)
        if (b0 == 127) return true;                      // 127.0.0.0/8 (loopback)
        if (b0 == 10) return true;                       // 10.0.0.0/8
        if (b0 == 172 && (b1 & 0xF0) == 16) return true;  // 172.16.0.0/12
        if (b0 == 192 && b1 == 168) return true;         // 192.168.0.0/16
        if (b0 == 169 && b1 == 254) return true;         // 169.254.0.0/16 (APIPA)
        if (b0 >= 224) return true;                      // 224.0.0.0/4 multicast + 240/4 + broadcast

        return false;
    }

    private static bool IsNoiseV6(ReadOnlySpan<byte> a)
    {
        // ::ffff:a.b.c.d -> judge by the embedded IPv4 address.
        if (a[..10].IndexOfAnyExcept((byte)0) < 0 && a[10] == 0xFF && a[11] == 0xFF)
            return IsNoiseV4(a[12..]);

        bool allZeroExceptLast = a[..15].IndexOfAnyExcept((byte)0) < 0;

        if (allZeroExceptLast && a[15] == 0) return true;  // ::
        if (allZeroExceptLast && a[15] == 1) return true;  // ::1 loopback

        if (a[0] == 0xFE && (a[1] & 0xC0) == 0x80) return true;  // fe80::/10 link-local
        if (a[0] == 0xFE && (a[1] & 0xC0) == 0xC0) return true;  // fec0::/10 site-local (deprecated)
        if ((a[0] & 0xFE) == 0xFC) return true;                  // fc00::/7 unique-local
        if (a[0] == 0xFF) return true;                           // ff00::/8 multicast

        return false;
    }

    public static string Describe(ReadOnlySpan<byte> address)
    {
        try
        {
            return new IPAddress(address).ToString();
        }
        catch
        {
            return "?";
        }
    }
}
