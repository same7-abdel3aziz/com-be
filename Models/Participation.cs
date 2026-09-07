namespace CompetitionManagementSystem.Models;

public sealed class Participation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompetitionId { get; set; }
    public Competition? Competition { get; set; }

    public string Platform { get; set; } = "X";
    public string ExternalPostId { get; set; } = string.Empty;
    public string ExternalPostUrl { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtOnPlatformUtc { get; set; }
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;

    public string AuthorUserName { get; set; } = string.Empty;
    public string AuthorDisplayName { get; set; } = string.Empty;
    public string? AuthorDescription { get; set; }
    public string? AuthorLocation { get; set; }
    public int AuthorFollowers { get; set; }
    public bool AuthorIsBlueVerified { get; set; }

    public int LikeCount { get; set; }
    public int RetweetCount { get; set; }
    public int ReplyCount { get; set; }
    public int QuoteCount { get; set; }
    public int ViewCount { get; set; }
    public bool IsReply { get; set; }
    public string? InReplyToId { get; set; }

    // Full raw JSON response from twitterapi.io. This is the source-of-truth vault.
    public string RawJsonData { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
    public ParticipationStatus Status { get; set; } = ParticipationStatus.Imported;
    public string? ExclusionReason { get; set; }
    public bool IsAutoExcluded { get; set; }
    public string? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? ReservedByJudgeUserId { get; set; }
    public DateTime? ReservedAtUtc { get; set; }
    public string? ScoredByJudgeUserId { get; set; }
    public DateTime? ScoredAtUtc { get; set; }
    public bool HasVideo { get; set; }
    public bool HasPrimaryHashtag { get; set; }
    public bool HasSecondaryHashtag { get; set; }
    public string? CachedMediaUrlsJson { get; set; }
    public string? CachedPostSnapshotJson { get; set; }
    public bool IsManualTieWinner { get; set; }
    public string? TieSelectionReason { get; set; }
    public string? TieSelectedByUserId { get; set; }
    public DateTime? TieSelectedAtUtc { get; set; }

    // Precomputed report fields. Updated transactionally when a judge submits a score.
    public decimal ScoreSum { get; set; }
    public int ScoreCount { get; set; }
    public decimal FinalScore { get; set; }
    public DateTime? LastScoredAtUtc { get; set; }

    public ICollection<ParticipationScore> Scores { get; set; } = new List<ParticipationScore>();
    public ICollection<ParticipationExternalUrl> ExternalUrls { get; set; } = new List<ParticipationExternalUrl>();
}
