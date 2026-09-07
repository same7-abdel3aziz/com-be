using System.Collections.Concurrent;
using System.Text.Json;
using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Dtos.Ingestion;
using CompetitionManagementSystem.Models;
using CompetitionManagementSystem.Options;
using CompetitionManagementSystem.Services.Twitter;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CompetitionManagementSystem.Services.Ingestion;

public sealed class IngestionService : IIngestionService
{
    // One semaphore per competition — prevents concurrent range backfills on the same competition.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _backfillLocks = new();

    // Checkpoint is persisted after every top-level window (not every leaf).
    // Leaf windows do NOT flush individually — the parent loop does.

    private readonly ApplicationDbContext _db;
    private readonly ITwitterSearchClient _twitterClient;
    private readonly TwitterApiIoOptions _options;
    private readonly ILogger<IngestionService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public IngestionService(
        ApplicationDbContext db,
        ITwitterSearchClient twitterClient,
        IOptions<TwitterApiIoOptions> options,
        ILogger<IngestionService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _db           = db;
        _twitterClient = twitterClient;
        _options      = options.Value;
        _logger       = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task RunForAllActiveCompetitionsAsync(CancellationToken cancellationToken)
    {
        var competitions = await _db.Competitions
            .Where(c => c.IsActive)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        foreach (var competitionId in competitions)
        {
            try
            {
                await RunForCompetitionAsync(competitionId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ingestion failed for competition {CompetitionId}", competitionId);
            }
        }
    }

    public async Task<IngestionResult> RunForCompetitionAsync(Guid competitionId, CancellationToken cancellationToken)
    {
        var competition = await _db.Competitions.FirstOrDefaultAsync(c => c.Id == competitionId, cancellationToken);
        if (competition is null)
            throw new KeyNotFoundException("Competition not found.");

        // DB-backed lock: skip regular ingestion if a range backfill is running on any instance.
        // Uses sp_getapplock on SQL Server so the check is atomic with the backfill's own lock.
        bool skipIngestion;
        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await AcquireBackfillAppLockAsync(competitionId, cancellationToken);
                skipIngestion = await _db.IngestionRuns.AnyAsync(
                    r => r.CompetitionId == competitionId
                         && (r.Status == "InProgress" || r.Status == "Resuming"),
                    cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch (InvalidOperationException)
        {
            // sp_getapplock could not be acquired — backfill holds it. Skip.
            skipIngestion = true;
        }

        if (skipIngestion)
        {
            _logger.LogInformation(
                "RunForCompetition skipped for {Id}: range backfill is in progress.", competitionId);
            return new IngestionResult(0, 0, 0, 0, 0, Guid.Empty);
        }

        var until = DateTime.UtcNow;
        // var since = competition.LastIngestedUntilUtc ?? until.AddMinutes(-Math.Max(_options.LookbackMinutes, competition.PollingIntervalMinutes));
        var since = competition.LastIngestedUntilUtc ?? competition.StartDateUtc;
        var run = new IngestionRun
        {
            CompetitionId = competition.Id,
            FromUtc = since,
            UntilUtc = until,
            Status = "Started"
        };
        _db.IngestionRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var tweets = await _twitterClient.SearchHashtagAsync(
                competition.Hashtag,
                competition.SecondaryHashtag,
                since,
                until,
                cancellationToken);

            await ProcessTweetsAsync(competition, run, tweets, cancellationToken);

            competition.LastIngestedUntilUtc = until;
            competition.UpdatedAtUtc = DateTime.UtcNow;
            run.Status = "Succeeded";
            run.FinishedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Important: do not move LastIngestedUntilUtc on external API failure.
            // The next successful run will retry the same window and cover the outage period.
            run.FailedCount++;
            run.Status = ex is TwitterApiException tex && tex.IsRateLimited ? "RateLimited" : "Failed";
            run.ErrorMessage = ex.Message;
            run.FinishedAtUtc = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog
            {
                EntityType = "Competition",
                EntityId = competition.Id.ToString(),
                Action = "IngestionFailed",
                UserId = "System",
                UserEmail = "System",
                UserRole = "System",
                NewValueJson = JsonSerializer.Serialize(new { run.FromUtc, run.UntilUtc, Error = ex.Message })
            });
            await _db.SaveChangesAsync(cancellationToken);
            throw;
        }

        return new IngestionResult(run.ImportedCount, run.UpdatedExistingCount, run.SkippedDuplicateCount, run.AutoExcludedCount, run.FailedCount, run.Id);
    }

    public async Task<IngestionResult> BackfillWindowAsync(
        Guid competitionId,
        DateTime fromUtc,
        DateTime untilUtc,
        CancellationToken cancellationToken)
    {
        var competition = await _db.Competitions
            .FirstOrDefaultAsync(c => c.Id == competitionId, cancellationToken)
            ?? throw new KeyNotFoundException("Competition not found.");

        _logger.LogInformation(
            "Backfill started. CompetitionId={CompetitionId} FromUtc={From:o} UntilUtc={Until:o}",
            competitionId, fromUtc, untilUtc);

        var run = new IngestionRun
        {
            CompetitionId = competition.Id,
            FromUtc       = fromUtc,
            UntilUtc      = untilUtc,
            Status        = "Started"
        };
        _db.IngestionRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var tweets = await _twitterClient.SearchHashtagAsync(
                competition.Hashtag,
                competition.SecondaryHashtag,
                fromUtc,
                untilUtc,
                cancellationToken);

            await ProcessTweetsAsync(competition, run, tweets, cancellationToken);

            // Intentionally do NOT update competition.LastIngestedUntilUtc —
            // this is a targeted recovery operation, not a regular checkpoint advance.
            run.Status        = "Succeeded";
            run.FinishedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Backfill succeeded. CompetitionId={CompetitionId} Imported={Imported} " +
                "Updated={Updated} AutoExcluded={Excl} SkippedDuplicate={Skip} Failed={Failed}",
                competitionId,
                run.ImportedCount, run.UpdatedExistingCount,
                run.AutoExcludedCount, run.SkippedDuplicateCount, run.FailedCount);
        }
        catch (Exception ex)
        {
            run.FailedCount++;
            run.Status        = ex is TwitterApiException tex && tex.IsRateLimited ? "RateLimited" : "Failed";
            run.ErrorMessage  = ex.Message;
            run.FinishedAtUtc = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog
            {
                EntityType   = "Competition",
                EntityId     = competition.Id.ToString(),
                Action       = "BackfillFailed",
                UserId       = "System", UserEmail = "System", UserRole = "System",
                NewValueJson = JsonSerializer.Serialize(new { run.FromUtc, run.UntilUtc, Error = ex.Message })
            });
            await _db.SaveChangesAsync(cancellationToken);
            throw;
        }

        return new IngestionResult(
            run.ImportedCount, run.UpdatedExistingCount,
            run.SkippedDuplicateCount, run.AutoExcludedCount,
            run.FailedCount, run.Id);
    }

    // ── BackfillRange ────────────────────────────────────────────────────────────

    // Minimum window size — split stops when a half would be smaller than this.
    private static readonly TimeSpan MinWindowDuration = TimeSpan.FromSeconds(1);

    private static bool CanSplit(DateTime from, DateTime until) =>
        TimeSpan.FromTicks((until - from).Ticks / 2) >= MinWindowDuration;

    private static string DetermineStatus(BackfillRangeResult r) =>
        r.IsComplete ? "Succeeded" : "IncompleteCoverage";

    private bool IsSqlServer() =>
        _db.Database.ProviderName?.Contains("SqlServer") == true;

    // ── sp_getapplock: atomic DB lock shared by all instances ────────────────
    //
    // Lock key format: TwitterIngestion:{CompetitionId}
    // LockOwner=Transaction → released on commit/rollback.
    // The InProgress run record in DB is the semantic lock for the backfill duration.
    // sp_getapplock prevents the race condition in the "check + create run" section.
    //
    // On InMemory (tests): falls through to the DB count-check only.

    private async Task AcquireBackfillAppLockAsync(Guid competitionId, CancellationToken ct)
    {
        if (!IsSqlServer()) return;

        const string sql =
            @"DECLARE @r INT;
              EXEC @r = sp_getapplock
                  @Resource    = @lockKey,
                  @LockMode    = 'Exclusive',
                  @LockOwner   = 'Transaction',
                  @LockTimeout = 0;
              IF @r < 0
                  THROW 51000, N'BackfillLockConflict', 1;";
        try
        {
            await _db.Database.ExecuteSqlRawAsync(sql,
                new[] { new SqlParameter("@lockKey", $"TwitterIngestion:{competitionId}") }, ct);
        }
        catch (SqlException ex) when (ex.Number == 51000)
        {
            throw new InvalidOperationException(
                $"Concurrent backfill detected for competition {competitionId}. Lock not acquired.", ex);
        }
    }

    // ── Shared background-launch helper ──────────────────────────────────────

    private void LaunchBackgroundBackfill(
        Guid competitionId, DateTime fromUtc, DateTime untilUtc,
        int initialWindowMinutes, int maxRequests, Guid runId,
        SemaphoreSlim lockObj)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IIngestionService>();
                await ((IngestionService)svc).BackfillRangeAsync(
                    competitionId, fromUtc, untilUtc,
                    initialWindowMinutes, maxRequests, runId,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "BackfillRange background task failed. CompetitionId={Id} RunId={RunId}",
                    competitionId, runId);
            }
            finally
            {
                lockObj.Release();
            }
        });
    }

    // ── StartBackfillRangeAsync ──────────────────────────────────────────────

    public async Task<Guid> StartBackfillRangeAsync(
        Guid competitionId,
        DateTime fromUtc,
        DateTime untilUtc,
        int initialWindowMinutes,
        int maxRequests,
        CancellationToken cancellationToken)
    {
        var competition = await _db.Competitions
            .FirstOrDefaultAsync(c => c.Id == competitionId, cancellationToken)
            ?? throw new KeyNotFoundException("Competition not found.");

        // In-process guard for burst HTTP requests hitting the same process.
        var lockObj = _backfillLocks.GetOrAdd(competitionId, _ => new SemaphoreSlim(1, 1));
        if (!await lockObj.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException(
                $"A range backfill is already starting for competition {competitionId}.");

        IngestionRun run;
        try
        {
            // Atomic section: sp_getapplock (SQL Server) + DB count check + INSERT run.
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await AcquireBackfillAppLockAsync(competitionId, cancellationToken);

                var hasRunning = await _db.IngestionRuns.AnyAsync(
                    r => r.CompetitionId == competitionId
                         && (r.Status == "InProgress" || r.Status == "Resuming"),
                    cancellationToken);
                if (hasRunning)
                    throw new InvalidOperationException(
                        $"A range backfill is already running for competition {competitionId}.");

                run = new IngestionRun
                {
                    CompetitionId = competition.Id,
                    FromUtc       = fromUtc,
                    UntilUtc      = untilUtc,
                    Status        = "InProgress"
                    // IsComplete intentionally null until the run finishes
                };
                _db.IngestionRuns.Add(run);
                await _db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch
        {
            lockObj.Release();
            throw;
        }

        // Lock is held in lockObj until background task finishes.
        LaunchBackgroundBackfill(
            competitionId, fromUtc, untilUtc, initialWindowMinutes, maxRequests, run.Id, lockObj);

        return run.Id;
    }

    // ── ResumeBackfillRangeAsync ─────────────────────────────────────────────
    //
    // Allowed for: InProgress (stuck after crash), Resuming, IncompleteCoverage,
    //              BudgetExceeded, Failed.
    // NOT allowed for: Succeeded.
    // The current runId is explicitly excluded from the "other running" check so it
    // does not block itself.

    public async Task<Guid> ResumeBackfillRangeAsync(
        Guid competitionId,
        Guid runId,
        int maxRequests,
        CancellationToken cancellationToken)
    {
        var run = await _db.IngestionRuns.FindAsync(new object[] { runId }, cancellationToken)
            ?? throw new KeyNotFoundException($"IngestionRun {runId} not found.");

        if (run.CompetitionId != competitionId)
            throw new InvalidOperationException("Run does not belong to the specified competition.");

        if (run.Status == "Succeeded")
            throw new InvalidOperationException("Run already completed successfully — nothing to resume.");

        // In-process guard.
        var lockObj = _backfillLocks.GetOrAdd(competitionId, _ => new SemaphoreSlim(1, 1));
        if (!await lockObj.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException(
                "In-process lock is held — another backfill is starting on this instance. Try again in a moment.");

        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await AcquireBackfillAppLockAsync(competitionId, cancellationToken);

                // A DIFFERENT run being InProgress blocks resume.
                var hasOther = await _db.IngestionRuns.AnyAsync(
                    r => r.CompetitionId == competitionId && r.Id != runId
                         && (r.Status == "InProgress" || r.Status == "Resuming"),
                    cancellationToken);
                if (hasOther)
                    throw new InvalidOperationException(
                        "Another range backfill is already running for this competition.");

                var resumeFrom = run.NextWindowFromUtc ?? run.FromUtc
                    ?? throw new InvalidOperationException("Run has no valid FromUtc to resume from.");
                var resumeUntil = run.UntilUtc
                    ?? throw new InvalidOperationException("Run has no UntilUtc.");

                run.Status = "Resuming";
                await _db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                LaunchBackgroundBackfill(
                    competitionId, resumeFrom, resumeUntil, 60, maxRequests, runId, lockObj);

                return runId;
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch
        {
            lockObj.Release();
            throw;
        }
    }

    // ── BackfillRangeAsync (shared by Start + Resume) ────────────────────────

    public async Task BackfillRangeAsync(
        Guid competitionId,
        DateTime fromUtc,
        DateTime untilUtc,
        int initialWindowMinutes,
        int maxRequests,
        Guid runId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "BackfillRange started. CompetitionId={Id} From={From:o} Until={Until:o} InitWindow={Init}min MaxReq={Max}",
            competitionId, fromUtc, untilUtc, initialWindowMinutes, maxRequests);

        var competition = await _db.Competitions
            .FirstOrDefaultAsync(c => c.Id == competitionId, cancellationToken)
            ?? throw new KeyNotFoundException("Competition not found in background scope.");

        var run = await _db.IngestionRuns.FindAsync(new object[] { runId }, cancellationToken)
            ?? throw new KeyNotFoundException($"IngestionRun {runId} not found.");

        run.Status = "InProgress";
        await _db.SaveChangesAsync(cancellationToken);

        var expressions = TwitterHashtagVariants.BuildExpressions(
            competition.Hashtag, competition.SecondaryHashtag);

        var result   = new BackfillRangeResult { IngestionRunId = runId, IsComplete = true };
        var seenIds  = new HashSet<string>(StringComparer.Ordinal);
        var reqCount = new[] { 0 };

        try
        {
            // Guard: initialWindowMinutes=0 (test/edge case) → treat full range as one window.
            var windowSpan = initialWindowMinutes > 0
                ? TimeSpan.FromMinutes(initialWindowMinutes)
                : (untilUtc - fromUtc);
            var current    = fromUtc;

            while (current < untilUtc && !cancellationToken.IsCancellationRequested)
            {
                if (reqCount[0] >= maxRequests)
                {
                    result.BudgetExceeded = true;
                    result.IsComplete     = false;
                    _logger.LogWarning(
                        "BackfillRange: request budget ({Max}) exhausted at window {From:o}.", maxRequests, current);
                    break;
                }

                var windowEnd = current + windowSpan;
                if (windowEnd > untilUtc) windowEnd = untilUtc;

                await FetchAndIngestWindowAsync(
                    competition, run, expressions, current, windowEnd,
                    result, seenIds, reqCount, maxRequests, cancellationToken);

                if (result.BudgetExceeded) break;

                // Checkpoint: persist progress after each top-level window.
                run.NextWindowFromUtc = windowEnd;
                await _db.SaveChangesAsync(cancellationToken);

                current = windowEnd;
            }

            result.ImportedCount         = run.ImportedCount;
            result.UpdatedExistingCount  = run.UpdatedExistingCount;
            result.SkippedDuplicateCount = run.SkippedDuplicateCount;
            result.AutoExcludedCount     = run.AutoExcludedCount;
            result.FailedCount           = run.FailedCount;
            result.RequestsUsed          = reqCount[0];

            run.IsComplete        = result.IsComplete;
            run.Status            = DetermineStatus(result);
            run.FinishedAtUtc     = DateTime.UtcNow;
            run.ResultSummaryJson = JsonSerializer.Serialize(new
            {
                result.IsComplete,
                result.WindowsChecked,
                result.WindowsSplit,
                result.SaturatedTerminalCount,
                result.TotalTweetsFromApi,
                result.ImportedCount,
                result.UpdatedExistingCount,
                result.AutoExcludedCount,
                result.FailedCount,
                result.RequestsUsed,
                result.HasNextPageAdvisoryCount,
                result.BudgetExceeded,
                IncompleteWindowCount = result.IncompleteWindowKeys.Count,
                result.IncompleteWindowKeys
            });
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "BackfillRange finished. CompetitionId={Id} RunId={RunId} Status={Status} " +
                "IsComplete={Complete} Windows={Checked} Split={Split} Terminal={Terminal} " +
                "Fetched={Fetched} Imported={Imp} Requests={Req}/{Max}",
                competitionId, runId, run.Status,
                result.IsComplete, result.WindowsChecked, result.WindowsSplit,
                result.SaturatedTerminalCount, result.TotalTweetsFromApi,
                run.ImportedCount, reqCount[0], maxRequests);
        }
        catch (Exception ex)
        {
            run.Status        = "Failed";
            run.IsComplete    = false;
            run.ErrorMessage  = ex.Message;
            run.FinishedAtUtc = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog
            {
                EntityType   = "Competition",
                EntityId     = competitionId.ToString(),
                Action       = "BackfillRangeFailed",
                UserId       = "System", UserEmail = "System", UserRole = "System",
                NewValueJson = JsonSerializer.Serialize(new { runId, fromUtc, untilUtc, Error = ex.Message })
            });
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    // ── FetchAndIngestWindowAsync ────────────────────────────────────────────

    private async Task FetchAndIngestWindowAsync(
        Competition competition,
        IngestionRun run,
        IReadOnlyList<string> expressions,
        DateTime windowFrom,
        DateTime windowUntil,
        BackfillRangeResult result,
        HashSet<string> seenIds,
        int[] reqCount,
        int maxRequests,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || result.BudgetExceeded)
            return;

        var windowTweets = new Dictionary<string, RawTweetDto>(StringComparer.Ordinal);
        var anySaturated = false;

        foreach (var expression in expressions)
        {
            if (reqCount[0] >= maxRequests)
            {
                result.BudgetExceeded = true;
                result.IsComplete     = false;
                _logger.LogWarning("BackfillRange: budget ({Max}) exhausted mid-window.", maxRequests);
                return;
            }

            reqCount[0]++;
            TwitterSearchPageResult page;
            try
            {
                page = await _twitterClient.FetchPageAsync(
                    expression, windowFrom, windowUntil, "Latest", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "FetchPageAsync failed for expression={Expr} window={From:o}-{Until:o}. Skipping.",
                    expression, windowFrom, windowUntil);
                run.FailedCount++;
                continue;
            }

            foreach (var t in page.Tweets.Where(t => !string.IsNullOrWhiteSpace(t.Id)))
                windowTweets.TryAdd(t.Id!, t);

            if (page.IsSaturated) anySaturated = true;
            if (page.HasNextPageAdvisory) result.HasNextPageAdvisoryCount++;
        }

        if (result.BudgetExceeded) return;

        // Adaptive splitting: recurse until window is unsaturated or too small to halve.
        if (anySaturated && CanSplit(windowFrom, windowUntil))
        {
            result.WindowsSplit++;
            var mid = windowFrom + TimeSpan.FromTicks((windowUntil - windowFrom).Ticks / 2);

            await FetchAndIngestWindowAsync(competition, run, expressions,
                windowFrom, mid, result, seenIds, reqCount, maxRequests, cancellationToken);

            await FetchAndIngestWindowAsync(competition, run, expressions,
                mid, windowUntil, result, seenIds, reqCount, maxRequests, cancellationToken);

            return; // sub-windows handle ingestion
        }

        // Terminal window: too small to split further but still saturated → IncompleteCoverage.
        if (anySaturated)
        {
            result.SaturatedTerminalCount++;
            result.IsComplete = false;
            var key = $"{windowFrom:o}/{windowUntil:o}";
            result.IncompleteWindowKeys.Add(key);
            _logger.LogWarning(
                "BackfillRange: terminal saturated window (too small to split). " +
                "Duration={Dur:0.###}s From={From:o} Until={Until:o}. Ingesting partial results.",
                (windowUntil - windowFrom).TotalSeconds, windowFrom, windowUntil);
        }

        // De-duplicate across all windows.
        var newTweets = windowTweets.Values
            .Where(t => seenIds.Add(t.Id!))
            .ToList();

        result.TotalTweetsFromApi += newTweets.Count;
        result.WindowsChecked++;

        if (newTweets.Count > 0)
        {
            var importedBefore = run.ImportedCount;
            await ProcessTweetsAsync(competition, run, newTweets, cancellationToken);
            var added = run.ImportedCount - importedBefore;
            result.NewTweetIds.AddRange(newTweets.Take(added).Select(t => t.Id!));
        }
    }

    // ── Shared tweet processing pipeline ─────────────────────────────────────────

    /// <summary>
    /// Shared tweet processing pipeline used by both regular ingestion and backfill.
    /// Adds/updates Participation entities in the DbContext change tracker and updates
    /// run counters. Does NOT call SaveChangesAsync — the caller is responsible for that.
    /// Does NOT modify competition.LastIngestedUntilUtc.
    /// </summary>
    private async Task ProcessTweetsAsync(
        Competition competition,
        IngestionRun run,
        IReadOnlyList<RawTweetDto> tweets,
        CancellationToken cancellationToken)
    {
        foreach (var tweet in tweets.Where(t => !string.IsNullOrWhiteSpace(t.Id)))
        {
            try
            {
                var existing = await _db.Participations
                    .Include(p => p.ExternalUrls)
                    .FirstOrDefaultAsync(p =>
                        p.CompetitionId == competition.Id &&
                        p.Platform == "X" &&
                        p.ExternalPostId == tweet.Id,
                        cancellationToken);

                var hasPrimary = ContainsHashtag(tweet, competition.Hashtag);
                var hasSecondary = !string.IsNullOrWhiteSpace(competition.SecondaryHashtag)
                                   && ContainsHashtag(tweet, competition.SecondaryHashtag);
                var hasVideo = tweet.HasVideo;
                var validationErrors = new List<string>();
                if (!hasVideo)
                    validationErrors.Add("الفيديو ليس موجود ");
                if (!hasPrimary && !hasSecondary)
                    validationErrors.Add("الهاشتاج الأساسي أو الثانوي غير موجود");

                if (existing is null)
                {
                    var participation = new Participation
                    {
                        CompetitionId          = competition.Id,
                        Platform               = "X",
                        ExternalPostId         = tweet.Id,
                        ExternalPostUrl        = tweet.Url,
                        Text                   = tweet.Text,
                        CreatedAtOnPlatformUtc = tweet.CreatedAtUtc,
                        ImportedAtUtc          = DateTime.UtcNow,
                        AuthorUserName         = tweet.AuthorUserName,
                        AuthorDisplayName      = tweet.AuthorDisplayName,
                        AuthorDescription      = tweet.AuthorDescription,
                        AuthorLocation         = tweet.AuthorLocation,
                        AuthorFollowers        = tweet.AuthorFollowers,
                        AuthorIsBlueVerified   = tweet.AuthorIsBlueVerified,
                        LikeCount              = tweet.LikeCount,
                        RetweetCount           = tweet.RetweetCount,
                        ReplyCount             = tweet.ReplyCount,
                        QuoteCount             = tweet.QuoteCount,
                        ViewCount              = tweet.ViewCount,
                        IsReply                = tweet.IsReply,
                        InReplyToId            = tweet.InReplyToId,
                        RawJsonData            = tweet.RawJsonData,
                        IsActive               = true,
                        HasVideo               = hasVideo,
                        HasPrimaryHashtag      = hasPrimary,
                        HasSecondaryHashtag    = hasSecondary,
                        CachedMediaUrlsJson    = JsonSerializer.Serialize(tweet.Media.Select(m => new { m.Type, m.Url, m.PreviewImageUrl })),
                        CachedPostSnapshotJson = JsonSerializer.Serialize(new
                        {
                            tweet.Id,
                            tweet.Url,
                            tweet.Text,
                            tweet.CreatedAtUtc,
                            tweet.AuthorUserName,
                            tweet.AuthorDisplayName,
                            Media = tweet.Media.Select(m => new { m.Type, m.Url, m.PreviewImageUrl }),
                            Urls  = tweet.Urls.Select(u => new { u.Url, u.ExpandedUrl, u.DisplayUrl })
                        })
                    };

                    if (validationErrors.Count > 0)
                    {
                        participation.Status          = ParticipationStatus.AutoExcluded;
                        participation.IsAutoExcluded  = true;
                        participation.ExclusionReason = string.Join("; ", validationErrors);
                        run.AutoExcludedCount++;
                    }
                    else
                    {
                        participation.Status = ParticipationStatus.PendingApproval;
                    }

                    foreach (var url in tweet.Urls)
                    {
                        participation.ExternalUrls.Add(new ParticipationExternalUrl
                        {
                            Url         = url.Url,
                            ExpandedUrl = url.ExpandedUrl,
                            DisplayUrl  = url.DisplayUrl,
                            RawJsonData = url.RawJsonData
                        });
                    }

                    _db.Participations.Add(participation);
                    run.ImportedCount++;
                    _db.AuditLogs.Add(new AuditLog
                    {
                        EntityType   = "Participation",
                        EntityId     = participation.Id.ToString(),
                        Action       = participation.Status == ParticipationStatus.AutoExcluded
                                           ? "ParticipationAutoExcluded"
                                           : "ParticipationImported",
                        UserId       = "System", UserEmail = "System", UserRole = "System",
                        NewValueJson = JsonSerializer.Serialize(new
                        {
                            participation.ExternalPostId,
                            participation.Status,
                            participation.ExclusionReason
                        })
                    });
                }
                else
                {
                    var oldSnapshot = JsonSerializer.Serialize(new
                    {
                        existing.LikeCount,
                        existing.RetweetCount,
                        existing.ReplyCount,
                        existing.QuoteCount,
                        existing.ViewCount,
                        existing.Status
                    });

                    existing.ExternalPostUrl        = tweet.Url;
                    existing.Text                   = tweet.Text;
                    existing.AuthorUserName         = tweet.AuthorUserName;
                    existing.AuthorDisplayName      = tweet.AuthorDisplayName;
                    existing.AuthorDescription      = tweet.AuthorDescription;
                    existing.AuthorLocation         = tweet.AuthorLocation;
                    existing.AuthorFollowers        = tweet.AuthorFollowers;
                    existing.AuthorIsBlueVerified   = tweet.AuthorIsBlueVerified;
                    existing.LikeCount              = tweet.LikeCount;
                    existing.RetweetCount           = tweet.RetweetCount;
                    existing.ReplyCount             = tweet.ReplyCount;
                    existing.QuoteCount             = tweet.QuoteCount;
                    existing.ViewCount              = tweet.ViewCount;
                    existing.RawJsonData            = tweet.RawJsonData;
                    existing.HasVideo               = hasVideo;
                    existing.HasPrimaryHashtag      = hasPrimary;
                    existing.HasSecondaryHashtag    = hasSecondary;
                    existing.CachedMediaUrlsJson    = JsonSerializer.Serialize(tweet.Media.Select(m => new { m.Type, m.Url, m.PreviewImageUrl }));
                    existing.CachedPostSnapshotJson = JsonSerializer.Serialize(new
                    {
                        tweet.Id,
                        tweet.Url,
                        tweet.Text,
                        tweet.CreatedAtUtc,
                        tweet.AuthorUserName,
                        tweet.AuthorDisplayName,
                        Media = tweet.Media.Select(m => new { m.Type, m.Url, m.PreviewImageUrl }),
                        Urls  = tweet.Urls.Select(u => new { u.Url, u.ExpandedUrl, u.DisplayUrl })
                    });

                    // Do not undo manual decisions or scoring. Only auto-exclude records still waiting for approval.
                    if (existing.Status is ParticipationStatus.Imported or ParticipationStatus.PendingApproval or ParticipationStatus.AutoExcluded)
                    {
                        if (validationErrors.Count > 0)
                        {
                            existing.Status          = ParticipationStatus.AutoExcluded;
                            existing.IsAutoExcluded  = true;
                            existing.ExclusionReason = string.Join("; ", validationErrors);
                        }
                        else if (existing.Status == ParticipationStatus.AutoExcluded)
                        {
                            existing.Status          = ParticipationStatus.PendingApproval;
                            existing.IsAutoExcluded  = false;
                            existing.ExclusionReason = null;
                        }
                    }

                    run.UpdatedExistingCount++;
                    _db.AuditLogs.Add(new AuditLog
                    {
                        EntityType   = "Participation",
                        EntityId     = existing.Id.ToString(),
                        Action       = "ParticipationRefreshedFromX",
                        UserId       = "System", UserEmail = "System", UserRole = "System",
                        OldValueJson = oldSnapshot,
                        NewValueJson = JsonSerializer.Serialize(new
                        {
                            existing.LikeCount, existing.RetweetCount,
                            existing.ReplyCount, existing.QuoteCount,
                            existing.ViewCount, existing.Status
                        })
                    });
                }
            }
            catch (DbUpdateException)
            {
                run.SkippedDuplicateCount++;
            }
            catch (Exception ex)
            {
                run.FailedCount++;
                _logger.LogWarning(ex, "Failed to process tweet {TweetId}", tweet.Id);
            }
        }
    }

    //private static bool ContainsHashtag(RawTweetDto tweet, string? hashtag)
    //{
    //    if (string.IsNullOrWhiteSpace(hashtag))
    //        return true;
    //    var clean = hashtag.Trim().TrimStart('#');
    //    if (string.IsNullOrWhiteSpace(clean))
    //        return true;
    //    var text = tweet.Text ?? string.Empty;
    //    return text.Contains("#" + clean, StringComparison.OrdinalIgnoreCase) ||
    //           text.Contains(clean, StringComparison.OrdinalIgnoreCase) ||
    //           tweet.RawJsonData.Contains("#" + clean, StringComparison.OrdinalIgnoreCase);
    //}

    private static bool ContainsHashtag(RawTweetDto tweet, string? hashtag)
    {
        if (string.IsNullOrWhiteSpace(hashtag))
            return true;

        var clean = NormalizeArabic(hashtag.Trim().TrimStart('#'));

        if (string.IsNullOrWhiteSpace(clean))
            return true;

        var text = NormalizeArabic(tweet.Text ?? string.Empty);
        var raw = NormalizeArabic(tweet.RawJsonData ?? string.Empty);

        // Match "#clean" only when it is a complete hashtag token, i.e. the next character
        // is not a letter, digit, or underscore. This prevents "#اب" from matching "#اب5".
        var pattern = System.Text.RegularExpressions.Regex.Escape("#" + clean) + @"(?![\p{L}\p{N}_])";
        var options = System.Text.RegularExpressions.RegexOptions.IgnoreCase;

        return System.Text.RegularExpressions.Regex.IsMatch(text, pattern, options)
               || System.Text.RegularExpressions.Regex.IsMatch(raw, pattern, options);
    }

    private static string NormalizeArabic(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        return input
            .Trim()
            .ToLowerInvariant()

            // Normalize Alef
            .Replace("أ", "ا")
            .Replace("إ", "ا")
            .Replace("آ", "ا")
            .Replace("ٱ", "ا")

            // Normalize Ya
            .Replace("ى", "ي")
            .Replace("ئ", "ي")

            // Normalize Waw
            .Replace("ؤ", "و")

            // Normalize Ta Marbuta
            .Replace("ة", "ه")

            // Arabic numbers -> English
            .Replace("٠", "0")
            .Replace("١", "1")
            .Replace("٢", "2")
            .Replace("٣", "3")
            .Replace("٤", "4")
            .Replace("٥", "5")
            .Replace("٦", "6")
            .Replace("٧", "7")
            .Replace("٨", "8")
            .Replace("٩", "9")

            // Remove Tatweel
            .Replace("ـ", "");
    }
}