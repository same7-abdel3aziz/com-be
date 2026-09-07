namespace CompetitionManagementSystem.Dtos.Scores;

public sealed record SubmitScoreRequest(decimal Score, string? Notes, string? Justification = null);
