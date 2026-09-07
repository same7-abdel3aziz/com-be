namespace CompetitionManagementSystem.Dtos.Users;

public sealed record UserResponse(string Id, string Email, string FullName, bool IsActive, IReadOnlyList<string> Roles);
