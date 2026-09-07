namespace CompetitionManagementSystem.Dtos.Scores;

public sealed record UpdateScoreRequest(decimal Score, string? Notes, string? Justification = null);
