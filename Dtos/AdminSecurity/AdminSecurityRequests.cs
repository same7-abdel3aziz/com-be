using System.ComponentModel.DataAnnotations;

namespace CompetitionManagementSystem.Dtos.AdminSecurity;

public sealed class CreateIpRangeRequest
{
    [Required] public string IpRange { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? Notes { get; set; }
}

public sealed class UpdateIpRangeRequest
{
    public string? IpRange { get; set; }
    public string? Description { get; set; }
    public bool? IsEnabled { get; set; }
    public string? Notes { get; set; }
}

public sealed class UpdateAdminSecuritySettingsRequest
{
    public bool? EnableAdminIpRestriction { get; set; }
    public bool? RequireMfaForAdmins { get; set; }
}
