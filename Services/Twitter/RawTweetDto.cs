using System.Text.Json;

namespace CompetitionManagementSystem.Services.Twitter;

public sealed class RawTweetDto
{
    public string Id { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public string AuthorUserName { get; init; } = string.Empty;
    public string AuthorDisplayName { get; init; } = string.Empty;
    public string? AuthorDescription { get; init; }
    public string? AuthorLocation { get; init; }
    public int AuthorFollowers { get; init; }
    public bool AuthorIsBlueVerified { get; init; }
    public int LikeCount { get; init; }
    public int RetweetCount { get; init; }
    public int ReplyCount { get; init; }
    public int QuoteCount { get; init; }
    public int ViewCount { get; init; }
    public bool IsReply { get; init; }
    public string? InReplyToId { get; init; }
    public bool HasVideo { get; init; }
    public string RawJsonData { get; init; } = "{}";
    public IReadOnlyList<RawTweetUrlDto> Urls { get; init; } = [];
    public IReadOnlyList<RawTweetMediaDto> Media { get; init; } = [];
}

public sealed class RawTweetUrlDto
{
    public string Url { get; init; } = string.Empty;
    public string? ExpandedUrl { get; init; }
    public string? DisplayUrl { get; init; }
    public string? RawJsonData { get; init; }
}

public sealed class RawTweetMediaDto
{
    public string Type { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? PreviewImageUrl { get; init; }
    public string? RawJsonData { get; init; }
}
