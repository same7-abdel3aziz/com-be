namespace CompetitionManagementSystem.Options;

public sealed class AdminSecurityOptions
{
    /// <summary>
    /// Emergency rollback only. When true, the admin IP restriction middleware bypasses every
    /// check, ignoring the DB-backed settings. Use to recover from accidental lockout. Leave
    /// false in normal operation.
    /// </summary>
    public bool EmergencyDisableAdminIpRestriction { get; set; } = false;

    /// <summary>
    /// Cloudflare's own edge IP ranges. CF-Connecting-IP is trusted only when the immediate
    /// RemoteIpAddress falls inside one of these. Refresh from https://www.cloudflare.com/ips/.
    /// This stays in config (infrastructure trust list, not user-managed whitelist).
    /// </summary>
    public List<string> AllowedCloudflareIpRanges { get; set; } = new();

    /// <summary>
    /// When the environment is Development, allow 127.0.0.1 / ::1 even if not whitelisted.
    /// </summary>
    public bool AllowLocalhostInDevelopment { get; set; } = true;

    /// <summary>
    /// JWT-style ticket that bridges the password step and the OTP step during MFA login.
    /// </summary>
    public int MfaTicketTtlSeconds { get; set; } = 300;

    /// <summary>
    /// TOTP verification window in steps (each step = 30 s). 1 allows the immediately
    /// previous and next codes, accommodating clock drift.
    /// </summary>
    public int TotpVerificationWindowSteps { get; set; } = 1;
}
