using System.Text.Json;

namespace CompetitionManagementSystem.Services.Twitter;

public sealed class MockTwitterSearchClient : ITwitterSearchClient
{
    public Task<IReadOnlyList<RawTweetDto>> SearchHashtagAsync(
        string hashtag,
        string? secondaryHashtag,
        DateTime sinceUtc,
        DateTime untilUtc,
        CancellationToken cancellationToken)
    {
        var primary = NormalizeTag(hashtag, "#primary");
        var secondary = NormalizeTag(secondaryHashtag, "#secondary");
        var baseTime = new DateTime(2026, 5, 25, 8, 0, 0, DateTimeKind.Utc);

        var tweets = new List<RawTweetDto>
        {
            BuildTweet(
                idSuffix: "A",
                createdAtUtc: baseTime.AddMinutes(1),
                text: $"تجربة مع كلا الهاشتاجين {primary} {secondary}",
                hasVideo: true),

            BuildTweet(
                idSuffix: "B",
                createdAtUtc: baseTime.AddMinutes(2),
                text: $"تجربة مع الأساسي فقط {primary}",
                hasVideo: true),

            BuildTweet(
                idSuffix: "C",
                createdAtUtc: baseTime.AddMinutes(3),
                text: $"تجربة مع الثانوي فقط {secondary}",
                hasVideo: true),

            BuildTweet(
                idSuffix: "D",
                createdAtUtc: baseTime.AddMinutes(4),
                text: "منشور لا يحتوي على أي هاشتاج مطلوب",
                hasVideo: true),

            BuildTweet(
                idSuffix: "E",
                createdAtUtc: baseTime.AddMinutes(5),
                text: $"تجربة بدون فيديو {primary}",
                hasVideo: false),
        };

        return Task.FromResult<IReadOnlyList<RawTweetDto>>(tweets);
    }

    public Task<TwitterSearchPageResult> FetchPageAsync(
        string expression, DateTime sinceUtc, DateTime untilUtc,
        string queryType, CancellationToken cancellationToken)
        => Task.FromResult(new TwitterSearchPageResult(Array.Empty<RawTweetDto>(), IsSaturated: false));

    private static string NormalizeTag(string? value, string fallback)
    {
        var t = (value ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(t))
            return fallback;
        return t.StartsWith('#') ? t : "#" + t;
    }

    private static RawTweetDto BuildTweet(string idSuffix, DateTime createdAtUtc, string text, bool hasVideo)
    {
        var id = $"mock-{idSuffix}";
        var userName = $"mock_user_{idSuffix}";
        var url = $"https://x.com/{userName}/status/{id}";

        var media = new List<RawTweetMediaDto>();
        if (hasVideo)
        {
            media.Add(new RawTweetMediaDto
            {
                Type = "video",
                Url = $"https://video.example.com/{id}.mp4",
                PreviewImageUrl = $"https://video.example.com/{id}.jpg",
                RawJsonData = JsonSerializer.Serialize(new { type = "video", url = $"https://video.example.com/{id}.mp4" })
            });
        }

        var raw = new
        {
            id,
            url,
            text,
            createdAt = createdAtUtc.ToString("O"),
            likeCount = 10,
            retweetCount = 2,
            replyCount = 1,
            quoteCount = 0,
            viewCount = 100,
            isReply = false,
            inReplyToId = (string?)null,
            author = new
            {
                userName,
                name = $"Mock User {idSuffix}",
                isBlueVerified = false,
                description = "Mock participant from local ingestion",
                location = "Riyadh",
                followers = 500
            }
        };

        return new RawTweetDto
        {
            Id = id,
            Url = url,
            Text = text,
            CreatedAtUtc = createdAtUtc,
            AuthorUserName = userName,
            AuthorDisplayName = $"Mock User {idSuffix}",
            AuthorDescription = "Mock participant from local ingestion",
            AuthorLocation = "Riyadh",
            AuthorFollowers = 500,
            AuthorIsBlueVerified = false,
            LikeCount = 10,
            RetweetCount = 2,
            ReplyCount = 1,
            QuoteCount = 0,
            ViewCount = 100,
            IsReply = false,
            InReplyToId = null,
            HasVideo = hasVideo,
            RawJsonData = JsonSerializer.Serialize(raw),
            Media = media,
            Urls = new List<RawTweetUrlDto>()
        };
    }
}