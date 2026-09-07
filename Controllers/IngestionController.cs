using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Dtos.Ingestion;
using CompetitionManagementSystem.Security;
using CompetitionManagementSystem.Services.Ingestion;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CompetitionManagementSystem.Controllers;

[ApiController]
[Route("api/ingestion")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin}")]
public sealed class IngestionController : ControllerBase
{
    private readonly IIngestionService _ingestionService;
    private readonly ApplicationDbContext _db;

    public IngestionController(IIngestionService ingestionService, ApplicationDbContext db)
    {
        _ingestionService = ingestionService;
        _db = db;
    }

    [HttpPost("competitions/{competitionId:guid}/run")]
    public async Task<ActionResult<IngestionResult>> Run(Guid competitionId, CancellationToken ct)
    {
        var result = await _ingestionService.RunForCompetitionAsync(competitionId, ct);
        return Ok(result);
    }

    /// <summary>
    /// Best-effort targeted backfill for a specific time window.
    /// Never advances LastIngestedUntilUtc. Use to recover missed tweets.
    /// Max window: 1 hour.
    /// </summary>
    [HttpPost("competitions/{competitionId:guid}/backfill-window")]
    public async Task<ActionResult<IngestionResult>> BackfillWindow(
        Guid competitionId,
        [FromBody] BackfillWindowRequest request,
        CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new { error = "Request body is required." });

        var fromUtc  = DateTime.SpecifyKind(request.FromUtc,  DateTimeKind.Utc);
        var untilUtc = DateTime.SpecifyKind(request.UntilUtc, DateTimeKind.Utc);

        if (untilUtc <= fromUtc)
            return BadRequest(new { error = "UntilUtc must be after FromUtc." });

        if ((untilUtc - fromUtc).TotalHours > 1)
            return BadRequest(new { error = "Backfill window cannot exceed 1 hour in this version." });

        var result = await _ingestionService.BackfillWindowAsync(competitionId, fromUtc, untilUtc, ct);
        return Ok(result);
    }

    /// <summary>
    /// Launches a full historical range backfill as a background task.
    /// Returns 202 Accepted immediately with the IngestionRun ID.
    /// Never advances LastIngestedUntilUtc.
    /// </summary>
    [HttpPost("competitions/{competitionId:guid}/backfill-range")]
    public async Task<ActionResult<object>> BackfillRange(
        Guid competitionId,
        [FromBody] BackfillRangeRequest request,
        CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new { error = "Request body is required." });

        var fromUtc  = DateTime.SpecifyKind(request.FromUtc,  DateTimeKind.Utc);
        var untilUtc = DateTime.SpecifyKind(request.UntilUtc, DateTimeKind.Utc);

        if (untilUtc <= fromUtc)
            return BadRequest(new { error = "UntilUtc must be after FromUtc." });

        if (request.InitialWindowMinutes < 1)
            return BadRequest(new { error = "InitialWindowMinutes must be at least 1." });

        if (request.MaxRequests < 1)
            return BadRequest(new { error = "MaxRequests must be at least 1." });

        Guid runId;
        try
        {
            runId = await _ingestionService.StartBackfillRangeAsync(
                competitionId, fromUtc, untilUtc,
                request.InitialWindowMinutes, request.MaxRequests, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        return Accepted(new
        {
            message        = "Range backfill started. Poll GET runs/{runId} for status.",
            ingestionRunId = runId,
            competitionId,
            statusUrl      = $"/api/ingestion/competitions/{competitionId}/runs/{runId}"
        });
    }

    /// <summary>
    /// Returns the current status and persisted result of a range backfill run.
    /// Safe to poll repeatedly — read-only.
    /// </summary>
    [HttpGet("competitions/{competitionId:guid}/runs/{runId:guid}")]
    public async Task<ActionResult<object>> GetRunStatus(
        Guid competitionId, Guid runId, CancellationToken ct)
    {
        var run = await _db.IngestionRuns
            .FirstOrDefaultAsync(r => r.Id == runId && r.CompetitionId == competitionId, ct);

        if (run is null)
            return NotFound(new { error = "IngestionRun not found." });

        return Ok(new
        {
            run.Id,
            run.CompetitionId,
            run.Status,
            run.IsComplete,
            run.FromUtc,
            run.UntilUtc,
            run.NextWindowFromUtc,
            run.StartedAtUtc,
            run.FinishedAtUtc,
            run.ImportedCount,
            run.UpdatedExistingCount,
            run.SkippedDuplicateCount,
            run.AutoExcludedCount,
            run.FailedCount,
            run.ErrorMessage,
            ResultSummary = run.ResultSummaryJson
        });
    }

    /// <summary>
    /// Resumes a BudgetExceeded or IncompleteCoverage range backfill from its last checkpoint.
    /// Returns 202 Accepted with the same run ID.
    /// </summary>
    [HttpPost("competitions/{competitionId:guid}/runs/{runId:guid}/resume")]
    public async Task<ActionResult<object>> ResumeBackfillRange(
        Guid competitionId,
        Guid runId,
        [FromQuery] int maxRequests = 3000,
        CancellationToken ct = default)
    {
        if (maxRequests < 1)
            return BadRequest(new { error = "maxRequests must be at least 1." });

        try
        {
            await _ingestionService.ResumeBackfillRangeAsync(competitionId, runId, maxRequests, ct);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        return Accepted(new
        {
            message        = "Backfill resumed. Poll GET runs/{runId} for status.",
            ingestionRunId = runId,
            competitionId,
            statusUrl      = $"/api/ingestion/competitions/{competitionId}/runs/{runId}"
        });
    }
}
