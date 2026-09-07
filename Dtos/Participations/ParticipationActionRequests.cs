namespace CompetitionManagementSystem.Dtos.Participations;

public sealed record ExcludeParticipationRequest(string Reason);

public sealed record SelectTieWinnerRequest(string Reason);
