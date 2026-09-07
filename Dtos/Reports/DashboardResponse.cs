namespace CompetitionManagementSystem.Dtos.Reports;

public sealed record DashboardResponse(
    int ActiveCompetitions,
    int TotalParticipations,
    int NewParticipationsToday,
    int CompletedScores,
    IReadOnlyList<TopCompetitionActivityDto> TopActiveCompetitions,
    IReadOnlyList<HighestScoreDto> HighestScores,
    IReadOnlyList<TopInteractorDto> TopInteractors,
    IReadOnlyList<LatestParticipationDto> LatestParticipations);

public sealed record TopCompetitionActivityDto(Guid CompetitionId, string CompetitionName, string Hashtag, int ParticipationsCount, int ScoresCount);
public sealed record HighestScoreDto(Guid ParticipationId, string AuthorUserName, string Text, string Url, decimal FinalScore, int ScoreCount);
public sealed record TopInteractorDto(string AuthorUserName, string AuthorDisplayName, int Followers, bool IsBlueVerified, int TotalInteractions);
public sealed record LatestParticipationDto(Guid ParticipationId, string AuthorUserName, string Text, string Url, DateTime ImportedAtUtc);
