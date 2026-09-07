using System.Security.Claims;
using System.Text.Json;
using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Dtos.Competitions;
using CompetitionManagementSystem.Models;
using CompetitionManagementSystem.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CompetitionManagementSystem.Controllers;

[ApiController]
[Route("api/competitions")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public sealed class CompetitionsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public CompetitionsController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin},{SystemRoles.GeneralSupervisor},{SystemRoles.Judge}")]
    public async Task<ActionResult<IReadOnlyList<CompetitionResponse>>> GetAll(CancellationToken ct)
    {
        var competitions = await _db.Competitions.AsNoTracking()
            .OrderByDescending(c => c.CreatedAtUtc)
            .ToListAsync(ct);

        return Ok(competitions.Select(ToResponse).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CompetitionResponse>> GetById(Guid id, CancellationToken ct)
    {
        var c = await _db.Competitions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
            return NotFound();
        return Ok(ToResponse(c));
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = SystemRoles.SystemAdmin)]
    public async Task<ActionResult<CompetitionResponse>> Create(CreateCompetitionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Hashtag))
            return BadRequest("Primary hashtag is required.");

        if (string.IsNullOrWhiteSpace(request.SecondaryHashtag))
            return BadRequest("Secondary hashtag is required.");

        var competition = new Competition
        {
            Name = request.Name,
            Description = request.Description,
            Hashtag = NormalizeHashtag(request.Hashtag),
            SecondaryHashtag = NormalizeHashtag(request.SecondaryHashtag),
            StartDateUtc = request.StartDateUtc,
            EndDateUtc = request.EndDateUtc,
            IsActive = request.IsActive,
            PollingIntervalMinutes = request.PollingIntervalMinutes <= 0 ? 10 : request.PollingIntervalMinutes,
            RequiredScoresPerJudge = request.RequiredScoresPerJudge <= 0 ? 20 : request.RequiredScoresPerJudge
        };

        _db.Competitions.Add(competition);
        AddAudit("Competition", competition.Id.ToString(), "CompetitionCreated", null, competition);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { id = competition.Id }, ToResponse(competition));
    }

    [HttpPut("{id:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = SystemRoles.SystemAdmin)]
    public async Task<IActionResult> Update(Guid id, CreateCompetitionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Hashtag))
            return BadRequest("Primary hashtag is required.");

        if (string.IsNullOrWhiteSpace(request.SecondaryHashtag))
            return BadRequest("Secondary hashtag is required.");

        var competition = await _db.Competitions.FindAsync([id], ct);
        if (competition is null)
            return NotFound();

        var oldValue = JsonSerializer.Serialize(competition);

        competition.Name = request.Name;
        competition.Description = request.Description;
        competition.Hashtag = NormalizeHashtag(request.Hashtag);
        competition.SecondaryHashtag = NormalizeHashtag(request.SecondaryHashtag);
        competition.StartDateUtc = request.StartDateUtc;
        competition.EndDateUtc = request.EndDateUtc;
        competition.IsActive = request.IsActive;
        competition.PollingIntervalMinutes = request.PollingIntervalMinutes <= 0 ? 10 : request.PollingIntervalMinutes;
        competition.RequiredScoresPerJudge = request.RequiredScoresPerJudge <= 0 ? 20 : request.RequiredScoresPerJudge;
        competition.UpdatedAtUtc = DateTime.UtcNow;

        AddAudit("Competition", competition.Id.ToString(), "CompetitionUpdated", oldValue, competition);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpPost("{id:guid}/start-judging")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin},{SystemRoles.GeneralSupervisor}")]
    public async Task<IActionResult> StartJudging(Guid id, CancellationToken ct)
    {
        var competition = await _db.Competitions.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (competition is null)
            return NotFound();

        if (DateTime.UtcNow < competition.EndDateUtc)
            return BadRequest("Judging can start only after competition end date.");

        var pendingCount = await _db.Participations.CountAsync(p =>
            p.CompetitionId == id &&
            p.IsActive &&
            p.Status == ParticipationStatus.PendingApproval,
            ct);

        if (pendingCount > 0)
            return BadRequest($"Judging cannot start while {pendingCount} participations are still pending approval.");

        var oldValue = JsonSerializer.Serialize(new { competition.JudgingStarted, competition.JudgingStartedAtUtc });

        competition.JudgingStarted = true;
        competition.JudgingStartedAtUtc = DateTime.UtcNow;
        competition.UpdatedAtUtc = DateTime.UtcNow;

        AddAudit("Competition", competition.Id.ToString(), "JudgingStarted", oldValue, new { competition.JudgingStarted, competition.JudgingStartedAtUtc });
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    //[HttpPost("{id:guid}/start-next")]
    //[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = SystemRoles.Judge)]
    //public async Task<IActionResult> StartNext(Guid id, CancellationToken ct)
    //{
    //    using var tx = await _db.Database.BeginTransactionAsync(ct);

    //    var competition = await _db.Competitions
    //        .FirstOrDefaultAsync(c => c.Id == id, ct);

    //    if (competition is null)
    //        return NotFound("Competition not found.");

    //    if (!competition.JudgingStarted)
    //        return BadRequest("Judging has not started for this competition.");

    //    var currentJudgeId = User.FindFirstValue(ClaimTypes.NameIdentifier);

    //    if (string.IsNullOrWhiteSpace(currentJudgeId))
    //        return Unauthorized();

    //    var reservedJudgeIds = await _db.Participations
    //        .Where(p =>
    //            p.CompetitionId == id &&
    //            p.ReservedByJudgeUserId != null)
    //        .Select(p => p.ReservedByJudgeUserId!)
    //        .ToListAsync(ct);

    //    var scoredJudgeIds = await _db.Participations
    //        .Where(p =>
    //            p.CompetitionId == id &&
    //            p.ScoredByJudgeUserId != null)
    //        .Select(p => p.ScoredByJudgeUserId!)
    //        .ToListAsync(ct);

    //    var existingJudgeIds = reservedJudgeIds
    //        .Concat(scoredJudgeIds)
    //        .Distinct()
    //        .ToList();

    //    var isExistingJudge = existingJudgeIds.Contains(currentJudgeId);

    //    if (!isExistingJudge && existingJudgeIds.Count >= competition.RequiredScoresPerJudge)
    //    {
    //        return BadRequest("Maximum number of judges for this competition has been reached.");
    //    }

    //    var participation = await _db.Participations
    //        .Where(p =>
    //            p.CompetitionId == id &&
    //            p.IsActive &&
    //            p.Status == ParticipationStatus.ApprovedForJudging &&
    //            p.ReservedByJudgeUserId == null)
    //        .OrderBy(p => p.ImportedAtUtc)
    //        .FirstOrDefaultAsync(ct);

    //    if (participation is null)
    //    {
    //        await tx.RollbackAsync(ct);
    //        return NotFound("No available participation for judging.");
    //    }

    //    participation.Status = ParticipationStatus.ReservedForJudging;
    //    participation.ReservedByJudgeUserId = currentJudgeId;
    //    participation.ReservedAtUtc = DateTime.UtcNow;

    //    AddAudit(
    //        "Participation",
    //        participation.Id.ToString(),
    //        "ParticipationReserved",
    //        null,
    //        new
    //        {
    //            participation.Id,
    //            participation.Status,
    //            participation.ReservedByJudgeUserId,
    //            participation.ReservedAtUtc
    //        });

    //    await _db.SaveChangesAsync(ct);
    //    await tx.CommitAsync(ct);

    //    return Ok(new
    //    {
    //        participation.Id,
    //        participation.Status,
    //        participation.ReservedByJudgeUserId,
    //        participation.ReservedAtUtc,
    //        participation.ImportedAtUtc
    //    });
    //}

    private static CompetitionResponse ToResponse(Competition c) =>
        new(c.Id, c.Name, c.Description, c.Hashtag, c.SecondaryHashtag, c.StartDateUtc, c.EndDateUtc,
            c.IsActive, c.PollingIntervalMinutes, c.RequiredScoresPerJudge, c.JudgingStarted,
            c.JudgingStartedAtUtc, c.LastIngestedUntilUtc, c.CreatedAtUtc);

    private static string NormalizeHashtag(string value)
    {
        var clean = value.Trim();
        return clean.StartsWith('#') ? clean : "#" + clean;
    }

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
}
