namespace CompetitionManagementSystem.Dtos.Users;

public sealed record CreateUserRequest(string Email, string Password, string FullName, string Role);
