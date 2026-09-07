namespace CompetitionManagementSystem.Models;

public sealed class ParticipationScore
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompetitionId { get; set; }
    public Competition? Competition { get; set; }
    public Guid ParticipationId { get; set; }
    public Participation? Participation { get; set; }
    public string JudgeUserId { get; set; } = string.Empty;
    public ApplicationUser? JudgeUser { get; set; }
    public decimal Score { get; set; }
    public string? Notes { get; set; }
    public string Justification { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
