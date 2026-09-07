using System.Security.Claims;
using System.Text.Json;
using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Dtos.AdminSecurity;
using CompetitionManagementSystem.Models;
using CompetitionManagementSystem.Options;
using CompetitionManagementSystem.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CompetitionManagementSystem.Controllers;

[ApiController]
[Route("api/admin/security")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = SystemRoles.SystemAdmin)]
public sealed class AdminSecurityController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IClientIpResolver _ipResolver;
    private readonly IOptionsMonitor<AdminSecurityOptions> _options;
    private readonly IHostEnvironment _env;

    public AdminSecurityController(
        ApplicationDbContext db,
        IClientIpResolver ipResolver,
        IOptionsMonitor<AdminSecurityOptions> options,
        IHostEnvironment env)
    {
        _db = db;
        _ipResolver = ipResolver;
        _options = options;
        _env = env;
    }

    [HttpGet("ip-ranges")]
    public async Task<ActionResult<IReadOnlyList<AdminAllowedIpRange>>> ListIpRanges()
    {
        var rows = await _db.AdminAllowedIpRanges.AsNoTracking().OrderBy(r => r.IpRange).ToListAsync();
        return Ok(rows);
    }

    [HttpPost("ip-ranges")]
    public async Task<ActionResult<AdminAllowedIpRange>> CreateIpRange(CreateIpRangeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IpRange))
            return BadRequest(new { error = "IpRange is required." });

        var normalized = request.IpRange.Trim();
        if (!IpCidr.TryParse(normalized, out _))
            return BadRequest(new { error = $"IpRange '{normalized}' is not a valid IP or CIDR." });

        var duplicate = await _db.AdminAllowedIpRanges.AnyAsync(r => r.IpRange == normalized);
        if (duplicate)
            return Conflict(new { error = $"IpRange '{normalized}' already exists." });

        var row = new AdminAllowedIpRange
        {
            IpRange = normalized,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            IsEnabled = request.IsEnabled,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)
        };
        _db.AdminAllowedIpRanges.Add(row);
        AddAudit("AdminAllowedIpRange", row.Id.ToString(), "AdminIpRangeCreated", null, new { row.IpRange, row.IsEnabled, row.Description });
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(ListIpRanges), new { id = row.Id }, row);
    }

    [HttpPut("ip-ranges/{id:guid}")]
    public async Task<IActionResult> UpdateIpRange(Guid id, UpdateIpRangeRequest request)
    {
        var row = await _db.AdminAllowedIpRanges.FirstOrDefaultAsync(r => r.Id == id);
        if (row is null) return NotFound();

        var oldSnapshot = new { row.IpRange, row.IsEnabled, row.Description, row.Notes };

        if (request.IpRange is not null)
        {
            var normalized = request.IpRange.Trim();
            if (!IpCidr.TryParse(normalized, out _))
                return BadRequest(new { error = $"IpRange '{normalized}' is not a valid IP or CIDR." });
            if (!string.Equals(normalized, row.IpRange, StringComparison.OrdinalIgnoreCase))
            {
                var duplicate = await _db.AdminAllowedIpRanges.AnyAsync(r => r.IpRange == normalized && r.Id != id);
                if (duplicate)
                    return Conflict(new { error = $"IpRange '{normalized}' already exists." });
                row.IpRange = normalized;
            }
        }
        if (request.Description is not null)
            row.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (request.IsEnabled.HasValue)
            row.IsEnabled = request.IsEnabled.Value;
        if (request.Notes is not null)
            row.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();

        row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        AddAudit("AdminAllowedIpRange", row.Id.ToString(), "AdminIpRangeUpdated", oldSnapshot, new { row.IpRange, row.IsEnabled, row.Description, row.Notes });
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("ip-ranges/{id:guid}")]
    public async Task<IActionResult> DeleteIpRange(Guid id)
    {
        var row = await _db.AdminAllowedIpRanges.FirstOrDefaultAsync(r => r.Id == id);
        if (row is null) return NotFound();

        _db.AdminAllowedIpRanges.Remove(row);
        AddAudit("AdminAllowedIpRange", row.Id.ToString(), "AdminIpRangeDeleted", new { row.IpRange, row.IsEnabled, row.Description }, null);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("ip/current")]
    public async Task<IActionResult> CurrentIp()
    {
        var resolved = _ipResolver.Resolve(HttpContext);
        bool isAllowed = false;
        string? matchedRange = null;
        if (resolved.ClientIp is not null && resolved.RejectionReason is null)
        {
            var ranges = await _db.AdminAllowedIpRanges.AsNoTracking()
                .Where(r => r.IsEnabled).Select(r => r.IpRange).ToListAsync();
            foreach (var spec in ranges)
            {
                if (IpCidr.TryParse(spec, out var parsed) && IpCidr.Contains(parsed, resolved.ClientIp))
                {
                    isAllowed = true;
                    matchedRange = spec;
                    break;
                }
            }
        }

        return Ok(new
        {
            remoteIp = resolved.RemoteIp?.ToString(),
            cfConnectingIpHeader = resolved.CfConnectingIpHeader,
            resolvedClientIp = resolved.ClientIp?.ToString(),
            rejectionReason = resolved.RejectionReason,
            isInAllowList = isAllowed,
            matchedRange,
            developmentLocalhostBypassActive = _env.IsDevelopment()
                && _options.CurrentValue.AllowLocalhostInDevelopment
                && resolved.ClientIp is { } ip && System.Net.IPAddress.IsLoopback(ip)
        });
    }

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var s = await GetOrCreateSettingsAsync();
        return Ok(new
        {
            s.EnableAdminIpRestriction,
            s.RequireMfaForAdmins,
            s.UpdatedAtUtc,
            s.UpdatedByUserId,
            emergencyDisableAdminIpRestriction = _options.CurrentValue.EmergencyDisableAdminIpRestriction
        });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings(UpdateAdminSecuritySettingsRequest request)
    {
        var s = await GetOrCreateSettingsAsync();
        var oldSnapshot = new { s.EnableAdminIpRestriction, s.RequireMfaForAdmins };

        // Safety check: if enabling IP restriction, ensure the caller's current IP matches at
        // least one enabled range — otherwise the admin would lock themselves out instantly.
        if (request.EnableAdminIpRestriction == true && !s.EnableAdminIpRestriction)
        {
            var resolved = _ipResolver.Resolve(HttpContext);
            if (resolved.RejectionReason is not null || resolved.ClientIp is null)
                return BadRequest(new { error = $"Cannot enable: cannot resolve a trusted client IP ({resolved.RejectionReason ?? "no client IP"})." });

            var devLocalhostBypass = _env.IsDevelopment()
                && _options.CurrentValue.AllowLocalhostInDevelopment
                && System.Net.IPAddress.IsLoopback(resolved.ClientIp);

            if (!devLocalhostBypass)
            {
                var enabledRanges = await _db.AdminAllowedIpRanges.AsNoTracking()
                    .Where(r => r.IsEnabled).Select(r => r.IpRange).ToListAsync();
                var parsed = IpCidr.ParseAll(enabledRanges);
                if (!IpCidr.AnyContains(parsed, resolved.ClientIp))
                {
                    return BadRequest(new
                    {
                        error = "Cannot enable AdminIpRestriction: your current resolved IP "
                                + resolved.ClientIp + " is not in any enabled AdminAllowedIpRange. "
                                + "Add it first via POST /api/admin/security/ip-ranges, then re-enable."
                    });
                }
            }
        }

        if (request.EnableAdminIpRestriction.HasValue)
            s.EnableAdminIpRestriction = request.EnableAdminIpRestriction.Value;
        if (request.RequireMfaForAdmins.HasValue)
            s.RequireMfaForAdmins = request.RequireMfaForAdmins.Value;

        s.UpdatedAtUtc = DateTime.UtcNow;
        s.UpdatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        AddAudit("AdminSecuritySetting", s.Id.ToString(), "AdminSecuritySettingsUpdated", oldSnapshot, new { s.EnableAdminIpRestriction, s.RequireMfaForAdmins });
        await _db.SaveChangesAsync();
        return Ok(new { s.EnableAdminIpRestriction, s.RequireMfaForAdmins, s.UpdatedAtUtc, s.UpdatedByUserId });
    }

    private async Task<AdminSecuritySetting> GetOrCreateSettingsAsync()
    {
        var s = await _db.AdminSecuritySettings.FirstOrDefaultAsync(x => x.Id == 1);
        if (s is null)
        {
            s = new AdminSecuritySetting { Id = 1, EnableAdminIpRestriction = false, RequireMfaForAdmins = false };
            _db.AdminSecuritySettings.Add(s);
            await _db.SaveChangesAsync();
        }
        return s;
    }

    private void AddAudit(string entityType, string entityId, string action, object? oldValue, object? newValue)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            OldValueJson = oldValue is null ? null : JsonSerializer.Serialize(oldValue),
            NewValueJson = newValue is null ? null : JsonSerializer.Serialize(newValue),
            UserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
            UserEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name,
            UserRole = string.Join(',', User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value)),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAtUtc = DateTime.UtcNow
        });
    }
}
