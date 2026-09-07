using System.Data;
using System.Security.Claims;
using System.Text.Json;
using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Dtos.Participations;
using CompetitionManagementSystem.Models;
using CompetitionManagementSystem.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CompetitionManagementSystem.Controllers;

[ApiController]
[Route("api/participations")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public sealed class ParticipationsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public ParticipationsController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin},{SystemRoles.GeneralSupervisor}")]
    public async Task<ActionResult<IReadOnlyList<ParticipationResponse>>> GetAll(
        [FromQuery] Guid? competitionId,
        [FromQuery] string? authorUserName,
        [FromQuery] ParticipationStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.Participations.AsNoTracking().Where(p => p.IsActive);

        if (competitionId.HasValue)
            query = query.Where(p => p.CompetitionId == competitionId);

        if (status.HasValue)
            query = query.Where(p => p.Status == status.Value);

        if (!string.IsNullOrWhiteSpace(authorUserName))
            query = query.Where(p => p.AuthorUserName.Contains(authorUserName));

        var rows = await query
            .OrderByDescending(p => p.ImportedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(rows.Select(ToResponse).ToList());
    }

    [HttpGet("pending-approval")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin},{SystemRoles.GeneralSupervisor}")]
    public async Task<ActionResult<IReadOnlyList<ParticipationResponse>>> GetPendingApproval(
        [FromQuery] Guid competitionId,
        [FromQuery] string? authorUserName,
        CancellationToken ct = default)
    {
        var query = _db.Participations.AsNoTracking()
            .Where(p => p.CompetitionId == competitionId && p.IsActive && p.Status == ParticipationStatus.PendingApproval);

        if (!string.IsNullOrWhiteSpace(authorUserName))
            query = query.Where(p => p.AuthorUserName.Contains(authorUserName.Trim()));

        var rows = await query
            .OrderBy(p => p.ImportedAtUtc)
            .ToListAsync(ct);

        return Ok(rows.Select(ToResponse).ToList());
    }

    [HttpGet("pending-approval/paged")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin},{SystemRoles.GeneralSupervisor}")]
    public async Task<ActionResult> GetPendingApprovalPaged(
        [FromQuery] Guid competitionId,
        [FromQuery] string? authorUserName,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.Participations.AsNoTracking()
            .Where(p => p.CompetitionId == competitionId && p.IsActive && p.Status == ParticipationStatus.PendingApproval);

        if (!string.IsNullOrWhiteSpace(authorUserName))
            query = query.Where(p => p.AuthorUserName.Contains(authorUserName.Trim()));

        var totalCount = await query.CountAsync(ct);

        var rows = await query
            .OrderBy(p => p.ImportedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new
        {
            page,
            pageSize,
            totalCount,
            totalPages = (int)Math.Ceiling((double)totalCount / pageSize),
            items = rows.Select(ToResponse).ToList()
        });
    }

    // Kept for backward compatibility with the current frontend. New flow should call start-next.
    [HttpGet("judge-queue")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = SystemRoles.Judge)]
    public async Task<ActionResult<IReadOnlyList<ParticipationResponse>>> GetJudgeQueue([FromQuery] Guid competitionId, CancellationToken ct)
    {
        var rows = await _db.Participations.AsNoTracking()
            .Where(p => p.CompetitionId == competitionId && p.IsActive)
            .Where(p => p.Status == ParticipationStatus.ApprovedForJudging)
            .Where(p => !_db.ParticipationScores.Any(s =>
                s.CompetitionId == competitionId &&
                s.ParticipationId == p.Id))
            .OrderBy(p => p.ImportedAtUtc)
            .ToListAsync(ct);

        return Ok(rows.Select(ToResponse).ToList());
    }

    //[HttpPost("judging/start-next")]
    //[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = SystemRoles.Judge)]
    //public async Task<ActionResult<ParticipationResponse>> StartNextForJudging([FromQuery] Guid competitionId, CancellationToken ct)
    //{
    //    var judgeId = User.FindFirstValue(ClaimTypes.NameIdentifier);
    //    if (judgeId is null)
    //        return Unauthorized();

    //    await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    //    var competition = await _db.Competitions.FirstOrDefaultAsync(c => c.Id == competitionId, ct);
    //    if (competition is null)
    //        return NotFound("Competition not found.");

    //    if (!competition.JudgingStarted)
    //        return BadRequest("Judging has not been started by Admin/Supervisor yet.");

    //    if (DateTime.UtcNow < competition.EndDateUtc)
    //        return BadRequest("Judging can start only after competition end date.");

    //    var judgeScoreCount = await _db.ParticipationScores.CountAsync(s => s.CompetitionId == competitionId && s.JudgeUserId == judgeId, ct);
    //    if (judgeScoreCount >= competition.RequiredScoresPerJudge)
    //        return BadRequest("Required scores count for this judge has already been completed.");

    //    var participation = await _db.Participations
    //        .Where(p => p.CompetitionId == competitionId && p.IsActive)
    //        .Where(p => p.Status == ParticipationStatus.ApprovedForJudging)
    //        .Where(p => !_db.ParticipationScores.Any(s => s.CompetitionId == competitionId && s.ParticipationId == p.Id))
    //        .OrderBy(p => p.ImportedAtUtc)
    //        .FirstOrDefaultAsync(ct);

    //    if (participation is null)
    //        return NotFound("No available participation for judging.");

    //    var oldValue = JsonSerializer.Serialize(new { participation.Status, participation.ReservedByJudgeUserId, participation.ReservedAtUtc });
    //    participation.Status = ParticipationStatus.ReservedForJudging;
    //    participation.ReservedByJudgeUserId = judgeId;
    //    participation.ReservedAtUtc = DateTime.UtcNow;

    //    AddAudit("Participation", participation.Id.ToString(), "ParticipationReservedForJudging", oldValue, new { participation.Status, participation.ReservedByJudgeUserId, participation.ReservedAtUtc });
    //    await _db.SaveChangesAsync(ct);
    //    await transaction.CommitAsync(ct);

    //    return Ok(ToResponse(participation));
    //}

    [HttpPost("judging/start-next")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = SystemRoles.Judge)]
    public async Task<ActionResult<ParticipationResponse>> StartNextForJudging(
    [FromQuery] Guid competitionId,
    CancellationToken ct)
    {
        var judgeId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (judgeId is null)
            return Unauthorized();

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var competition = await _db.Competitions
            .FirstOrDefaultAsync(c => c.Id == competitionId, ct);

        if (competition is null)
            return NotFound("Competition not found.");

        if (!competition.JudgingStarted)
            return BadRequest("Judging has not been started by Admin/Supervisor yet.");

        if (DateTime.UtcNow < competition.EndDateUtc)
            return BadRequest("Judging can start only after competition end date.");

        // لو الحكم عنده مشاركة محجوزة ولسه ما حكمهاش، رجّعها له بدل ما تحجز له واحدة جديدة
        var alreadyReserved = await _db.Participations
            .AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.CompetitionId == competitionId &&
                p.IsActive &&
                p.Status == ParticipationStatus.ReservedForJudging &&
                p.ReservedByJudgeUserId == judgeId,
                ct);

        if (alreadyReserved is not null)
        {
            await transaction.CommitAsync(ct);
            return Ok(ToResponse(alreadyReserved));
        }

        // عدد المشاركات التي حكمها هذا الحكم بالفعل
        var judgeScoreCount = await _db.ParticipationScores.CountAsync(
            s => s.CompetitionId == competitionId &&
                 s.JudgeUserId == judgeId,
            ct);

        if (judgeScoreCount >= competition.RequiredScoresPerJudge)
            return BadRequest("Required scores count for this judge has already been completed.");

        // هات أول مشاركة معتمدة للتحكيم ولم يتم حجزها أو تحكيمها
        var participation = await _db.Participations
            .Where(p => p.CompetitionId == competitionId && p.IsActive)
            .Where(p => p.Status == ParticipationStatus.ApprovedForJudging)
            .Where(p => p.ReservedByJudgeUserId == null)
            .Where(p => !_db.ParticipationScores.Any(s =>
                s.CompetitionId == competitionId &&
                s.ParticipationId == p.Id))
            .OrderBy(p => p.ImportedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (participation is null)
            return NotFound("No available participation for judging.");

        var oldValue = JsonSerializer.Serialize(new
        {
            participation.Status,
            participation.ReservedByJudgeUserId,
            participation.ReservedAtUtc
        });

        participation.Status = ParticipationStatus.ReservedForJudging;
        participation.ReservedByJudgeUserId = judgeId;
        participation.ReservedAtUtc = DateTime.UtcNow;

        AddAudit(
            "Participation",
            participation.Id.ToString(),
            "ParticipationReservedForJudging",
            oldValue,
            new
            {
                participation.Status,
                participation.ReservedByJudgeUserId,
                participation.ReservedAtUtc
            });

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Ok(ToResponse(participation));
    }



    [HttpGet("completed-by-me")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = SystemRoles.Judge)]
    public async Task<ActionResult<IReadOnlyList<ParticipationResponse>>> GetCompletedByMe([FromQuery] Guid competitionId, CancellationToken ct)
    {
        var judgeId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (judgeId is null)
            return Unauthorized();

        var rows = await _db.Participations.AsNoTracking()
            .Where(p => p.CompetitionId == competitionId && p.IsActive)
            .Where(p => _db.ParticipationScores.Any(s =>
                s.CompetitionId == competitionId &&
                s.ParticipationId == p.Id &&
                s.JudgeUserId == judgeId))
            .OrderByDescending(p => p.LastScoredAtUtc)
            .ToListAsync(ct);

        // Map participationId → the current judge's own score details. Filtered server-side by
        // JudgeUserId so another judge's score/notes/justification is never returned by this endpoint.
        var scoreDetailsByParticipation = await _db.ParticipationScores.AsNoTracking()
            .Where(s => s.CompetitionId == competitionId
                        && s.JudgeUserId == judgeId
                        && rows.Select(p => p.Id).Contains(s.ParticipationId))
            .Select(s => new { s.ParticipationId, s.Id, s.Score, s.Notes, s.Justification })
            .ToDictionaryAsync(s => s.ParticipationId, ct);

        return Ok(rows.Select(p =>
        {
            var response = ToResponse(p);
            if (scoreDetailsByParticipation.TryGetValue(p.Id, out var d))
            {
                response = response with
                {
                    ScoreId = d.Id,
                    Score = d.Score,
                    Notes = d.Notes,
                    Justification = d.Justification
                };
            }
            return response;
        }).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ParticipationResponse>> GetById(Guid id, CancellationToken ct)
    {
        var row = await _db.Participations.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null)
            return NotFound();
        return Ok(ToResponse(row));
    }

    [HttpGet("{id:guid}/raw-json")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin},{SystemRoles.GeneralSupervisor},{SystemRoles.Judge}")]
    public async Task<IActionResult> GetRawJson(Guid id, CancellationToken ct)
    {
        var raw = await _db.Participations.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => p.RawJsonData)
            .FirstOrDefaultAsync(ct);

        if (raw is null)
            return NotFound();
        return Content(raw, "application/json");
    }

    [HttpGet("{id:guid}/cached-snapshot")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin},{SystemRoles.GeneralSupervisor},{SystemRoles.Judge}")]
    public async Task<IActionResult> GetCachedSnapshot(Guid id, CancellationToken ct)
    {
        var raw = await _db.Participations.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => p.CachedPostSnapshotJson)
            .FirstOrDefaultAsync(ct);

        if (raw is null)
            return NotFound();
        return Content(raw, "application/json");
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin},{SystemRoles.GeneralSupervisor}")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var row = await _db.Participations.FindAsync([id], ct);
        if (row is null)
            return NotFound();

        if (row.Status is ParticipationStatus.AutoExcluded or ParticipationStatus.ManuallyExcluded or ParticipationStatus.Scored)
            return BadRequest("This participation cannot be approved in its current status.");

        var oldValue = JsonSerializer.Serialize(new { row.Status, row.ApprovedByUserId, row.ApprovedAtUtc });
        row.Status = ParticipationStatus.ApprovedForJudging;
        row.ApprovedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        row.ApprovedAtUtc = DateTime.UtcNow;
        row.IsActive = true;

        AddAudit("Participation", row.Id.ToString(), "ParticipationApproved", oldValue, new { row.Status, row.ApprovedByUserId, row.ApprovedAtUtc });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/exclude")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin},{SystemRoles.GeneralSupervisor}")]
    public async Task<IActionResult> Exclude(Guid id, ExcludeParticipationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest("Reason is required.");

        var row = await _db.Participations.FindAsync([id], ct);
        if (row is null)
            return NotFound();

        if (row.Status == ParticipationStatus.Scored)
            return BadRequest("Scored participation cannot be excluded.");

        var oldValue = JsonSerializer.Serialize(new { row.Status, row.ExclusionReason, row.IsActive });
        row.Status = ParticipationStatus.ManuallyExcluded;
        row.ExclusionReason = request.Reason.Trim();
        row.IsActive = false;

        AddAudit("Participation", row.Id.ToString(), "ParticipationManuallyExcluded", oldValue, new { row.Status, row.ExclusionReason, row.IsActive });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }


    [HttpPost("{id:guid}/select-tie-winner")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin},{SystemRoles.GeneralSupervisor}")]
    public async Task<IActionResult> SelectTieWinner(Guid id, SelectTieWinnerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest("Reason is required.");

        var row = await _db.Participations.FindAsync([id], ct);
        if (row is null)
            return NotFound();

        if (row.Status != ParticipationStatus.Scored)
            return BadRequest("Only scored participations can be manually selected in tie cases.");

        var oldValue = JsonSerializer.Serialize(new { row.IsManualTieWinner, row.TieSelectionReason, row.TieSelectedByUserId, row.TieSelectedAtUtc });
        row.IsManualTieWinner = true;
        row.TieSelectionReason = request.Reason.Trim();
        row.TieSelectedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        row.TieSelectedAtUtc = DateTime.UtcNow;

        AddAudit("Participation", row.Id.ToString(), "TieWinnerSelectedManually", oldValue, new { row.IsManualTieWinner, row.TieSelectionReason, row.TieSelectedByUserId, row.TieSelectedAtUtc });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/deactivate")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = SystemRoles.SystemAdmin)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var row = await _db.Participations.FindAsync([id], ct);
        if (row is null)
            return NotFound();

        var oldValue = JsonSerializer.Serialize(new { row.IsActive, row.Status });
        row.IsActive = false;
        if (row.Status != ParticipationStatus.Scored)
        {
            row.Status = ParticipationStatus.ManuallyExcluded;
            row.ExclusionReason ??= "Deactivated by admin.";
        }

        AddAudit("Participation", row.Id.ToString(), "ParticipationDeactivated", oldValue, new { row.IsActive, row.Status, row.ExclusionReason });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static ParticipationResponse ToResponse(Models.Participation p) =>
        new(
            p.Id, p.CompetitionId, p.Platform, p.ExternalPostId, p.ExternalPostUrl, p.Text,
            ToSaudiTime(p.CreatedAtOnPlatformUtc), ToSaudiTime(p.ImportedAtUtc), p.AuthorUserName, p.AuthorDisplayName,
            p.AuthorDescription, p.AuthorLocation, p.AuthorFollowers, p.AuthorIsBlueVerified,
            p.LikeCount, p.RetweetCount, p.ReplyCount, p.QuoteCount, p.ViewCount,
            p.IsReply, p.InReplyToId, p.IsActive, p.Status, p.ExclusionReason, p.IsAutoExcluded,
            p.HasVideo, p.HasPrimaryHashtag, p.HasSecondaryHashtag, p.ReservedByJudgeUserId,
            p.ReservedAtUtc, p.ScoredByJudgeUserId, p.ScoredAtUtc, p.CachedMediaUrlsJson,
            p.CachedPostSnapshotJson, p.IsManualTieWinner, p.TieSelectionReason, p.TieSelectedByUserId,
            p.TieSelectedAtUtc, p.FinalScore, p.ScoreCount, p.LastScoredAtUtc);

    private void AddAudit(string entityType, string entityId, string action, object? oldValue, object? newValue)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            OldValueJson = oldValue is null ? null : oldValue as string ?? JsonSerializer.Serialize(oldValue),
            NewValueJson = newValue is null ? null : JsonSerializer.Serialize(newValue),
            UserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
            UserEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name,
            UserRole = string.Join(',', User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value)),
            IpAddress = Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded) ? forwarded.ToString().Split(',')[0].Trim() : HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    private static DateTime ToSaudiTime(DateTime utcDate)
    {
        if (utcDate.Kind == DateTimeKind.Unspecified)
            utcDate = DateTime.SpecifyKind(utcDate, DateTimeKind.Utc);

        var timeZoneId = OperatingSystem.IsWindows()
            ? "Arab Standard Time"
            : "Asia/Riyadh";

        var saudiZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return TimeZoneInfo.ConvertTimeFromUtc(utcDate, saudiZone);
    }
}
