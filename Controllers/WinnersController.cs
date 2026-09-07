using System.Security.Claims;
using System.Text.Json;
using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Dtos.Winners;
using CompetitionManagementSystem.Models;
using CompetitionManagementSystem.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CompetitionManagementSystem.Controllers;

[ApiController]
[Route("api/winners")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin},{SystemRoles.GeneralSupervisor}")]
public sealed class WinnersController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public WinnersController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet("judging-status")]
    public async Task<ActionResult<JudgingStatusResponse>> GetJudgingStatus(
        [FromQuery] Guid competitionId,
        CancellationToken ct)
    {
        var competitionExists = await _db.Competitions
            .AnyAsync(c => c.Id == competitionId, ct);

        if (!competitionExists)
            return NotFound("Competition not found.");

        var approvedForJudgingCount = await _db.Participations.CountAsync(p =>
            p.CompetitionId == competitionId &&
            p.IsActive &&
            p.Status == ParticipationStatus.ApprovedForJudging,
            ct);

        var reservedForJudgingCount = await _db.Participations.CountAsync(p =>
            p.CompetitionId == competitionId &&
            p.IsActive &&
            p.Status == ParticipationStatus.ReservedForJudging,
            ct);

        var scoredCount = await _db.Participations.CountAsync(p =>
            p.CompetitionId == competitionId &&
            p.IsActive &&
            p.Status == ParticipationStatus.Scored,
            ct);

        var totalStillWaiting = approvedForJudgingCount + reservedForJudgingCount;

        var isJudgingCompleted = totalStillWaiting == 0 && scoredCount > 0;

        return Ok(new JudgingStatusResponse(
            competitionId,
            isJudgingCompleted,
            approvedForJudgingCount,
            reservedForJudgingCount,
            scoredCount,
            totalStillWaiting));
    }

    [HttpGet("candidates")]
    public async Task<ActionResult<IReadOnlyList<WinnerCandidateResponse>>> GetCandidates(
        [FromQuery] Guid competitionId,
        CancellationToken ct)
    {
        var competitionExists = await _db.Competitions
            .AnyAsync(c => c.Id == competitionId, ct);

        if (!competitionExists)
            return NotFound("Competition not found.");

        var rows = await _db.Participations
            .AsNoTracking()
    .Where(p =>
    p.CompetitionId == competitionId &&
    p.IsActive &&
    p.Status == ParticipationStatus.Scored &&
    p.ScoreCount > 0 &&
    !_db.Winners.Any(w =>
        w.CompetitionId == competitionId &&
        w.ParticipationId == p.Id &&
        w.IsActive))
            .OrderByDescending(p => p.FinalScore)
            .ThenByDescending(p => p.IsManualTieWinner)
            .ThenByDescending(p => p.ViewCount)
            .ThenByDescending(p => p.LikeCount + p.RetweetCount + p.ReplyCount + p.QuoteCount)
            .Select(p => new WinnerCandidateResponse(
                p.Id,
                p.AuthorUserName,
                p.AuthorDisplayName,
                p.ExternalPostUrl,
                p.Text,
                p.FinalScore,
                p.ScoreCount,
                p.ScoredAtUtc,
                p.IsManualTieWinner,
                p.TieSelectionReason))
            .ToListAsync(ct);

        return Ok(rows);
    }

    [HttpPost("select")]
    public async Task<ActionResult<IReadOnlyList<WinnerResponse>>> SelectWinners(
        SelectWinnersRequest request,
        CancellationToken ct)
    {
        var selectedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (selectedByUserId is null)
            return Unauthorized();

        var competition = await _db.Competitions
            .FirstOrDefaultAsync(c => c.Id == request.CompetitionId, ct);

        if (competition is null)
            return NotFound("Competition not found.");

        var selectedIds = new[]
        {
            request.FirstPlaceParticipationId,
            request.SecondPlaceParticipationId,
            request.ThirdPlaceParticipationId
        };

        if (selectedIds.Distinct().Count() != 3)
            return BadRequest("Winner participation IDs must be different.");

        var participations = await _db.Participations
            .Where(p =>
                p.CompetitionId == request.CompetitionId &&
                p.IsActive &&
                selectedIds.Contains(p.Id))
            .ToListAsync(ct);

        if (participations.Count != 3)
            return BadRequest("One or more selected participations were not found in this competition.");

        if (participations.Any(p => p.Status != ParticipationStatus.Scored || p.ScoreCount <= 0))
            return BadRequest("Only scored participations can be selected as winners.");

        var oldWinners = await _db.Winners
            .Where(w => w.CompetitionId == request.CompetitionId && w.IsActive)
            .ToListAsync(ct);

        foreach (var oldWinner in oldWinners)
            oldWinner.IsActive = false;

        var now = DateTime.UtcNow;

        var winners = new List<Winner>
        {
            CreateWinner(request.CompetitionId, request.FirstPlaceParticipationId, 1, participations, selectedByUserId, now),
            CreateWinner(request.CompetitionId, request.SecondPlaceParticipationId, 2, participations, selectedByUserId, now),
            CreateWinner(request.CompetitionId, request.ThirdPlaceParticipationId, 3, participations, selectedByUserId, now)
        };

        _db.Winners.AddRange(winners);

        _db.AuditLogs.Add(new AuditLog
        {
            EntityType = "Competition",
            EntityId = request.CompetitionId.ToString(),
            Action = "WinnersSelected",
            NewValueJson = JsonSerializer.Serialize(new
            {
                request.CompetitionId,
                Winners = winners.Select(w => new
                {
                    w.ParticipationId,
                    w.Rank,
                    w.FinalScore
                })
            }),
            UserId = selectedByUserId,
            UserEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name,
            UserRole = string.Join(',', User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value)),
            IpAddress = Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded)
                ? forwarded.ToString().Split(',')[0].Trim()
                : HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);

        var response = await BuildWinnerResponse(request.CompetitionId, ct);
        return Ok(response);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WinnerResponse>>> GetWinners(
        [FromQuery] Guid competitionId,
        CancellationToken ct)
    {
        var response = await BuildWinnerResponse(competitionId, ct);
        return Ok(response);
    }

    private static Winner CreateWinner(
        Guid competitionId,
        Guid participationId,
        int rank,
        IReadOnlyList<Participation> participations,
        string selectedByUserId,
        DateTime now)
    {
        var participation = participations.First(p => p.Id == participationId);

        return new Winner
        {
            CompetitionId = competitionId,
            ParticipationId = participationId,
            Rank = rank,
            FinalScore = participation.FinalScore,
            SelectedByUserId = selectedByUserId,
            SelectedAtUtc = now,
            IsActive = true
        };
    }

    private async Task<IReadOnlyList<WinnerResponse>> BuildWinnerResponse(Guid competitionId, CancellationToken ct)
    {
        return await _db.Winners
            .AsNoTracking()
            .Include(w => w.Participation)
            .Where(w => w.CompetitionId == competitionId && w.IsActive)
            .OrderBy(w => w.Rank)
            .Select(w => new WinnerResponse(
                w.Id,
                w.CompetitionId,
                w.ParticipationId,
                w.Rank,
                w.FinalScore,
                w.Participation.AuthorUserName,
                w.Participation.AuthorDisplayName,
                w.Participation.ExternalPostUrl,
                w.SelectedAtUtc,
                w.SelectedByUserId,
                _db.Users
                    .Where(u => u.Id == w.SelectedByUserId)
                    .Select(u => u.FullName)
                    .FirstOrDefault(),
                _db.Users
                    .Where(u => u.Id == w.SelectedByUserId)
                    .Select(u => u.Email)
                    .FirstOrDefault()
            ))
            .ToListAsync(ct);
    }
    [HttpPost("select-rank")]
    public async Task<ActionResult<IReadOnlyList<WinnerResponse>>> SelectWinnerRank(
    SelectWinnerRankRequest request,
    CancellationToken ct)
    {
        var selectedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (selectedByUserId is null)
            return Unauthorized();

        if (request.Rank < 1 || request.Rank > 120)
            return BadRequest("Rank must be between 1 and 120.");

        var competitionExists = await _db.Competitions
            .AnyAsync(c => c.Id == request.CompetitionId, ct);

        if (!competitionExists)
            return NotFound("Competition not found.");

        var participation = await _db.Participations
            .FirstOrDefaultAsync(p =>
                p.Id == request.ParticipationId &&
                p.CompetitionId == request.CompetitionId &&
                p.IsActive,
                ct);

        if (participation is null)
            return BadRequest("Selected participation was not found in this competition.");

        if (participation.Status != ParticipationStatus.Scored || participation.ScoreCount <= 0)
            return BadRequest("Only scored participations can be selected as winners.");

        var activeWinners = await _db.Winners
            .Where(w => w.CompetitionId == request.CompetitionId && w.IsActive)
            .ToListAsync(ct);

        var sameRankWinner = activeWinners.FirstOrDefault(w => w.Rank == request.Rank);
        if (sameRankWinner is not null)
            sameRankWinner.IsActive = false;

        var sameParticipationWinner = activeWinners.FirstOrDefault(w => w.ParticipationId == request.ParticipationId);
        if (sameParticipationWinner is not null)
            sameParticipationWinner.IsActive = false;

        var now = DateTime.UtcNow;

        var winner = new Winner
        {
            CompetitionId = request.CompetitionId,
            ParticipationId = request.ParticipationId,
            Rank = request.Rank,
            FinalScore = participation.FinalScore,
            SelectedByUserId = selectedByUserId,
            SelectedAtUtc = now,
            IsActive = true
        };

        _db.Winners.Add(winner);

        _db.AuditLogs.Add(new AuditLog
        {
            EntityType = "Competition",
            EntityId = request.CompetitionId.ToString(),
            Action = "WinnerRankSelected",
            NewValueJson = JsonSerializer.Serialize(new
            {
                request.CompetitionId,
                request.ParticipationId,
                request.Rank,
                participation.FinalScore
            }),
            UserId = selectedByUserId,
            UserEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name,
            UserRole = string.Join(',', User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value)),
            IpAddress = Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded)
                ? forwarded.ToString().Split(',')[0].Trim()
                : HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);

        var response = await BuildWinnerResponse(request.CompetitionId, ct);
        return Ok(response);
    }
}