using System.ComponentModel.DataAnnotations;

namespace CompetitionManagementSystem.Dtos.Users;

/// <summary>
/// Admin-only payload to reset another user's password.
/// Identify the target by Id OR Email; one of them must be provided.
/// </summary>
public sealed class AdminResetPasswordRequest
{
    public string? UserId { get; set; }

    [EmailAddress]
    public string? Email { get; set; }

    [Required]
    public string NewPassword { get; set; } = string.Empty;
}