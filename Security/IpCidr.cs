using System.Net;
using System.Net.Sockets;

namespace CompetitionManagementSystem.Security;

/// <summary>
/// Minimal CIDR helper. Accepts "1.2.3.4", "1.2.3.0/24", "::1", "2001:db8::/32".
/// </summary>
public static class IpCidr
{
    public readonly record struct ParsedRange(IPAddress BaseAddress, int PrefixLength);

    public static bool TryParse(string spec, out ParsedRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(spec)) return false;

        string ipPart;
        string? prefixPart;
        var slash = spec.IndexOf('/');
        if (slash >= 0)
        {
            ipPart = spec.Substring(0, slash).Trim();
            prefixPart = spec.Substring(slash + 1).Trim();
        }
        else
        {
            ipPart = spec.Trim();
            prefixPart = null;
        }

        if (!IPAddress.TryParse(ipPart, out var addr)) return false;
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();

        var maxPrefix = addr.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = maxPrefix;
        if (prefixPart != null)
        {
            if (!int.TryParse(prefixPart, out prefix) || prefix < 0 || prefix > maxPrefix)
                return false;
        }

        range = new ParsedRange(addr, prefix);
        return true;
    }

    public static bool Contains(ParsedRange range, IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (range.BaseAddress.AddressFamily != address.AddressFamily) return false;

        var rb = range.BaseAddress.GetAddressBytes();
        var ab = address.GetAddressBytes();
        var fullBytes = range.PrefixLength / 8;
        var remainingBits = range.PrefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (rb[i] != ab[i]) return false;
        }
        if (remainingBits == 0) return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (rb[fullBytes] & mask) == (ab[fullBytes] & mask);
    }

    public static bool AnyContains(IEnumerable<ParsedRange> ranges, IPAddress address)
    {
        foreach (var r in ranges)
        {
            if (Contains(r, address)) return true;
        }
        return false;
    }

    public static List<ParsedRange> ParseAll(IEnumerable<string>? specs, ILogger? logger = null)
    {
        var result = new List<ParsedRange>();
        if (specs is null) return result;
        foreach (var s in specs)
        {
            if (TryParse(s, out var r))
                result.Add(r);
            else
                logger?.LogWarning("AdminSecurity: ignoring invalid CIDR/IP spec '{Spec}'.", s);
        }
        return result;
    }
}
