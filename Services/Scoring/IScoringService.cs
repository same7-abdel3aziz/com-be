using CompetitionManagementSystem.Dtos.Scores;

namespace CompetitionManagementSystem.Services.Scoring;

public interface IScoringService
{
    Task<ScoreResponse> SubmitScoreAsync(Guid participationId, string judgeUserId, string? judgeEmail, string? judgeRole, string? ipAddress, decimal score, string? notes, string? justification, CancellationToken cancellationToken);

    /// <summary>
    /// Updates an already-submitted score (the participation is in <c>Scored</c> state).
    /// Only the judge who owns the score may update it. The status stays <c>Scored</c>;
    /// no new score row is created.
    /// </summary>
    Task<ScoreResponse> UpdateScoreAsync(Guid scoreId, string judgeUserId, string? judgeEmail, string? judgeRole, string? ipAddress, decimal score, string? notes, string? justification, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when the caller is authenticated but not allowed to act on the target resource
/// (e.g. another judge's score). Controller maps to 403.
/// </summary>
public sealed class ForbiddenScoreActionException : Exception
{
    public ForbiddenScoreActionException(string message) : base(message) { }
}
