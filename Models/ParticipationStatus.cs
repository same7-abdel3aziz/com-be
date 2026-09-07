namespace CompetitionManagementSystem.Models;

public enum ParticipationStatus
{
    Imported = 0,
    AutoExcluded = 1,
    PendingApproval = 2,
    ApprovedForJudging = 3,
    ReservedForJudging = 4,
    Scored = 5,
    ManuallyExcluded = 6
}
