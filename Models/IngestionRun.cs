namespace CompetitionManagementSystem.Models;

public sealed class IngestionRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompetitionId { get; set; }
    public Competition? Competition { get; set; }
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? UntilUtc { get; set; }
    public string Status { get; set; } = "Started";
    public int ImportedCount { get; set; }
    public int UpdatedExistingCount { get; set; }
    public int SkippedDuplicateCount { get; set; }
    public int AutoExcludedCount { get; set; }
    public int FailedCount { get; set; }
    public string? ErrorMessage { get; set; }

    // ── Resumable backfill fields ────────────────────────────────────────────
    /// <summary>The next window start time for a range backfill. Used to resume after restart.</summary>
    public DateTime? NextWindowFromUtc { get; set; }

    /// <summary>NULL = historical/unknown. true = complete. false = incomplete coverage.</summary>
    public bool? IsComplete { get; set; }

    /// <summary>JSON summary report persisted at end of range backfill.</summary>
    public string? ResultSummaryJson { get; set; }
}
