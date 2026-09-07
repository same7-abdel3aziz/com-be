using CompetitionManagementSystem.Dtos.Ingestion;

namespace CompetitionManagementSystem.Services.Ingestion;

public interface IIngestionService
{
    Task<IngestionResult> RunForCompetitionAsync(Guid competitionId, CancellationToken cancellationToken);
    Task RunForAllActiveCompetitionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ingests tweets for the given competition within the explicit time window.
    /// Never advances <c>competition.LastIngestedUntilUtc</c>.
    /// </summary>
    Task<IngestionResult> BackfillWindowAsync(
        Guid competitionId,
        DateTime fromUtc,
        DateTime untilUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates an IngestionRun and launches full historical recovery as a background task.
    /// Returns immediately with the run ID (202 pattern).
    /// Never advances <c>competition.LastIngestedUntilUtc</c>.
    /// </summary>
    Task<Guid> StartBackfillRangeAsync(
        Guid competitionId,
        DateTime fromUtc,
        DateTime untilUtc,
        int initialWindowMinutes,
        int maxRequests,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resumes an existing InProgress or BudgetExceeded/IncompleteCoverage range backfill
    /// from the last saved checkpoint (<see cref="IngestionRun.NextWindowFromUtc"/>).
    /// Returns the same run ID. 202 pattern — returns immediately.
    /// </summary>
    Task<Guid> ResumeBackfillRangeAsync(
        Guid competitionId,
        Guid runId,
        int maxRequests,
        CancellationToken cancellationToken);
}
