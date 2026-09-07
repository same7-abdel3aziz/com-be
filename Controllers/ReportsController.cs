using System.Security.Claims;
using System.Text.Json;
using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Models;
using CompetitionManagementSystem.Security;
using CompetitionManagementSystem.Services.Reporting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CompetitionManagementSystem.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin},{SystemRoles.GeneralSupervisor}")]
public sealed class ReportsController : ControllerBase
{
    private readonly IReportService _reportService;
    private readonly ApplicationDbContext _db;

    public ReportsController(IReportService reportService, ApplicationDbContext db)
    {
        _reportService = reportService;
        _db = db;
    }

    [HttpGet("participations/export.xls")]
    public async Task<IActionResult> ExportParticipations([FromQuery] Guid competitionId, CancellationToken ct)
    {
        var bytes = await _reportService.ExportParticipationsCsvAsync(competitionId, ct);
        await AuditExportAsync(competitionId, "ParticipationsExcelExported", ct);

        return File(
            bytes,
            "application/vnd.ms-excel; charset=utf-8",
            "تقرير-المشاركات.xls");
    }

    [HttpGet("scores/export.xls")]
    public async Task<IActionResult> ExportScores([FromQuery] Guid competitionId, CancellationToken ct)
    {
        var bytes = await _reportService.ExportScoresCsvAsync(competitionId, ct);
        await AuditExportAsync(competitionId, "ScoresExcelExported", ct);

        return File(
            bytes,
            "application/vnd.ms-excel; charset=utf-8",
            "تقرير-التقييمات.xls");
    }

    [HttpGet("top-scores/export.xls")]
    public async Task<IActionResult> ExportTopScores(
        [FromQuery] Guid competitionId,
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        var bytes = await _reportService.ExportTopScoresCsvAsync(competitionId, take, ct);
        await AuditExportAsync(competitionId, "TopScoresExcelExported", ct);

        return File(
            bytes,
            "application/vnd.ms-excel; charset=utf-8",
            "تقرير-أعلى-الدرجات.xls");
    }

    [HttpGet("raw-json/export.json")]
    public async Task<IActionResult> ExportRawJson([FromQuery] Guid competitionId, CancellationToken ct)
    {
        var bytes = await _reportService.ExportRawJsonAsync(competitionId, ct);
        await AuditExportAsync(competitionId, "RawJsonExported", ct);

        return File(
            bytes,
            "application/json; charset=utf-8",
            "raw-json-export.json");
    }

    private async Task AuditExportAsync(Guid competitionId, string action, CancellationToken ct)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            EntityType = "Competition",
            EntityId = competitionId.ToString(),
            Action = action,
            NewValueJson = JsonSerializer.Serialize(new { competitionId }),
            UserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
            UserEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name,
            UserRole = string.Join(',', User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value)),
            IpAddress = Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded)
                ? forwarded.ToString().Split(',')[0].Trim()
                : HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAtUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);
    }
}