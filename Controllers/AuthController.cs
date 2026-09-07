using System.Net;
using System.Security.Claims;
using System.Text.Json;
using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Dtos.Auth;
using CompetitionManagementSystem.Dtos.Mfa;
using CompetitionManagementSystem.Models;
using CompetitionManagementSystem.Options;
using CompetitionManagementSystem.Security;
using CompetitionManagementSystem.Services;
using CompetitionManagementSystem.Services.Mfa;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CompetitionManagementSystem.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITokenService _tokenService;
    private readonly IConfiguration _configuration;
    private readonly ApplicationDbContext _db;
    private readonly IMfaTicketService _mfaTickets;
    private readonly ITotpService _totp;
    private readonly IOptions<JwtSettings> _jwt;
    private readonly IClientIpResolver _ipResolver;
    private readonly IOptionsMonitor<AdminSecurityOptions> _adminOptions;
    private readonly IHostEnvironment _env;

    public AuthController(
        UserManager<ApplicationUser> userManager,
        ITokenService tokenService,
        IConfiguration configuration,
        ApplicationDbContext db,
        IMfaTicketService mfaTickets,
        ITotpService totp,
        IOptions<JwtSettings> jwt,
        IClientIpResolver ipResolver,
        IOptionsMonitor<AdminSecurityOptions> adminOptions,
        IHostEnvironment env)
    {
        _userManager = userManager;
        _tokenService = tokenService;
        _configuration = configuration;
        _db = db;
        _mfaTickets = mfaTickets;
        _totp = totp;
        _jwt = jwt;
        _ipResolver = ipResolver;
        _adminOptions = adminOptions;
        _env = env;
    }

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request)
    {
        if (!_configuration.GetValue<bool>("PublicRegistration:Enabled"))
            return BadRequest("Public registration is disabled.");

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

        await _userManager.SetLockoutEnabledAsync(user, true);

        await _userManager.AddToRoleAsync(user, SystemRoles.Judge);
        AddAnonymousAudit("User", user.Id, "PublicJudgeRegistered", new { user.Email, user.FullName, Role = SystemRoles.Judge });
        await _db.SaveChangesAsync();

        var (token, expires) = await _tokenService.CreateTokenAsync(user);
        return Ok(new AuthResponse(token, expires, user.Id, user.Email!, user.FullName, [SystemRoles.Judge]));
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);
        if (user is null || !user.IsActive)
            return BadRequest(new { error = new { message = "Invalid email or password." } });

        if (await _userManager.IsLockedOutAsync(user))
        {
            AddAnonymousAudit("User", user.Id, "UserLoginBlockedLockedOut", new { user.Email });
            await _db.SaveChangesAsync();
            return StatusCode(StatusCodes.Status423Locked,
                "Account locked. Please try again after 1 minute.");
        }

        var valid = await _userManager.CheckPasswordAsync(user, request.Password);
        if (!valid)
        {
            await _userManager.AccessFailedAsync(user);
            var failedCount = await _userManager.GetAccessFailedCountAsync(user);
            var isLockedNow = await _userManager.IsLockedOutAsync(user);
            if (!isLockedNow && failedCount >= 3)
            {
                await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(1));
                isLockedNow = true;
            }
            if (isLockedNow)
            {
                AddAnonymousAudit("User", user.Id, "UserLockedOut", new { user.Email });
                await _db.SaveChangesAsync();
                return StatusCode(StatusCodes.Status423Locked,
                    "Account locked. Please try again after 1 minute.");
            }
            await _db.SaveChangesAsync();
            return BadRequest(new { error = new { message = "Invalid email or password." } });
        }

        if (await _userManager.IsLockedOutAsync(user))
            return StatusCode(StatusCodes.Status423Locked,
                "Account locked. Please try again after 1 minute.");

        await _userManager.ResetAccessFailedCountAsync(user);

        var roles = await _userManager.GetRolesAsync(user);
        var isAdmin = roles.Contains(SystemRoles.SystemAdmin);

        // IP restriction and MFA branching apply only to SystemAdmin.
        if (isAdmin)
        {
            var settings = await _db.AdminSecuritySettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1);

            if (settings is not null && settings.EnableAdminIpRestriction && !await IsAdminIpAllowedAsync())
            {
                AddAnonymousAudit("User", user.Id, "AdminLoginBlockedIpNotAllowed", new { user.Email });
                await _db.SaveChangesAsync();
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { error = "Admin login is not allowed from this IP address." });
            }

            if (settings is not null && settings.RequireMfaForAdmins)
            {
                if (!user.TotpEnabled)
                {
                    var setupTicket = _mfaTickets.Issue(user.Id, MfaTicketPurposes.Setup);
                    AddAnonymousAudit("User", user.Id, "AdminMfaEnrollmentRequired", new { user.Email });
                    await _db.SaveChangesAsync();
                    return Ok(new
                    {
                        mfaEnrollmentRequired = true,
                        mfaSetupTicket = setupTicket,
                        expiresInSeconds = _configuration.GetValue<int?>("AdminSecurity:MfaTicketTtlSeconds") ?? 300
                    });
                }

                var verifyTicket = _mfaTickets.Issue(user.Id, MfaTicketPurposes.Verify);
                AddAnonymousAudit("User", user.Id, "AdminMfaChallengeIssued", new { user.Email });
                await _db.SaveChangesAsync();
                return Ok(new
                {
                    mfaRequired = true,
                    mfaTicket = verifyTicket,
                    expiresInSeconds = _configuration.GetValue<int?>("AdminSecurity:MfaTicketTtlSeconds") ?? 300
                });
            }
        }

        AddAnonymousAudit("User", user.Id, "UserLoggedIn", new { user.Email, Roles = roles });
        await _db.SaveChangesAsync();

        var (token, expires) = await _tokenService.CreateTokenAsync(user);
        return Ok(new AuthResponse(token, expires, user.Id, user.Email!, user.FullName, roles.ToList()));
    }

    [AllowAnonymous]
    [HttpPost("mfa/enroll/start")]
    public async Task<IActionResult> MfaEnrollStart(MfaEnrollStartRequest request)
    {
        var ticket = _mfaTickets.ValidateAndRead(request.MfaSetupTicket, MfaTicketPurposes.Setup);
        if (ticket is null) return Unauthorized(new { error = "Invalid or expired setup ticket." });

        var user = await _userManager.FindByIdAsync(ticket.UserId);
        if (user is null || !user.IsActive) return Unauthorized(new { error = "Invalid or expired setup ticket." });

        if (user.TotpEnabled)
            return BadRequest(new { error = "MFA is already enabled for this account." });

        var roles = await _userManager.GetRolesAsync(user);
        if (!roles.Contains(SystemRoles.SystemAdmin))
            return Forbid();

        var issuer = _jwt.Value.Issuer.Length > 0 ? _jwt.Value.Issuer : "CompetitionManagementSystem";
        var artifacts = _totp.CreateEnrollment(user, issuer);

        // CreateEnrollment populates user.TotpSecretEncrypted (but TotpEnabled stays false until confirmed).
        await _userManager.UpdateAsync(user);

        AddAnonymousAudit("User", user.Id, "AdminMfaEnrollmentStarted", new { user.Email });
        await _db.SaveChangesAsync();

        return Ok(new
        {
            otpauthUri = artifacts.OtpAuthUri,
            manualEntryKey = artifacts.ManualEntryKey,
            qrCodeDataUri = artifacts.QrCodeDataUri
        });
    }

    [AllowAnonymous]
    [HttpPost("mfa/enroll/confirm")]
    public async Task<IActionResult> MfaEnrollConfirm(MfaEnrollConfirmRequest request)
    {
        var ticket = _mfaTickets.ValidateAndRead(request.MfaSetupTicket, MfaTicketPurposes.Setup);
        if (ticket is null) return Unauthorized(new { error = "Invalid or expired setup ticket." });

        var user = await _userManager.FindByIdAsync(ticket.UserId);
        if (user is null || !user.IsActive) return Unauthorized(new { error = "Invalid or expired setup ticket." });

        if (string.IsNullOrWhiteSpace(user.TotpSecretEncrypted))
            return BadRequest(new { error = "No enrollment in progress. Call /mfa/enroll/start first." });

        if (!_totp.VerifyCode(user.TotpSecretEncrypted, request.Code))
        {
            await _userManager.AccessFailedAsync(user);
            AddAnonymousAudit("User", user.Id, "AdminMfaEnrollmentConfirmFailed", new { user.Email });
            await _db.SaveChangesAsync();
            return Unauthorized(new { error = "Invalid code." });
        }

        var (codes, encryptedBlob) = _totp.GenerateRecoveryCodes();
        user.TotpRecoveryCodesEncrypted = encryptedBlob;
        user.TotpEnabled = true;
        user.TotpEnabledAtUtc = DateTime.UtcNow;
        user.TotpLastVerifiedAtUtc = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);
        await _userManager.ResetAccessFailedCountAsync(user);

        AddAnonymousAudit("User", user.Id, "AdminMfaEnrolled", new { user.Email });
        await _db.SaveChangesAsync();

        var (token, expires) = await _tokenService.CreateTokenAsync(user);
        var roles = await _userManager.GetRolesAsync(user);
        return Ok(new
        {
            token,
            expiresAtUtc = expires,
            userId = user.Id,
            email = user.Email,
            fullName = user.FullName,
            roles,
            recoveryCodes = codes,
            recoveryCodesNotice = "Store these codes now. They are shown only once and cannot be retrieved later."
        });
    }

    [AllowAnonymous]
    [HttpPost("mfa/verify")]
    public async Task<IActionResult> MfaVerify(MfaVerifyRequest request)
    {
        var ticket = _mfaTickets.ValidateAndRead(request.MfaTicket, MfaTicketPurposes.Verify);
        if (ticket is null) return Unauthorized(new { error = "Invalid or expired MFA ticket." });

        var user = await _userManager.FindByIdAsync(ticket.UserId);
        if (user is null || !user.IsActive) return Unauthorized(new { error = "Invalid or expired MFA ticket." });
        if (!user.TotpEnabled || string.IsNullOrWhiteSpace(user.TotpSecretEncrypted))
            return BadRequest(new { error = "MFA is not enabled for this account." });

        if (await _userManager.IsLockedOutAsync(user))
            return StatusCode(StatusCodes.Status423Locked,
                "Account locked. Please try again after 1 minute.");

        var code = (request.Code ?? string.Empty).Trim();
        var totpOk = _totp.VerifyCode(user.TotpSecretEncrypted, code);

        var recoveryOk = false;
        if (!totpOk && !string.IsNullOrWhiteSpace(user.TotpRecoveryCodesEncrypted))
        {
            recoveryOk = _totp.TryConsumeRecoveryCode(user.TotpRecoveryCodesEncrypted, code, out var updatedBlob);
            if (recoveryOk)
            {
                user.TotpRecoveryCodesEncrypted = updatedBlob;
                await _userManager.UpdateAsync(user);
            }
        }

        if (!totpOk && !recoveryOk)
        {
            await _userManager.AccessFailedAsync(user);
            var failedCount = await _userManager.GetAccessFailedCountAsync(user);
            if (!await _userManager.IsLockedOutAsync(user) && failedCount >= 3)
            {
                await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(1));
            }
            AddAnonymousAudit("User", user.Id, "AdminMfaVerifyFailed", new { user.Email });
            await _db.SaveChangesAsync();
            return Unauthorized(new { error = "Invalid code." });
        }

        user.TotpLastVerifiedAtUtc = DateTime.UtcNow;
        await _userManager.UpdateAsync(user);
        await _userManager.ResetAccessFailedCountAsync(user);

        var roles = await _userManager.GetRolesAsync(user);
        AddAnonymousAudit("User", user.Id, recoveryOk ? "AdminMfaVerifiedWithRecoveryCode" : "AdminMfaVerified", new { user.Email });
        await _db.SaveChangesAsync();

        var (jwtToken, jwtExpires) = await _tokenService.CreateTokenAsync(user);
        return Ok(new AuthResponse(jwtToken, jwtExpires, user.Id, user.Email!, user.FullName, roles.ToList()));
    }

    private async Task<bool> IsAdminIpAllowedAsync()
    {
        if (_adminOptions.CurrentValue.EmergencyDisableAdminIpRestriction)
            return true;

        var resolved = _ipResolver.Resolve(HttpContext);
        if (resolved.RejectionReason is not null || resolved.ClientIp is null)
            return false;

        if (_adminOptions.CurrentValue.AllowLocalhostInDevelopment && _env.IsDevelopment() && IPAddress.IsLoopback(resolved.ClientIp))
            return true;

        var enabledRanges = await _db.AdminAllowedIpRanges
            .AsNoTracking()
            .Where(r => r.IsEnabled)
            .Select(r => r.IpRange)
            .ToListAsync();
        var parsed = IpCidr.ParseAll(enabledRanges);
        return IpCidr.AnyContains(parsed, resolved.ClientIp);
    }

    private void AddAnonymousAudit(string entityType, string entityId, string action, object? newValue)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            NewValueJson = newValue is null ? null : JsonSerializer.Serialize(newValue),
            UserId = entityId,
            UserEmail = newValue?.GetType().GetProperty("Email")?.GetValue(newValue)?.ToString(),
            UserRole = null,
            IpAddress = Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded) ? forwarded.ToString().Split(',')[0].Trim() : HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAtUtc = DateTime.UtcNow
        });
    }
}
