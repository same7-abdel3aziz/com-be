using System.Net;
using CompetitionManagementSystem.Options;
using Microsoft.Extensions.Options;

namespace CompetitionManagementSystem.Security;

public sealed record ResolvedClientIp(
    IPAddress? RemoteIp,
    string? CfConnectingIpHeader,
    IPAddress? ClientIp,
    string? RejectionReason);

public interface IClientIpResolver
{
    ResolvedClientIp Resolve(HttpContext context);
}

/// <summary>
/// Cloudflare-aware client IP resolver. CF-Connecting-IP is trusted only when the immediate
/// RemoteIpAddress is inside a configured Cloudflare range; otherwise the header is treated
/// as a spoof attempt and a rejection reason is returned.
/// </summary>
public sealed class ClientIpResolver : IClientIpResolver
{
    private readonly IOptionsMonitor<AdminSecurityOptions> _options;

    public ClientIpResolver(IOptionsMonitor<AdminSecurityOptions> options)
    {
        _options = options;
    }

    public ResolvedClientIp Resolve(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
            return new ResolvedClientIp(null, null, null, "No RemoteIpAddress on the connection");

        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();

        var cfHeaderValue = context.Request.Headers["CF-Connecting-IP"].ToString();
        var cfHeader = string.IsNullOrEmpty(cfHeaderValue) ? null : cfHeaderValue;

        if (cfHeader is null)
            return new ResolvedClientIp(remote, null, remote, null);

        var cloudflareRanges = IpCidr.ParseAll(_options.CurrentValue.AllowedCloudflareIpRanges);
        if (IpCidr.AnyContains(cloudflareRanges, remote))
        {
            if (!IPAddress.TryParse(cfHeader, out var cfIp))
                return new ResolvedClientIp(remote, cfHeader, remote, "CF-Connecting-IP header is malformed");
            if (cfIp.IsIPv4MappedToIPv6) cfIp = cfIp.MapToIPv4();
            return new ResolvedClientIp(remote, cfHeader, cfIp, null);
        }

        return new ResolvedClientIp(
            remote,
            cfHeader,
            remote,
            "CF-Connecting-IP header present but RemoteIpAddress is not in AllowedCloudflareIpRanges");
    }
}
