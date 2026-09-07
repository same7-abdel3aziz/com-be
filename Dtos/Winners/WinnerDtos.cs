namespace CompetitionManagementSystem.Dtos.Winners;

public sealed record JudgingStatusResponse(
    Guid CompetitionId,
    bool IsJudgingCompleted,
    int ApprovedForJudgingCount,
    int ReservedForJudgingCount,
    int ScoredCount,
    int TotalStillWaiting);

public sealed record WinnerCandidateResponse(
    Guid ParticipationId,
    string? AuthorUserName,
    string? AuthorDisplayName,
    string? ExternalPostUrl,
    string? Text,
    decimal FinalScore,
    int ScoreCount,
    DateTime? ScoredAtUtc,
    bool IsManualTieWinner,
    string? TieSelectionReason);

public sealed record SelectWinnersRequest(
    Guid CompetitionId,
    Guid FirstPlaceParticipationId,
    Guid SecondPlaceParticipationId,
    Guid ThirdPlaceParticipationId);

public sealed record WinnerResponse(
    Guid Id,
    Guid CompetitionId,
    Guid ParticipationId,
    int Rank,
    decimal FinalScore,
    string? AuthorUserName,
    string? AuthorDisplayName,
    string? ExternalPostUrl,
    DateTime SelectedAtUtc,
    string? SelectedByUserId,
    string? SelectedByAdminName,
    string? SelectedByAdminEmail);

public sealed record SelectWinnerRankRequest(
    Guid CompetitionId,
    Guid ParticipationId,
    int Rank);