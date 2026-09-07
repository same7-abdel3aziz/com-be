using System.Reflection;
using CompetitionManagementSystem.Controllers;
using CompetitionManagementSystem.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CompetitionManagementSystem.Tests.Ingestion;

/// <summary>
/// Verifies that the backfill-window endpoint is protected by [Authorize(Roles=SystemAdmin)]
/// at the controller level, and that no [AllowAnonymous] override exists on the action.
///
/// Approach: reflection on the compiled attributes — deterministic, no app-startup needed.
/// The [Authorize] attribute on the controller class is enforced by ASP.NET Core's
/// authorization middleware for every action in that controller unless overridden by
/// [AllowAnonymous] on the action itself.
/// </summary>
public class BackfillWindowAuthorizationTests
{
    private static readonly Type ControllerType = typeof(IngestionController);

    private static MethodInfo GetBackfillAction() =>
        ControllerType.GetMethod("BackfillWindow",
            BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("BackfillWindow action not found on IngestionController");

    // ──────────────────────────────────────────────────────────────────────────
    // TC-AUTH-1: Controller carries [Authorize] with SystemAdmin role
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IngestionController_HasAuthorizeAttribute_WithSystemAdminRole()
    {
        var attr = ControllerType.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attr);
        Assert.Equal(SystemRoles.SystemAdmin, attr!.Roles);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-AUTH-2: Controller uses JwtBearer authentication scheme
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IngestionController_AuthorizeAttribute_UsesJwtBearerScheme()
    {
        var attr = ControllerType.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attr);
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, attr!.AuthenticationSchemes);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-AUTH-3: BackfillWindow action has NO [AllowAnonymous] override
    //             (if it did, the controller-level [Authorize] would be bypassed)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BackfillWindowAction_DoesNotHaveAllowAnonymous()
    {
        var action = GetBackfillAction();
        var attr   = action.GetCustomAttribute<AllowAnonymousAttribute>();

        Assert.Null(attr);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-AUTH-4: BackfillWindow action has no action-level [Authorize] override
    //             (authorization comes from controller class — expected behavior)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BackfillWindowAction_AuthorizationComesFromController_NotOverridden()
    {
        var action        = GetBackfillAction();
        var actionAttr    = action.GetCustomAttribute<AuthorizeAttribute>();
        var controllerAttr = ControllerType.GetCustomAttribute<AuthorizeAttribute>();

        // Action should not override — auth is inherited from controller
        Assert.Null(actionAttr);

        // Controller must have [Authorize(Roles = SystemAdmin)]
        Assert.NotNull(controllerAttr);
        Assert.Equal(SystemRoles.SystemAdmin, controllerAttr!.Roles);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-AUTH-5: BackfillWindow action is [HttpPost] on the expected route
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BackfillWindowAction_IsHttpPost_WithExpectedRoute()
    {
        var action    = GetBackfillAction();
        var httpPost  = action.GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(httpPost);
        Assert.Contains("backfill-window", httpPost!.Template ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-AUTH-6: SystemAdmin role string matches the constant (no typo)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SystemAdminRoleConstant_MatchesAuthorizeAttribute()
    {
        var attr = ControllerType.GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal("SystemAdmin", SystemRoles.SystemAdmin);
        Assert.Equal("SystemAdmin", attr!.Roles);
    }
}
