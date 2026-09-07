using System.Data;
using System.Text.Json;
using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Dtos.Scores;
using CompetitionManagementSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace CompetitionManagementSystem.Services.Scoring;

public sealed class ScoringService : IScoringService
{
    private readonly ApplicationDbContext _db;

    public ScoringService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<ScoreResponse> SubmitScoreAsync(Guid participationId, string judgeUserId, string? judgeEmail, string? judgeRole, string? ipAddress, decimal score, string? notes, string? justification, CancellationToken cancellationToken)
    {
        if (score < 1 || score > 100)
            throw new InvalidOperationException("Score must be between 1 and 10.");

        var finalJustification = string.IsNullOrWhiteSpace(justification) ? notes : justification;
        if (string.IsNullOrWhiteSpace(finalJustification))
            throw new InvalidOperationException("Justification is required.");

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var participation = await _db.Participations
            .FirstOrDefaultAsync(p => p.Id == participationId && p.IsActive, cancellationToken);

        if (participation is null)
            throw new KeyNotFoundException("Participation not found.");

        //if (participation.Status == ParticipationStatus.ApprovedForJudging && string.IsNullOrWhiteSpace(participation.ReservedByJudgeUserId))
        //{
        //    participation.Status = ParticipationStatus.ReservedForJudging;
        //    participation.ReservedByJudgeUserId = judgeUserId;
        //    participation.ReservedAtUtc = DateTime.UtcNow;
        //}

        if (participation.Status != ParticipationStatus.ReservedForJudging || participation.ReservedByJudgeUserId != judgeUserId)
            throw new InvalidOperationException("This participation is not reserved for the current judge.");

        var alreadyScored = await _db.ParticipationScores.AnyAsync(s =>
            s.CompetitionId == participation.CompetitionId &&
            s.ParticipationId == participation.Id,
            cancellationToken);

        if (alreadyScored || participation.ScoreCount > 0 || participation.Status == ParticipationStatus.Scored)
            throw new InvalidOperationException("This participation has already been scored.");

        var scoreEntity = new ParticipationScore
        {
            CompetitionId = participation.CompetitionId,
            ParticipationId = participation.Id,
            JudgeUserId = judgeUserId,
            Score = score,
            Notes = notes,
            Justification = finalJustification.Trim(),
            IpAddress = ipAddress,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.ParticipationScores.Add(scoreEntity);

        var oldValue = JsonSerializer.Serialize(new
        {
            participation.Status,
            participation.ScoreSum,
            participation.ScoreCount,
            participation.FinalScore,
            participation.LastScoredAtUtc,
            participation.ScoredByJudgeUserId,
            participation.ScoredAtUtc
        });

        participation.ScoreSum = score;
        participation.ScoreCount = 1;
        participation.FinalScore = score;
        participation.LastScoredAtUtc = scoreEntity.CreatedAtUtc;
        participation.Status = ParticipationStatus.Scored;
        participation.ScoredByJudgeUserId = judgeUserId;
        participation.ScoredAtUtc = scoreEntity.CreatedAtUtc;

        _db.AuditLogs.Add(new AuditLog
        {
            EntityType = "Participation",
            EntityId = participation.Id.ToString(),
            Action = "ParticipationScored",
            OldValueJson = oldValue,
            NewValueJson = JsonSerializer.Serialize(new
            {
                participation.Status,
                participation.FinalScore,
                ScoreId = scoreEntity.Id,
                scoreEntity.Score,
                scoreEntity.Justification,
                JudgeUserId = judgeUserId
            }),
            UserId = judgeUserId,
            UserEmail = judgeEmail,
            UserRole = judgeRole,
            IpAddress = ipAddress,
            CreatedAtUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ScoreResponse(
            scoreEntity.Id,
            scoreEntity.CompetitionId,
            scoreEntity.ParticipationId,
            scoreEntity.JudgeUserId,
            scoreEntity.Score,
            scoreEntity.Notes,
            scoreEntity.Justification,
            scoreEntity.CreatedAtUtc);
    }

    public async Task<ScoreResponse> UpdateScoreAsync(
        Guid scoreId,
        string judgeUserId,
        string? judgeEmail,
        string? judgeRole,
        string? ipAddress,
        decimal score,
        string? notes,
        string? justification,
        CancellationToken cancellationToken)
    {
        if (score < 1 || score > 100)
            throw new InvalidOperationException("Score must be between 1 and 100.");

        var finalJustification = string.IsNullOrWhiteSpace(justification) ? notes : justification;
        if (string.IsNullOrWhiteSpace(finalJustification))
            throw new InvalidOperationException("Justification is required.");

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var scoreEntity = await _db.ParticipationScores
            .FirstOrDefaultAsync(s => s.Id == scoreId, cancellationToken);

        if (scoreEntity is null)
            throw new KeyNotFoundException("Score not found.");

        // Authorization: a judge may only edit their own score row.
        if (!string.Equals(scoreEntity.JudgeUserId, judgeUserId, StringComparison.Ordinal))
            throw new ForbiddenScoreActionException("This score belongs to another judge.");

        var participation = await _db.Participations
            .FirstOrDefaultAsync(p => p.Id == scoreEntity.ParticipationId && p.IsActive, cancellationToken);

        if (participation is null)
            throw new KeyNotFoundException("Participation for this score not found.");

        // "Completed" === Scored. Only allow editing once the evaluation is in its terminal state.
        if (participation.Status != ParticipationStatus.Scored)
            throw new InvalidOperationException("Only completed (Scored) evaluations can be updated.");

        // Defensive: the score's ScoredByJudgeUserId on the participation must match the caller.
        // ScoreCount must be exactly 1 (single-judge model), so we can safely re-derive aggregates.
        if (!string.Equals(participation.ScoredByJudgeUserId, judgeUserId, StringComparison.Ordinal))
            throw new ForbiddenScoreActionException("This participation was scored by another judge.");
        if (participation.ScoreCount != 1)
            throw new InvalidOperationException("Unexpected score state: cannot safely update.");

        var oldScoreSnapshot = JsonSerializer.Serialize(new
        {
            scoreEntity.Score,
            scoreEntity.Notes,
            scoreEntity.Justification,
            participation.ScoreSum,
            participation.FinalScore,
            participation.LastScoredAtUtc,
            participation.Status
        });

        // Update only the mutable score fields. Protected fields stay frozen:
        //   Id, CompetitionId, ParticipationId, JudgeUserId, CreatedAtUtc.
        scoreEntity.Score = score;
        scoreEntity.Notes = notes;
        scoreEntity.Justification = finalJustification.Trim();
        scoreEntity.IpAddress = ipAddress;

        // Recompute aggregates on the parent participation. Status stays Scored.
        // ScoredByJudgeUserId / ScoredAtUtc are left as-is (they mark the original scoring event).
        var now = DateTime.UtcNow;
        participation.ScoreSum = score;
        participation.FinalScore = score;
        participation.LastScoredAtUtc = now;

        _db.AuditLogs.Add(new AuditLog
        {
            EntityType = "ParticipationScore",
            EntityId = scoreEntity.Id.ToString(),
            Action = "ParticipationScoreUpdated",
            OldValueJson = oldScoreSnapshot,
            NewValueJson = JsonSerializer.Serialize(new
            {
                scoreEntity.Score,
                scoreEntity.Notes,
                scoreEntity.Justification,
                participation.ScoreSum,
                participation.FinalScore,
                participation.LastScoredAtUtc,
                participation.Status
            }),
            UserId = judgeUserId,
            UserEmail = judgeEmail,
            UserRole = judgeRole,
            IpAddress = ipAddress,
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ScoreResponse(
            scoreEntity.Id,
            scoreEntity.CompetitionId,
            scoreEntity.ParticipationId,
            scoreEntity.JudgeUserId,
            scoreEntity.Score,
            scoreEntity.Notes,
            scoreEntity.Justification,
            scoreEntity.CreatedAtUtc);
    }
}
