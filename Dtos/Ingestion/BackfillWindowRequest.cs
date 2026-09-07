namespace CompetitionManagementSystem.Dtos.Ingestion;

public sealed class BackfillWindowRequest
{
    public DateTime FromUtc  { get; set; }
    public DateTime UntilUtc { get; set; }
}
