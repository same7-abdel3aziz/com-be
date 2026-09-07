namespace CompetitionManagementSystem.Dtos.Competitions;

public sealed record CompetitionResponse(
    Guid Id,
    string Name,
    string? Description,
    string Hashtag,
    string? SecondaryHashtag,
    DateTime StartDateUtc,
    DateTime EndDateUtc,
    bool IsActive,
    int PollingIntervalMinutes,
    int RequiredScoresPerJudge,
    bool JudgingStarted,
    DateTime? JudgingStartedAtUtc,
    DateTime? LastIngestedUntilUtc,
    DateTime CreatedAtUtc);
