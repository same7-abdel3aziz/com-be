using System.Net;
using System.Text.Json;
using CompetitionManagementSystem.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CompetitionManagementSystem.Services.Twitter;

public sealed class TwitterApiIoSearchClient : ITwitterSearchClient
{
    private readonly HttpClient _httpClient;
    private readonly TwitterApiIoOptions _options;
    private readonly ILogger<TwitterApiIoSearchClient> _logger;

    public TwitterApiIoSearchClient(
        HttpClient httpClient,
        IOptions<TwitterApiIoOptions> options,
        ILogger<TwitterApiIoSearchClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RawTweetDto>> SearchHashtagAsync(
        string hashtag,
        string? secondaryHashtag,
        DateTime sinceUtc,
        DateTime untilUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("TwitterApiIo:ApiKey is missing. Enable TwitterApiIo:UseMock or add a valid API key.");

        var primary   = NormalizeHashtag(hashtag);
        var secondary = NormalizeHashtag(secondaryHashtag);
        var hasSecondary = !string.IsNullOrEmpty(secondary)
                           && !string.Equals(primary, secondary, StringComparison.OrdinalIgnoreCase);

        var mode = (_options.HashtagSearchMode ?? "SeparateRequests").Trim();
        var useSeparateRequests = !string.Equals(mode, "OrQuery", StringComparison.OrdinalIgnoreCase);

        // Generate digit + hamza variants for each hashtag.
        // e.g. "#تحدي_الالقاء_للاطفال5" → 4 expressions covering both digit forms
        // and the hamza-restored form (#تحدي_الإلقاء_للأطفال5 / ٥).
        var primaryVariants = TwitterHashtagVariants.GenerateHashtagSearchVariants(hashtag);
        var secondaryVariants = hasSecondary
            ? TwitterHashtagVariants.GenerateHashtagSearchVariants(secondaryHashtag)
                  .Where(v => !primaryVariants.Contains(v, StringComparer.OrdinalIgnoreCase))
                  .ToList()
            : new List<string>();

        // Build the list of expressions to search.
        // SeparateRequests: one HTTP request per expression (variant).
        // OrQuery:          all variants (primary + secondary) merged into a single "(A OR B OR …)" query.
        var expressions = new List<string>();
        if (useSeparateRequests)
        {
            expressions.AddRange(primaryVariants);
            expressions.AddRange(secondaryVariants);
        }
        else
        {
            // OrQuery: always one request containing all unique variants
            var allVariants = primaryVariants
                .Concat(secondaryVariants)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            expressions.Add(allVariants.Count == 1
                ? allVariants[0]
                : "(" + string.Join(" OR ", allVariants) + ")");
        }

        _logger.LogInformation(
            "Twitter search planning. Mode={Mode} PrimaryHashtag={Primary} SecondaryHashtag={Secondary} HasSecondary={HasSecondary} EnableTopSearch={EnableTopSearch} ExpressionCount={Count} SinceUtc={Since:o} UntilUtc={Until:o}",
            useSeparateRequests ? "SeparateRequests" : "OrQuery",
            primary,
            string.IsNullOrEmpty(secondary) ? "(none)" : secondary,
            hasSecondary,
            _options.EnableTopSearch,
            expressions.Count,
            sinceUtc,
            untilUtc);

        var delaySeconds = Math.Max(0, _options.DelayBetweenQueryTypesSeconds);
        var all = new List<RawTweetDto>();
        var isFirst = true;

        foreach (var expression in expressions)
        {
            // Latest
            if (!isFirst && delaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            var (latestTweets, _) = await DoSearchRequestAsync(expression, sinceUtc, untilUtc, "Latest", cancellationToken);
            all.AddRange(latestTweets);
            isFirst = false;

            // Top
            if (_options.EnableTopSearch)
            {
                if (delaySeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                var (topTweets, _) = await DoSearchRequestAsync(expression, sinceUtc, untilUtc, "Top", cancellationToken);
                all.AddRange(topTweets);
            }
            else
            {
                _logger.LogInformation("Twitter search: TwitterApiIo:EnableTopSearch=false, skipping 'Top' query.");
            }
        }

        var deduped = all
            .Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .ToList();

        _logger.LogInformation(
            "Twitter search complete. RawTweetCount={Raw} DedupedCount={Dedup} PrimaryHashtag={Primary} SecondaryHashtag={Secondary}",
            all.Count,
            deduped.Count,
            primary,
            string.IsNullOrEmpty(secondary) ? "(none)" : secondary);

        return deduped;
    }

    // ── ITwitterSearchClient.FetchPageAsync ────────────────────────────────────

    // A window is considered saturated (needs splitting) only when the API returns
    // >= SaturationThreshold tweets. has_next_page=true alone is NOT sufficient —
    // the API sometimes returns has_next_page=true with very few results (even 1),
    // which caused runaway recursive splitting. has_next_page=true with a low count
    // is recorded as HasNextPageAdvisory for diagnostics only.
    private const int SaturationThreshold = 20;

    public async Task<TwitterSearchPageResult> FetchPageAsync(
        string expression,
        DateTime sinceUtc,
        DateTime untilUtc,
        string queryType,
        CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        Exception? lastEx = null;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var (tweets, hasNextPage) = await DoSearchRequestAsync(
                    expression, sinceUtc, untilUtc, queryType, cancellationToken);

                var isSaturated = tweets.Count >= SaturationThreshold;
                var hasNextPageAdvisory = hasNextPage && !isSaturated;

                if (hasNextPageAdvisory)
                    _logger.LogInformation(
                        "HasNextPageAdvisory: has_next_page=true but count={Count} < {Threshold}. " +
                        "Not treating as saturated — no split will occur. Expression={Expr} " +
                        "SinceUtc={Since:o} UntilUtc={Until:o}",
                        tweets.Count, SaturationThreshold, expression, sinceUtc, untilUtc);

                return new TwitterSearchPageResult(tweets, isSaturated, hasNextPageAdvisory);
            }
            catch (TwitterApiException ex) when (ex.IsRateLimited && attempt < maxRetries)
            {
                lastEx = ex;
                var seconds = ex.RetryAfter is not null && int.TryParse(ex.RetryAfter, out var s) ? s : 60;
                _logger.LogWarning(
                    "Rate limited (429) on attempt {Attempt}/{Max}. Waiting {Seconds}s. Expression={Expr}",
                    attempt, maxRetries, seconds, expression);
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
            }
            catch (TwitterApiException ex) when (!ex.IsRateLimited && (int)ex.StatusCode >= 500 && attempt < maxRetries)
            {
                lastEx = ex;
                var seconds = (int)Math.Pow(2, attempt) * 5; // 10s, 20s
                _logger.LogWarning(
                    "Server error {Status} on attempt {Attempt}/{Max}. Waiting {Seconds}s. Expression={Expr}",
                    (int)ex.StatusCode, attempt, maxRetries, seconds, expression);
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
            }
        }

        throw lastEx ?? new InvalidOperationException("FetchPageAsync: unexpected retry loop exit.");
    }

    // ── Internal single-page fetcher ────────────────────────────────────────────

    private async Task<(IReadOnlyList<RawTweetDto> tweets, bool hasNextPage)> DoSearchRequestAsync(
        string hashtagExpression,
        DateTime sinceUtc,
        DateTime untilUtc,
        string queryTypeValue,
        CancellationToken cancellationToken)
    {
        var sinceUnix = new DateTimeOffset(sinceUtc).ToUnixTimeSeconds();
        var untilUnix = new DateTimeOffset(untilUtc).ToUnixTimeSeconds();

        var decodedExpression = $"{hashtagExpression} since_time:{sinceUnix} until_time:{untilUnix}";
        var query = Uri.EscapeDataString(decodedExpression);
        var queryType = Uri.EscapeDataString(queryTypeValue);
        var requestUrl = $"/twitter/tweet/advanced_search?query={query}&queryType={queryType}";

        _logger.LogInformation(
            "Twitter search request. QueryType={QueryType} Expression={Expression} SinceUtc={SinceUtc:o} UntilUtc={UntilUtc:o} Url={Url}",
            queryTypeValue,
            decodedExpression,
            sinceUtc,
            untilUtc,
            requestUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Add("X-API-Key", _options.ApiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch
            {
                body = "<unable to read response body>";
            }

            var retryAfter = response.Headers.RetryAfter?.ToString();
            var rateLimitHeaders = string.Join(
                "; ",
                response.Headers
                    .Where(h => h.Key.StartsWith("x-rate", StringComparison.OrdinalIgnoreCase)
                                || h.Key.StartsWith("x-ratelimit", StringComparison.OrdinalIgnoreCase)
                                || h.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase))
                    .Select(h => $"{h.Key}={string.Join(",", h.Value)}"));

            _logger.LogWarning(
                "Twitter API non-success. QueryType={QueryType} Status={Status} Url={Url} Expression={Expression} SinceUtc={SinceUtc:o} UntilUtc={UntilUtc:o} RetryAfter={RetryAfter} RateLimitHeaders={RateLimitHeaders} Body={Body}",
                queryTypeValue,
                (int)response.StatusCode,
                requestUrl,
                decodedExpression,
                sinceUtc,
                untilUtc,
                retryAfter ?? "(none)",
                string.IsNullOrEmpty(rateLimitHeaders) ? "(none)" : rateLimitHeaders,
                body);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var msg = retryAfter is null
                    ? "Twitter API rate limit exceeded (429)."
                    : $"Twitter API rate limit exceeded (429). Retry-After={retryAfter}.";
                throw new TwitterApiException(msg, response.StatusCode, retryAfter);
            }

            if (response.StatusCode == HttpStatusCode.PaymentRequired)
            {
                throw new TwitterApiException(
                    "Twitter API payment/credits issue (402). Check twitterapi.io account balance/plan.",
                    response.StatusCode,
                    retryAfter);
            }

            throw new TwitterApiException(
                $"Twitter API call failed with status {(int)response.StatusCode}.",
                response.StatusCode,
                retryAfter);
        }

        var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(rawBody);

        var tweetsPropertyExists = doc.RootElement.ValueKind == JsonValueKind.Object
                                   && doc.RootElement.TryGetProperty("tweets", out var preTweets)
                                   && preTweets.ValueKind == JsonValueKind.Array;
        var tweetsArrayLength = tweetsPropertyExists
            ? doc.RootElement.GetProperty("tweets").GetArrayLength()
            : 0;

        _logger.LogInformation(
            "Twitter API success. QueryType={QueryType} Status={Status} Url={Url} Expression={Expression} BodyLength={BodyLength} TweetsPropertyExists={HasTweets} TweetsCount={Count}",
            queryTypeValue,
            (int)response.StatusCode,
            requestUrl,
            decodedExpression,
            rawBody?.Length ?? 0,
            tweetsPropertyExists,
            tweetsArrayLength);

        var result = new List<RawTweetDto>();

        var hasNextPage = doc.RootElement.TryGetProperty("has_next_page", out var hnp)
            && hnp.ValueKind == JsonValueKind.True;

        if (!doc.RootElement.TryGetProperty("tweets", out var tweets) ||
            tweets.ValueKind != JsonValueKind.Array)
        {
            return (result, hasNextPage);
        }

        foreach (var tweet in tweets.EnumerateArray())
        {
            var author = tweet.TryGetProperty("author", out var a) ? a : default;

            var urls = new List<RawTweetUrlDto>();
            var media = new List<RawTweetMediaDto>();

            if (tweet.TryGetProperty("entities", out var entities))
            {
                if (entities.TryGetProperty("urls", out var urlArray) &&
                    urlArray.ValueKind == JsonValueKind.Array)
                {
                    urls.AddRange(urlArray.EnumerateArray().Select(u => new RawTweetUrlDto
                    {
                        Url = GetString(u, "url"),
                        ExpandedUrl = GetString(u, "expanded_url"),
                        DisplayUrl = GetString(u, "display_url"),
                        RawJsonData = u.GetRawText()
                    }));
                }

                if (entities.TryGetProperty("media", out var mediaArray) &&
                    mediaArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mediaArray.EnumerateArray())
                    {
                        media.Add(new RawTweetMediaDto
                        {
                            Type = GetString(m, "type"),
                            Url = FirstNonEmpty(
                                GetString(m, "media_url_https"),
                                GetString(m, "url"),
                                GetString(m, "video_url")),
                            PreviewImageUrl = GetString(m, "preview_image_url"),
                            RawJsonData = m.GetRawText()
                        });
                    }
                }
            }

            if (tweet.TryGetProperty("extendedEntities", out var extendedEntities))
            {
                if (extendedEntities.TryGetProperty("media", out var extendedMediaArray) &&
                    extendedMediaArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in extendedMediaArray.EnumerateArray())
                    {
                        media.Add(new RawTweetMediaDto
                        {
                            Type = GetString(m, "type"),
                            Url = ExtractMediaUrl(m),
                            PreviewImageUrl = GetString(m, "preview_image_url"),
                            RawJsonData = m.GetRawText()
                        });
                    }
                }
            }

