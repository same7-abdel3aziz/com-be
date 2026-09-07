namespace CompetitionManagementSystem.Models;

public sealed class ParticipationExternalUrl
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ParticipationId { get; set; }
    public Participation? Participation { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? ExpandedUrl { get; set; }
    public string? DisplayUrl { get; set; }
    public string? RawJsonData { get; set; }
}
