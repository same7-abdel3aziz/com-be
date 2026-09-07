using System.ComponentModel.DataAnnotations;

namespace CompetitionManagementSystem.Dtos.Mfa;

public sealed class MfaEnrollStartRequest
{
    [Required] public string MfaSetupTicket { get; set; } = string.Empty;
}

public sealed class MfaEnrollConfirmRequest
{
    [Required] public string MfaSetupTicket { get; set; } = string.Empty;
    [Required] public string Code { get; set; } = string.Empty;
}

public sealed class MfaVerifyRequest
{
    [Required] public string MfaTicket { get; set; } = string.Empty;
    /// <summary>6-digit TOTP code OR a recovery code (e.g. ABCDE-12345).</summary>
    [Required] public string Code { get; set; } = string.Empty;
}
