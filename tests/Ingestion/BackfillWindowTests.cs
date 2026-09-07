using System.Text.Json;
using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Models;
using CompetitionManagementSystem.Options;
using CompetitionManagementSystem.Services.Ingestion;
using CompetitionManagementSystem.Services.Twitter;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CompetitionManagementSystem.Tests.Ingestion;

/// <summary>
/// Tests that BackfillWindowAsync uses the same ProcessTweetsAsync pipeline as
/// RunForCompetitionAsync and never advances LastIngestedUntilUtc.
/// Uses an in-memory EF Core database and a stub ITwitterSearchClient.
/// </summary>
public class BackfillWindowTests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly Competition          _competition;
    private readonly DateTime             _originalCheckpoint;

    public BackfillWindowTests()
    {
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new ApplicationDbContext(dbOptions);

        _originalCheckpoint = new DateTime(2026, 6, 15, 19, 0, 0, DateTimeKind.Utc);
        _competition = new Competition
        {
            Id                    = Guid.NewGuid(),
            Name                  = "Test Competition",
            Hashtag               = "#تحدي5",
            StartDateUtc          = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDateUtc            = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            LastIngestedUntilUtc  = _originalCheckpoint,
            IsActive              = true
        };
        _db.Competitions.Add(_competition);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static RawTweetDto BuildTweet(string id, bool hasVideo = true) => new()
    {
        Id              = id,
        Text            = $"#تحدي5 tweet {id}",
        Url             = $"https://x.com/u/status/{id}",
        CreatedAtUtc    = new DateTime(2026, 6, 15, 18, 33, 45, DateTimeKind.Utc),
        AuthorUserName  = "testuser",
        AuthorDisplayName = "Test",
        HasVideo        = hasVideo,
        RawJsonData     = JsonSerializer.Serialize(new { id }),
        Media           = hasVideo
            ? new List<RawTweetMediaDto> { new() { Type = "video", Url = "https://vid.example.com/v.mp4" } }
            : new List<RawTweetMediaDto>(),
        Urls            = new List<RawTweetUrlDto>()
    };

    private IngestionService CreateService(ITwitterSearchClient twitterClient)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        return new IngestionService(
            _db, twitterClient,
            new OptionsWrapper<TwitterApiIoOptions>(new TwitterApiIoOptions
            {
                ApiKey = "test", UseMock = false, EnableTopSearch = false,
                HashtagSearchMode = "SeparateRequests", DelayBetweenQueryTypesSeconds = 0
            }),
            NullLogger<IngestionService>.Instance,
            sp.GetRequiredService<IServiceScopeFactory>());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-10: Backfill never advances LastIngestedUntilUtc — success path
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BackfillWindow_Success_DoesNotAdvanceCheckpoint()
    {
        var stub   = new StubTwitterClient(new[] { BuildTweet("tweet-abc") });
        var svc    = CreateService(stub);
        var from   = new DateTime(2026, 6, 15, 18, 25, 0, DateTimeKind.Utc);
        var until  = new DateTime(2026, 6, 15, 18, 45, 0, DateTimeKind.Utc);

        await svc.BackfillWindowAsync(_competition.Id, from, until, CancellationToken.None);

        var reloaded = await _db.Competitions.FindAsync(_competition.Id);
        Assert.Equal(_originalCheckpoint, reloaded!.LastIngestedUntilUtc);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-10 (failure path): Backfill failure also must not advance checkpoint
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BackfillWindow_ApiFailure_DoesNotAdvanceCheckpoint()
    {
        var stub = new ThrowingTwitterClient();
        var svc  = CreateService(stub);
        var from  = new DateTime(2026, 6, 15, 18, 25, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 6, 15, 18, 45, 0, DateTimeKind.Utc);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.BackfillWindowAsync(_competition.Id, from, until, CancellationToken.None));

        var reloaded = await _db.Competitions.FindAsync(_competition.Id);
        Assert.Equal(_originalCheckpoint, reloaded!.LastIngestedUntilUtc);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-9: Backfill uses the same pipeline — tweet with video + primary hashtag
    //        is imported as PendingApproval (not AutoExcluded)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BackfillWindow_TweetWithVideoAndPrimaryHashtag_ImportedAsPendingApproval()
    {
        var tweetId = "2066589995939360998";
        var stub    = new StubTwitterClient(new[] { BuildTweet(tweetId, hasVideo: true) });
        var svc     = CreateService(stub);
        var from    = new DateTime(2026, 6, 15, 18, 25, 0, DateTimeKind.Utc);
        var until   = new DateTime(2026, 6, 15, 18, 45, 0, DateTimeKind.Utc);

        var result = await svc.BackfillWindowAsync(_competition.Id, from, until, CancellationToken.None);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(0, result.AutoExcludedCount);
        Assert.Equal(0, result.FailedCount);

        var participation = await _db.Participations
            .FirstOrDefaultAsync(p => p.ExternalPostId == tweetId);

        Assert.NotNull(participation);
        Assert.Equal(ParticipationStatus.PendingApproval, participation!.Status);
        Assert.True(participation.HasVideo);
        Assert.True(participation.HasPrimaryHashtag);
        Assert.False(participation.IsAutoExcluded);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-9b: Tweet without video → AutoExcluded (same logic as regular ingestion)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BackfillWindow_TweetWithoutVideo_AutoExcluded()
    {
        var stub    = new StubTwitterClient(new[] { BuildTweet("no-video-id", hasVideo: false) });
        var svc     = CreateService(stub);
        var from    = new DateTime(2026, 6, 15, 18, 25, 0, DateTimeKind.Utc);
        var until   = new DateTime(2026, 6, 15, 18, 45, 0, DateTimeKind.Utc);

        var result = await svc.BackfillWindowAsync(_competition.Id, from, until, CancellationToken.None);

        Assert.Equal(1, result.AutoExcludedCount);
        var p = await _db.Participations.FirstOrDefaultAsync(x => x.ExternalPostId == "no-video-id");
        Assert.NotNull(p);
        Assert.Equal(ParticipationStatus.AutoExcluded, p!.Status);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Stubs
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class StubTwitterClient : ITwitterSearchClient
    {
        private readonly IReadOnlyList<RawTweetDto> _tweets;
        public StubTwitterClient(IReadOnlyList<RawTweetDto> tweets) => _tweets = tweets;
        public Task<IReadOnlyList<RawTweetDto>> SearchHashtagAsync(
            string hashtag, string? secondaryHashtag,
            DateTime sinceUtc, DateTime untilUtc, CancellationToken ct)
            => Task.FromResult(_tweets);
        public Task<TwitterSearchPageResult> FetchPageAsync(
            string expression, DateTime sinceUtc, DateTime untilUtc,
            string queryType, CancellationToken ct)
            => Task.FromResult(new TwitterSearchPageResult(_tweets, false));
    }

    private sealed class ThrowingTwitterClient : ITwitterSearchClient
    {
        public Task<IReadOnlyList<RawTweetDto>> SearchHashtagAsync(
            string hashtag, string? secondaryHashtag,
            DateTime sinceUtc, DateTime untilUtc, CancellationToken ct)
            => throw new InvalidOperationException("Simulated API failure");
        public Task<TwitterSearchPageResult> FetchPageAsync(
            string expression, DateTime sinceUtc, DateTime untilUtc,
            string queryType, CancellationToken ct)
            => throw new InvalidOperationException("Simulated API failure");
    }
}
