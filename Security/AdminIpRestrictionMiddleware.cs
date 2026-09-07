using System.Net;
using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CompetitionManagementSystem.Security;

/// <summary>
/// Restricts endpoints requiring the SystemAdmin role to a DB-managed allow-list of IP ranges.
/// Cloudflare-aware via <see cref="IClientIpResolver"/>. Emergency rollback via
/// AdminSecurity:EmergencyDisableAdminIpRestriction.
/// </summary>
public sealed class AdminIpRestrictionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<AdminSecurityOptions> _options;
    private readonly IHostEnvironment _env;
    private readonly ILogger<AdminIpRestrictionMiddleware> _logger;

    public AdminIpRestrictionMiddleware(
        RequestDelegate next,
        IOptionsMonitor<AdminSecurityOptions> options,
        IHostEnvironment env,
        ILogger<AdminIpRestrictionMiddleware> logger)
    {
        _next = next;
        _options = options;
        _env = env;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IClientIpResolver ipResolver,
        ApplicationDbContext db)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null || !EndpointRequiresAdmin(endpoint))
        {
            await _next(context);
            return;
        }

        var opts = _options.CurrentValue;
        if (opts.EmergencyDisableAdminIpRestriction)
        {
            _logger.LogWarning(
                "Admin IP restriction bypassed via EmergencyDisableAdminIpRestriction. Path={Path}",
                context.Request.Path);
            await _next(context);
            return;
        }

        var settings = await db.AdminSecuritySettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1);
        if (settings is null || !settings.EnableAdminIpRestriction)
        {
            await _next(context);
            return;
        }

        var resolved = ipResolver.Resolve(context);
        if (resolved.RejectionReason is not null || resolved.ClientIp is null)
        {
            await BlockAsync(context, resolved, resolved.RejectionReason ?? "Unable to resolve client IP");
            return;
        }

        if (opts.AllowLocalhostInDevelopment && _env.IsDevelopment() && IPAddress.IsLoopback(resolved.ClientIp))
        {
            await _next(context);
            return;
        }

        var enabledRanges = await db.AdminAllowedIpRanges
            .AsNoTracking()
            .Where(r => r.IsEnabled)
            .Select(r => r.IpRange)
            .ToListAsync();
        var parsed = IpCidr.ParseAll(enabledRanges, _logger);

        if (!IpCidr.AnyContains(parsed, resolved.ClientIp))
        {
            await BlockAsync(context, resolved, "Client IP not in any enabled AdminAllowedIpRanges row");
            return;
        }

        // Best-effort: record LastUsedAtUtc on the matching row (fire-and-forget; never blocks the request).
        _ = TouchLastUsedAsync(db, enabledRanges, resolved.ClientIp);

        await _next(context);
    }

    private static bool EndpointRequiresAdmin(Endpoint endpoint)
    {
        var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        if (authorize.Count == 0) return false;

        foreach (var a in authorize)
        {
            if (string.IsNullOrEmpty(a.Roles)) continue;
            foreach (var role in a.Roles.Split(','))
            {
                if (string.Equals(role.Trim(), SystemRoles.SystemAdmin, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static async Task TouchLastUsedAsync(ApplicationDbContext db, List<string> enabledRanges, IPAddress clientIp)
    {
        try
        {
            foreach (var spec in enabledRanges)
            {
                if (!IpCidr.TryParse(spec, out var parsed)) continue;
                if (!IpCidr.Contains(parsed, clientIp)) continue;
                var now = DateTime.UtcNow;
                await db.AdminAllowedIpRanges
                    .Where(r => r.IpRange == spec)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastUsedAtUtc, now));
                break;
            }
        }
        catch
        {
            // LastUsed bookkeeping must never break a request.
        }
    }

    private async Task BlockAsync(HttpContext context, ResolvedClientIp resolved, string reason)
    {
        _logger.LogWarning(
            "Admin IP blocked. Path={Path} RemoteIp={Remote} CFConnectingIp={CFIP} ResolvedClientIp={Client} Reason={Reason}",
            context.Request.Path,
            resolved.RemoteIp?.ToString() ?? context.Connection.RemoteIpAddress?.ToString() ?? "(none)",
            resolved.CfConnectingIpHeader ?? "(none)",
            resolved.ClientIp?.ToString() ?? "(unresolved)",
            reason);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"Forbidden.\"}");
    }
}
