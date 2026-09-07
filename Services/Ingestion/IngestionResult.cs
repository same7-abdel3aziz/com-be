namespace CompetitionManagementSystem.Services.Ingestion;

public sealed record IngestionResult(int ImportedCount, int UpdatedExistingCount, int SkippedDuplicateCount, int AutoExcludedCount, int FailedCount, Guid IngestionRunId);
