namespace CompetitionManagementSystem.Dtos.Competitions;

public sealed record CreateCompetitionRequest(
    string Name,
    string? Description,
    string Hashtag,
    DateTime StartDateUtc,
    DateTime EndDateUtc,
    bool IsActive = true,
    int PollingIntervalMinutes = 10,
    string? SecondaryHashtag = null,
    int RequiredScoresPerJudge = 20);
