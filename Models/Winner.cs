using CompetitionManagementSystem.Models;

public sealed class Winner
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CompetitionId { get; set; }
    public Competition Competition { get; set; } = null!;

    public Guid ParticipationId { get; set; }
    public Participation Participation { get; set; } = null!;

    public int Rank { get; set; } // 1, 2, 3

    public decimal FinalScore { get; set; }

    public string SelectedByUserId { get; set; } = "";
    public DateTime SelectedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsActive { get; set; } = true;
}