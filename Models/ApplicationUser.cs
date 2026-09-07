using Microsoft.AspNetCore.Identity;

namespace CompetitionManagementSystem.Models;

public sealed class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // TOTP-based MFA. Secrets and recovery codes are protected via IDataProtector
    // before being written. Never stored or logged in clear form.
    public string? TotpSecretEncrypted { get; set; }
    public bool TotpEnabled { get; set; }
    public string? TotpRecoveryCodesEncrypted { get; set; }
    public DateTime? TotpEnabledAtUtc { get; set; }
    public DateTime? TotpLastVerifiedAtUtc { get; set; }
}
