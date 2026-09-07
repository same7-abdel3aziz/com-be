using CompetitionManagementSystem.Dtos.Reports;

namespace CompetitionManagementSystem.Services.Reporting;

public interface IReportService
{
    Task<DashboardResponse> GetDashboardAsync(Guid? competitionId, CancellationToken cancellationToken);
    Task<byte[]> ExportParticipationsCsvAsync(Guid competitionId, CancellationToken cancellationToken);
    Task<byte[]> ExportScoresCsvAsync(Guid competitionId, CancellationToken cancellationToken);
    Task<byte[]> ExportRawJsonAsync(Guid competitionId, CancellationToken cancellationToken);
    Task<byte[]> ExportTopScoresCsvAsync(Guid competitionId, int take, CancellationToken cancellationToken);
}
