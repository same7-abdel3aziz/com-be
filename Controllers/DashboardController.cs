using CompetitionManagementSystem.Security;
using CompetitionManagementSystem.Services.Reporting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CompetitionManagementSystem.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin},{SystemRoles.GeneralSupervisor}")]
public sealed class DashboardController : ControllerBase
{
    private readonly IReportService _reportService;

    public DashboardController(IReportService reportService)
    {
        _reportService = reportService;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] Guid? competitionId, CancellationToken ct)
    {
        return Ok(await _reportService.GetDashboardAsync(competitionId, ct));
    }
}
