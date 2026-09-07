namespace CompetitionManagementSystem.Dtos.Scores;

public sealed record ScoreResponse(Guid Id, Guid CompetitionId, Guid ParticipationId, string JudgeUserId, decimal Score, string? Notes, string Justification, DateTime CreatedAtUtc);
