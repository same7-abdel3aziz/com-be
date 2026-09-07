namespace CompetitionManagementSystem.Security;

public static class SystemRoles
{
    public const string SystemAdmin = "SystemAdmin";
    public const string GeneralSupervisor = "GeneralSupervisor";
    public const string Judge = "Judge";

    public static readonly string[] All = [SystemAdmin, GeneralSupervisor, Judge];
}
