using CompetitionManagementSystem.Dtos.Scores;
using CompetitionManagementSystem.Security;
using CompetitionManagementSystem.Services.Scoring;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace CompetitionManagementSystem.Controllers;

[ApiController]
[Route("api/scores")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.Judge}")]
public sealed class ScoresController : ControllerBase
{
    private readonly IScoringService _scoringService;

    public ScoresController(IScoringService scoringService)
    {
        _scoringService = scoringService;
    }

    [HttpPost("participations/{participationId:guid}")]
    public async Task<ActionResult<ScoreResponse>> SubmitScore(Guid participationId, SubmitScoreRequest request, CancellationToken ct)
    {
        var judgeId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (judgeId is null)
            return Unauthorized();

        var ipAddress = Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded)
            ? forwarded.ToString().Split(',')[0].Trim()
            : HttpContext.Connection.RemoteIpAddress?.ToString();

        var judgeEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name;
        var judgeRole = string.Join(',', User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value));

        try
        {
            var result = await _scoringService.SubmitScoreAsync(participationId, judgeId, judgeEmail, judgeRole, ipAddress, request.Score, request.Notes, request.Justification, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Updates a score the current judge already submitted (participation is in Scored state,
    /// referred to as "Completed" in product language). The participation stays in Scored state;
    /// the original ScoredAt timestamp and ScoredByJudgeUserId are preserved. Audit log records
    /// the change.
    /// </summary>
    [HttpPut("{scoreId:guid}")]
    public async Task<ActionResult<ScoreResponse>> UpdateScore(Guid scoreId, UpdateScoreRequest request, CancellationToken ct)
    {
        var judgeId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (judgeId is null)
            return Unauthorized();

        var ipAddress = Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded)
            ? forwarded.ToString().Split(',')[0].Trim()
            : HttpContext.Connection.RemoteIpAddress?.ToString();

        var judgeEmail = User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name;
        var judgeRole = string.Join(',', User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value));

        try
        {
            var result = await _scoringService.UpdateScoreAsync(scoreId, judgeId, judgeEmail, judgeRole, ipAddress, request.Score, request.Notes, request.Justification, ct);
            return Ok(result);
        }
        catch (ForbiddenScoreActionException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
