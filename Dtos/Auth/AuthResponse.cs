namespace CompetitionManagementSystem.Dtos.Auth;

public sealed record AuthResponse(string Token, DateTime ExpiresAtUtc, string UserId, string Email, string FullName, IReadOnlyList<string> Roles);
