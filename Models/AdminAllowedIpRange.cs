namespace CompetitionManagementSystem.Models;

public sealed class AdminAllowedIpRange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IpRange { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedByUserId { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public string? Notes { get; set; }
}
