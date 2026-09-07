using System.Security.Claims;
using System.Text.Json;
using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Dtos.Users;
using CompetitionManagementSystem.Models;
using CompetitionManagementSystem.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CompetitionManagementSystem.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = SystemRoles.SystemAdmin)]
public sealed class UsersController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _db;

    public UsersController(UserManager<ApplicationUser> userManager, ApplicationDbContext db)
    {
        _userManager = userManager;
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserResponse>>> GetAll()
    {
        var users = _userManager.Users.OrderBy(u => u.Email).ToList();
        var response = new List<UserResponse>();
        foreach (var user in users)
        {
            var roles = await _userManager.GetRolesAsync(user);
            response.Add(new UserResponse(user.Id, user.Email ?? "", user.FullName, user.IsActive, roles.ToList()));
        }
        return Ok(response);
    }

    [HttpPost]
    public async Task<ActionResult<UserResponse>> Create(CreateUserRequest request)
    {
        if (!SystemRoles.All.Contains(request.Role))
            return BadRequest("Invalid role.");

        var policyErrors = PasswordPolicy.Validate(request.Password);
        if (policyErrors.Count > 0)
            return BadRequest(policyErrors);

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            EmailConfirmed = true,
            FullName = request.FullName,
            IsActive = true
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(result.Errors.Select(e => e.Description));

        // Defense in depth: ensure lockout is enabled regardless of historical defaults.
        await _userManager.SetLockoutEnabledAsync(user, true);

        await _userManager.AddToRoleAsync(user, request.Role);
        AddAudit("User", user.Id, "UserCreated", null, new { user.Email, user.FullName, Role = request.Role });
        await _db.SaveChangesAsync();

        return Ok(new UserResponse(user.Id, user.Email!, user.FullName, user.IsActive, [request.Role]));
    }

    [HttpPut("{id}/role")]
    public async Task<IActionResult> ChangeRole(string id, ChangeUserRoleRequest request)
    {
        if (!SystemRoles.All.Contains(request.Role))
            return BadRequest("Invalid role.");

        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
            return NotFound();

        var currentRoles = await _userManager.GetRolesAsync(user);
        await _userManager.RemoveFromRolesAsync(user, currentRoles);
        await _userManager.AddToRoleAsync(user, request.Role);
        AddAudit("User", user.Id, "UserRoleChanged", new { Roles = currentRoles }, new { Role = request.Role });
        await _db.SaveChangesAsync();

        return NoContent();
    }

    [HttpPut("{id}/enable")]
    public async Task<IActionResult> Enable(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
            return NotFound();
        var oldValue = new { user.IsActive };
        user.IsActive = true;
        await _userManager.UpdateAsync(user);
        AddAudit("User", user.Id, "UserEnabled", oldValue, new { user.IsActive });
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id}/disable")]
    public async Task<IActionResult> Disable(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
            return NotFound();
        var oldValue = new { user.IsActive };
        user.IsActive = false;
        await _userManager.UpdateAsync(user);
        AddAudit("User", user.Id, "UserDisabled", oldValue, new { user.IsActive });
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Admin-only password reset for another user. Does not require the current password.
    /// Uses GeneratePasswordResetTokenAsync + ResetPasswordAsync so the new password is
    /// validated against the configured policy (length, letter, digit, special character).
    /// </summary>
    [HttpPost("reset-password")]
    public async Task<IActionResult> AdminResetPassword(AdminResetPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserId) && string.IsNullOrWhiteSpace(request.Email))
            return BadRequest("Either UserId or Email is required.");

        if (string.IsNullOrWhiteSpace(request.NewPassword))
            return BadRequest("NewPassword is required.");

        var policyErrors = PasswordPolicy.Validate(request.NewPassword);
        if (policyErrors.Count > 0)
            return BadRequest(policyErrors);

        var user = !string.IsNullOrWhiteSpace(request.UserId)
            ? await _userManager.FindByIdAsync(request.UserId)
            : await _userManager.FindByEmailAsync(request.Email!);

        if (user is null)
            return NotFound("User not found.");

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(result.Errors.Select(e => e.Description));

        // Reset failed-attempt counter so the user is not locked out after a password reset.
        await _userManager.ResetAccessFailedCountAsync(user);

        AddAudit("User", user.Id, "UserPasswordResetByAdmin", null, new { user.Email });
        await _db.SaveChangesAsync();

        return Ok(new { message = "Password updated." });
    }

    /// <summary>
    /// Admin-only MFA reset: clears the target user's TOTP secret, recovery codes, and TotpEnabled flag.
    /// Next admin login will force a fresh enrollment.
    /// </summary>
    [HttpPost("{id}/mfa/reset")]
    public async Task<IActionResult> ResetMfa(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null) return NotFound();

        user.TotpEnabled = false;
        user.TotpSecretEncrypted = null;
        user.TotpRecoveryCodesEncrypted = null;
        user.TotpEnabledAtUtc = null;
        user.TotpLastVerifiedAtUtc = null;
        await _userManager.UpdateAsync(user);

        AddAudit("User", user.Id, "AdminMfaReset", null, new { user.Email });
        await _db.SaveChangesAsync();
        return Ok(new { message = "MFA reset for user." });
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
            IpAddress = Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded) ? forwarded.ToString().Split(',')[0].Trim() : HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAtUtc = DateTime.UtcNow
        });
    }
}