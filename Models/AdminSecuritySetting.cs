namespace CompetitionManagementSystem.Models;

/// <summary>
/// Single-row settings table (Id == 1). EmergencyDisableAdminIpRestriction lives in
/// appsettings as a process-level override and is intentionally not stored here.
/// </summary>
public sealed class AdminSecuritySetting
{
    public int Id { get; set; } = 1;
    public bool EnableAdminIpRestriction { get; set; }
    public bool RequireMfaForAdmins { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedByUserId { get; set; }
}
