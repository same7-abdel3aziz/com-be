namespace CompetitionManagementSystem.Dtos.Ingestion;

public sealed class BackfillRangeRequest
{
    public DateTime FromUtc  { get; set; }
    public DateTime UntilUtc { get; set; }

    /// <summary>Starting window size. Smaller sub-windows are used automatically when saturated.</summary>
    public int InitialWindowMinutes { get; set; } = 60;

    /// <summary>
    /// Hard cap on total API requests. The operation stops cleanly when the budget is exhausted
    /// and sets <see cref="BackfillRangeResult.BudgetExceeded"/> = true.
    /// </summary>
    public int MaxRequests { get; set; } = 3000;
}
