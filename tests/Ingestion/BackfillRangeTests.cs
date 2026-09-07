using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Models;
using CompetitionManagementSystem.Options;
using CompetitionManagementSystem.Services.Ingestion;
using CompetitionManagementSystem.Services.Twitter;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CompetitionManagementSystem.Tests.Ingestion;

public sealed class BackfillRangeTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static ApplicationDbContext BuildDb(string dbName)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(dbName).Options);

    private static Competition SeedCompetition(ApplicationDbContext db)
    {
        var comp = new Competition
        {
            Id                   = Guid.NewGuid(),
            Name                 = "Test",
            Hashtag              = "#test5",          // 2 variants: ASCII + Arabic
            SecondaryHashtag     = null,
            StartDateUtc         = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDateUtc           = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            LastIngestedUntilUtc = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc),
            IsActive             = true
        };
        db.Competitions.Add(comp);
        db.SaveChanges();
        return comp;
    }

    private static IngestionService BuildSvc(
        ApplicationDbContext db,
        ITwitterSearchClient client)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        return new IngestionService(
            db, client,
            new OptionsWrapper<TwitterApiIoOptions>(new TwitterApiIoOptions()),
            NullLogger<IngestionService>.Instance,
            sp.GetRequiredService<IServiceScopeFactory>());
    }

    private static RawTweetDto MakeTweet(string id) => new()
    {
        Id                = id,
        Text              = $"#test5 tweet {id}",
        AuthorUserName    = "testauthor",
        AuthorDisplayName = "Test Author",
        CreatedAtUtc      = DateTime.UtcNow,
        RawJsonData       = "{}"
    };

    private static IngestionRun AddRun(ApplicationDbContext db, Guid competitionId,
        DateTime from, DateTime until, string status = "InProgress")
    {
        var run = new IngestionRun
        {
            CompetitionId = competitionId,
            FromUtc       = from,
            UntilUtc      = until,
            Status        = status,
            IsComplete    = true
        };
        db.IngestionRuns.Add(run);
        db.SaveChanges();
        return run;
    }

    // ── TC-Range-1: no saturation → Succeeded, correct window count ───────────

    [Fact]
    public async Task TC_Range1_NoSaturation_SucceededAndCorrectCallCount()
    {
        var db   = BuildDb("range1");
        var comp = SeedCompetition(db);
        var mock = new Mock<ITwitterSearchClient>();
        mock.Setup(c => c.FetchPageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TwitterSearchPageResult(Array.Empty<RawTweetDto>(), false));
        mock.Setup(c => c.SearchHashtagAsync(It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RawTweetDto>());

        var svc = BuildSvc(db, mock.Object);
        var from  = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 5, 10, 3, 0, 0, DateTimeKind.Utc); // 3 × 60-min windows
        var run   = AddRun(db, comp.Id, from, until);

        await svc.BackfillRangeAsync(comp.Id, from, until, 60, 3000, run.Id, CancellationToken.None);

        // 3 windows × 2 expressions = 6 calls
        mock.Verify(c => c.FetchPageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            "Latest", It.IsAny<CancellationToken>()), Times.Exactly(6));

        var saved = await db.IngestionRuns.FindAsync(run.Id);
        Assert.Equal("Succeeded", saved!.Status);
        Assert.True(saved.IsComplete);
    }

    // ── TC-Range-2: saturation triggers recursive split ───────────────────────

    [Fact]
    public async Task TC_Range2_Saturation_TriggersWindowSplit()
    {
        var db       = BuildDb("range2");
        var comp     = SeedCompetition(db);
        var callCount = 0;
        var mock     = new Mock<ITwitterSearchClient>();
        mock.Setup(c => c.FetchPageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return new TwitterSearchPageResult(Array.Empty<RawTweetDto>(), callCount <= 2);
            });
        mock.Setup(c => c.SearchHashtagAsync(It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RawTweetDto>());

        var svc   = BuildSvc(db, mock.Object);
        var from  = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 5, 10, 1, 0, 0, DateTimeKind.Utc);
        var run   = AddRun(db, comp.Id, from, until);

        await svc.BackfillRangeAsync(comp.Id, from, until, 60, 3000, run.Id, CancellationToken.None);

        Assert.True(callCount > 2, $"Expected split to generate more than 2 calls. Got {callCount}");
    }

    // ── TC-Range-3: terminal saturated window → IncompleteCoverage, NOT Succeeded

    [Fact]
    public async Task TC_Range3_TerminalSaturation_IncompleteCoverageNotSucceeded()
    {
        var db   = BuildDb("range3");
        var comp = SeedCompetition(db);
        var mock = new Mock<ITwitterSearchClient>();
        // Always saturated — will eventually reach MinWindowDuration and stop splitting
        mock.Setup(c => c.FetchPageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TwitterSearchPageResult(Array.Empty<RawTweetDto>(), true));
        mock.Setup(c => c.SearchHashtagAsync(It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RawTweetDto>());

        var svc   = BuildSvc(db, mock.Object);
        var from  = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 5, 10, 0, 0, 2, DateTimeKind.Utc); // 2-second window → will hit MinWindowDuration fast
        var run   = AddRun(db, comp.Id, from, until);

        await svc.BackfillRangeAsync(comp.Id, from, until, 0, 3000, run.Id, CancellationToken.None);

        var saved = await db.IngestionRuns.FindAsync(run.Id);
        Assert.Equal("IncompleteCoverage", saved!.Status);
        Assert.False(saved.IsComplete);
        Assert.NotNull(saved.ResultSummaryJson);
    }

    // ── TC-Range-4: budget exhausted → IncompleteCoverage ────────────────────

    [Fact]
    public async Task TC_Range4_BudgetExhausted_IncompleteCoverage()
    {
        var db   = BuildDb("range4");
        var comp = SeedCompetition(db);
        var mock = new Mock<ITwitterSearchClient>();
        mock.Setup(c => c.FetchPageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TwitterSearchPageResult(Array.Empty<RawTweetDto>(), false));
        mock.Setup(c => c.SearchHashtagAsync(It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RawTweetDto>());

        var svc   = BuildSvc(db, mock.Object);
        var from  = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 5, 10, 6, 0, 0, DateTimeKind.Utc); // 6 windows × 2 = 12 calls needed
        var run   = AddRun(db, comp.Id, from, until);

        await svc.BackfillRangeAsync(comp.Id, from, until, 60, 3, run.Id, CancellationToken.None);

        var saved = await db.IngestionRuns.FindAsync(run.Id);
        Assert.Equal("IncompleteCoverage", saved!.Status);
        Assert.False(saved.IsComplete);
        mock.Verify(c => c.FetchPageAsync(
            It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtMost(3));
    }

    // ── TC-Range-5: LastIngestedUntilUtc never modified ──────────────────────

    [Fact]
    public async Task TC_Range5_LastIngestedUntilUtc_NeverModified()
    {
        var db   = BuildDb("range5");
        var comp = SeedCompetition(db);
        var original = comp.LastIngestedUntilUtc;

        var mock = new Mock<ITwitterSearchClient>();
        mock.Setup(c => c.FetchPageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TwitterSearchPageResult(new[] { MakeTweet("t1") }, false));
        mock.Setup(c => c.SearchHashtagAsync(It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RawTweetDto>());

        var svc  = BuildSvc(db, mock.Object);
        var from  = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 5, 10, 1, 0, 0, DateTimeKind.Utc);
        var run   = AddRun(db, comp.Id, from, until);

        await svc.BackfillRangeAsync(comp.Id, from, until, 60, 3000, run.Id, CancellationToken.None);

        var refreshed = await db.Competitions.FindAsync(comp.Id);
        Assert.Equal(original, refreshed!.LastIngestedUntilUtc);
    }

    // ── TC-Range-6: checkpoint saved → resume from NextWindowFromUtc ─────────

    [Fact]
    public async Task TC_Range6_Checkpoint_SavedAfterEachWindow()
    {
        var db   = BuildDb("range6");
        var comp = SeedCompetition(db);
        var mock = new Mock<ITwitterSearchClient>();
        mock.Setup(c => c.FetchPageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TwitterSearchPageResult(Array.Empty<RawTweetDto>(), false));
        mock.Setup(c => c.SearchHashtagAsync(It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RawTweetDto>());

        var svc   = BuildSvc(db, mock.Object);
        var from  = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 5, 10, 3, 0, 0, DateTimeKind.Utc);
        var run   = AddRun(db, comp.Id, from, until);

        await svc.BackfillRangeAsync(comp.Id, from, until, 60, 3000, run.Id, CancellationToken.None);

        var saved = await db.IngestionRuns.FindAsync(run.Id);
        // After full success, NextWindowFromUtc should equal the last window end = until
        Assert.Equal(until, saved!.NextWindowFromUtc);
    }

    // ── TC-Range-7: no duplicates on re-run of same range ────────────────────

    [Fact]
    public async Task TC_Range7_ReRunSameRange_NoDuplicates()
    {
        var db   = BuildDb("range7");
        var comp = SeedCompetition(db);
        var tweet = MakeTweet("tweet-dup");
        var mock = new Mock<ITwitterSearchClient>();
        mock.Setup(c => c.FetchPageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TwitterSearchPageResult(new[] { tweet }, false));
        mock.Setup(c => c.SearchHashtagAsync(It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RawTweetDto>());

        var svc   = BuildSvc(db, mock.Object);
        var from  = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 5, 10, 1, 0, 0, DateTimeKind.Utc);

        // First run
        var run1 = AddRun(db, comp.Id, from, until);
        await svc.BackfillRangeAsync(comp.Id, from, until, 60, 3000, run1.Id, CancellationToken.None);

        var countAfterFirst = await db.Participations.CountAsync();

        // Second run over same range
        var run2 = AddRun(db, comp.Id, from, until, "InProgress");
        await svc.BackfillRangeAsync(comp.Id, from, until, 60, 3000, run2.Id, CancellationToken.None);

        var countAfterSecond = await db.Participations.CountAsync();

        Assert.Equal(countAfterFirst, countAfterSecond);
    }

    // ── TC-Range-8: DB lock prevents concurrent backfill ─────────────────────

    [Fact]
    public async Task TC_Range8_DbLock_PreventsSecondConcurrentBackfill()
    {
        var db   = BuildDb("range8");
        var comp = SeedCompetition(db);

        // Seed an existing InProgress run — simulates backfill already running.
        var from  = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 5, 10, 1, 0, 0, DateTimeKind.Utc);
        db.IngestionRuns.Add(new IngestionRun
        {
            CompetitionId = comp.Id,
            FromUtc       = from,
            UntilUtc      = until,
            Status        = "InProgress"
        });
        db.SaveChanges();

        var mock = new Mock<ITwitterSearchClient>();
        var svc  = BuildSvc(db, mock.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.StartBackfillRangeAsync(comp.Id, from, until, 60, 3000, CancellationToken.None));
    }

    // ── TC-Range-10: has_next_page=true with count=1 → no split, Succeeded, advisory logged ──

    [Fact]
    public async Task TC_Range10_HasNextPageAdvisory_NoSplitWhenCountBelowThreshold()
    {
        var db   = BuildDb("range10");
        var comp = SeedCompetition(db);
        var mock = new Mock<ITwitterSearchClient>();

        // Simulate API returning 1 tweet + has_next_page=true (advisory only — not saturated)
        mock.Setup(c => c.FetchPageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TwitterSearchPageResult(
                new[] { MakeTweet("single") },
                IsSaturated: false,
                HasNextPageAdvisory: true));
        mock.Setup(c => c.SearchHashtagAsync(It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RawTweetDto>());

        var svc   = BuildSvc(db, mock.Object);
        var from  = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 5, 10, 1, 0, 0, DateTimeKind.Utc); // 1 window × 2 expressions = 2 calls
        var run   = AddRun(db, comp.Id, from, until);

        await svc.BackfillRangeAsync(comp.Id, from, until, 60, 3000, run.Id, CancellationToken.None);

        // Exactly 2 calls — no recursive split despite has_next_page=true
        mock.Verify(c => c.FetchPageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            "Latest", It.IsAny<CancellationToken>()), Times.Exactly(2));

        var saved = await db.IngestionRuns.FindAsync(run.Id);
        Assert.Equal("Succeeded", saved!.Status);
        Assert.True(saved.IsComplete);

        // HasNextPageAdvisoryCount must be recorded in the summary
        Assert.NotNull(saved.ResultSummaryJson);
        var summary = System.Text.Json.JsonDocument.Parse(saved.ResultSummaryJson!).RootElement;
        Assert.True(summary.GetProperty("HasNextPageAdvisoryCount").GetInt32() >= 2,
            "Expected at least 2 advisory records (one per expression)");
    }

    // ── TC-Range-11: count=20 → splits recursively → terminal → IncompleteCoverage ──

    [Fact]
    public async Task TC_Range11_SaturationByCount_SplitsUntilTerminal_IncompleteCoverage()
    {
        var db   = BuildDb("range11");
        var comp = SeedCompetition(db);
        var mock = new Mock<ITwitterSearchClient>();

        // Always return exactly 20 tweets — IsSaturated=true by count criterion
        mock.Setup(c => c.FetchPageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var tweets = Enumerable.Range(0, 20)
                    .Select(i => MakeTweet($"sat-{Guid.NewGuid():N}"))
                    .ToArray();
                return new TwitterSearchPageResult(tweets, IsSaturated: true);
            });
        mock.Setup(c => c.SearchHashtagAsync(It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RawTweetDto>());

        var svc   = BuildSvc(db, mock.Object);
        var from  = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 5, 10, 0, 0, 2, DateTimeKind.Utc); // 2-second window → hits MinWindowDuration fast
        var run   = AddRun(db, comp.Id, from, until);

        await svc.BackfillRangeAsync(comp.Id, from, until, 0, 3000, run.Id, CancellationToken.None);

        var saved = await db.IngestionRuns.FindAsync(run.Id);
        Assert.Equal("IncompleteCoverage", saved!.Status);
        Assert.False(saved.IsComplete);

        Assert.NotNull(saved.ResultSummaryJson);
        var summary = System.Text.Json.JsonDocument.Parse(saved.ResultSummaryJson!).RootElement;
        Assert.True(summary.GetProperty("SaturatedTerminalCount").GetInt32() > 0,
            "Expected at least one terminal saturated window");
    }

    // ── TC-Range-9: Resume from stuck InProgress (simulates process crash) ───────

    [Fact]
    public async Task TC_Range9_ResumeFromStuckInProgress_StartsFromCheckpoint_NoDuplicates()
    {
        var db   = BuildDb("range9");
        var comp = SeedCompetition(db);

        var from      = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        var until     = new DateTime(2026, 5, 10, 3, 0, 0, DateTimeKind.Utc);
        var checkpoint = new DateTime(2026, 5, 10, 1, 0, 0, DateTimeKind.Utc); // first hour already done

        // Seed a tweet that was already imported during the "first hour before crash"
        var alreadyImportedTweet = MakeTweet("already-imported");
        var participation = new Participation
        {
            ExternalPostId = alreadyImportedTweet.Id!,
            CompetitionId  = comp.Id,
            AuthorUserName = "testauthor",
            Text           = alreadyImportedTweet.Text,
            RawJsonData    = "{}",
            Status         = ParticipationStatus.PendingApproval
        };
        db.Participations.Add(participation);

        // Run stuck in the middle: status=InProgress, checkpoint after hour 1
        var stuckRun = new IngestionRun
        {
            CompetitionId     = comp.Id,
            FromUtc           = from,
            UntilUtc          = until,
            Status            = "InProgress",   // stuck after crash
            IsComplete        = null,
            NextWindowFromUtc = checkpoint,      // first hour was completed
            ImportedCount     = 1
        };
        db.IngestionRuns.Add(stuckRun);
        db.SaveChanges();

        // Twitter returns the same "already imported" tweet in the remaining windows too
        var callWindows = new List<(DateTime From, DateTime Until)>();
        var mock = new Mock<ITwitterSearchClient>();
        mock.Setup(c => c.FetchPageAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, DateTime wFrom, DateTime wUntil, string _, CancellationToken _) =>
            {
                callWindows.Add((wFrom, wUntil));
                return new TwitterSearchPageResult(new[] { alreadyImportedTweet }, false);
            });
        mock.Setup(c => c.SearchHashtagAsync(It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RawTweetDto>());

        var svc = BuildSvc(db, mock.Object);

        // Resume directly from checkpoint (simulates ResumeBackfillRangeAsync background call)
        await svc.BackfillRangeAsync(
            comp.Id, checkpoint, until, 60, 3000, stuckRun.Id, CancellationToken.None);

        // All API calls must be for windows AFTER the checkpoint
        Assert.All(callWindows, w =>
            Assert.True(w.From >= checkpoint,
                $"Expected all calls to be >= checkpoint {checkpoint:o} but got {w.From:o}"));

        // No duplicates: participation count must still be 1
        var totalCount = await db.Participations.CountAsync();
        Assert.Equal(1, totalCount);

        var saved = await db.IngestionRuns.FindAsync(stuckRun.Id);
        Assert.Equal("Succeeded", saved!.Status);
        Assert.True(saved.IsComplete);
    }
}