            if (tweet.TryGetProperty("media", out var rootMediaArray) &&
                rootMediaArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in rootMediaArray.EnumerateArray())
                {
                    media.Add(new RawTweetMediaDto
                    {
                        Type = GetString(m, "type"),
                        Url = FirstNonEmpty(
                            GetString(m, "media_url_https"),
                            GetString(m, "url"),
                            GetString(m, "video_url")),
                        PreviewImageUrl = GetString(m, "preview_image_url"),
                        RawJsonData = m.GetRawText()
                    });
                }
            }

            result.Add(new RawTweetDto
            {
                Id = GetString(tweet, "id"),
                Url = GetString(tweet, "url"),
                Text = GetString(tweet, "text"),
                CreatedAtUtc = ParseTwitterDate(GetString(tweet, "createdAt")),
                AuthorUserName = author.ValueKind == JsonValueKind.Object
                    ? GetString(author, "userName")
                    : string.Empty,
                AuthorDisplayName = author.ValueKind == JsonValueKind.Object
                    ? GetString(author, "name")
                    : string.Empty,
                AuthorDescription = author.ValueKind == JsonValueKind.Object
                    ? GetString(author, "description")
                    : null,
                AuthorLocation = author.ValueKind == JsonValueKind.Object
                    ? GetString(author, "location")
                    : null,
                AuthorFollowers = author.ValueKind == JsonValueKind.Object
                    ? GetInt(author, "followers")
                    : 0,
                AuthorIsBlueVerified = author.ValueKind == JsonValueKind.Object &&
                                       GetBool(author, "isBlueVerified"),
                LikeCount = GetInt(tweet, "likeCount"),
                RetweetCount = GetInt(tweet, "retweetCount"),
                ReplyCount = GetInt(tweet, "replyCount"),
                QuoteCount = GetInt(tweet, "quoteCount"),
                ViewCount = GetInt(tweet, "viewCount"),
                IsReply = GetBool(tweet, "isReply"),
                InReplyToId = GetString(tweet, "inReplyToId"),
                HasVideo = media.Any(m =>
                    m.Type.Equals("video", StringComparison.OrdinalIgnoreCase) ||
                    m.Type.Equals("animated_gif", StringComparison.OrdinalIgnoreCase) ||
                    m.Url.Contains(".mp4", StringComparison.OrdinalIgnoreCase)),
                RawJsonData = tweet.GetRawText(),
                Urls = urls,
                Media = media
            });
        }

        if (result.Count > 0)
        {
            var preview = result.Take(3).Select(t =>
            {
                var snippet = t.Text ?? string.Empty;
                if (snippet.Length > 120) snippet = snippet.Substring(0, 120) + "…";
                snippet = snippet.Replace("\n", " ").Replace("\r", " ");
                return $"{t.Id}::{snippet}";
            });
            _logger.LogInformation(
                "Twitter API parsed tweets. QueryType={QueryType} Expression={Expression} ParsedCount={Parsed} First3={First3}",
                queryTypeValue,
                decodedExpression,
                result.Count,
                string.Join(" | ", preview));
        }
        else
        {
            _logger.LogInformation(
                "Twitter API parsed zero tweets. QueryType={QueryType} Expression={Expression} BodyLength={BodyLength}",
                queryTypeValue,
                decodedExpression,
                rawBody?.Length ?? 0);
        }

        return (result, hasNextPage);
    }

    // Delegates to shared helpers — keeps logic in one place.
    private static string NormalizeHashtag(string? value)
        => TwitterHashtagVariants.NormalizeHashtag(value);

    // Kept for reflection-based tests in GenerateDigitVariantsTests.
    private static IReadOnlyList<string> GenerateDigitVariants(string? hashtag)
        => TwitterHashtagVariants.GenerateDigitVariants(hashtag);

    private static IReadOnlyList<string> GenerateHashtagSearchVariants(string? hashtag)
        => TwitterHashtagVariants.GenerateHashtagSearchVariants(hashtag);

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    }

    private static int GetInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            return parsed;

        return 0;
    }

    private static bool GetBool(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static DateTime ParseTwitterDate(string value)
    {
        if (DateTime.TryParse(value, out var parsed))
            return DateTime.SpecifyKind(parsed.ToUniversalTime(), DateTimeKind.Utc);

        return DateTime.UtcNow;
    }

    private static string ExtractMediaUrl(JsonElement media)
    {
        var directUrl = FirstNonEmpty(
            GetString(media, "media_url_https"),
            GetString(media, "url"),
            GetString(media, "video_url"));

        if (!string.IsNullOrWhiteSpace(directUrl))
            return directUrl;

        if (media.TryGetProperty("video_info", out var videoInfo) &&
            videoInfo.TryGetProperty("variants", out var variants) &&
            variants.ValueKind == JsonValueKind.Array)
        {
            foreach (var variant in variants.EnumerateArray())
            {
                var url = GetString(variant, "url");
                var contentType = GetString(variant, "content_type");

                if (!string.IsNullOrWhiteSpace(url) &&
                    contentType.Contains("mp4", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }
            }
        }

        return string.Empty;
    }
}