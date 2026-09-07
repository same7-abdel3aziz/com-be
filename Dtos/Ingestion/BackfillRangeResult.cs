namespace CompetitionManagementSystem.Dtos.Ingestion;

public sealed class BackfillRangeResult
{
    public Guid   IngestionRunId          { get; set; }
    public bool   IsComplete              { get; set; } = true;
    public int    WindowsChecked          { get; set; }
    public int    WindowsSplit            { get; set; }
    public int    SaturatedTerminalCount  { get; set; }  // windows too small to split but still saturated
    public int    TotalTweetsFromApi      { get; set; }
    public int    ImportedCount           { get; set; }
    public int    UpdatedExistingCount    { get; set; }
    public int    SkippedDuplicateCount   { get; set; }
    public int    AutoExcludedCount       { get; set; }
    public int    FailedCount             { get; set; }
    public int    RequestsUsed             { get; set; }
    public int    HasNextPageAdvisoryCount { get; set; }  // has_next_page=true but count < SaturationThreshold
    public bool   BudgetExceeded          { get; set; }
    public List<string> NewTweetIds              { get; set; } = new();
    public List<string> IncompleteWindowKeys     { get; set; } = new();
}
