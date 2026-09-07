namespace CompetitionManagementSystem.Dtos.Auth;

// Public registration is intentionally safe: no Role field exists here.
public sealed record RegisterRequest(string Email, string Password, string FullName);
