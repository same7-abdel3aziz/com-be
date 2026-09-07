namespace CompetitionManagementSystem.Models;

public sealed class Competition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Hashtag { get; set; } = string.Empty;
    public string? SecondaryHashtag { get; set; }
    public DateTime StartDateUtc { get; set; }
    public DateTime EndDateUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public int PollingIntervalMinutes { get; set; } = 10;
    public int RequiredScoresPerJudge { get; set; } = 20;
    public bool JudgingStarted { get; set; }
    public DateTime? JudgingStartedAtUtc { get; set; }
    public DateTime? LastIngestedUntilUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<Participation> Participations { get; set; } = new List<Participation>();
}
