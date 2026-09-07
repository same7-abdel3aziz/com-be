using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using CompetitionManagementSystem.Options;
using CompetitionManagementSystem.Services.Twitter;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CompetitionManagementSystem.Tests.Twitter;

/// <summary>
/// Tests that TwitterApiIoSearchClient sends the correct number of expressions
/// (SeparateRequests vs OrQuery) and deduplicates tweet IDs correctly.
///
/// Uses a fake HttpMessageHandler to intercept all HTTP calls so no real API key is needed.
/// </summary>
public class SearchHashtagExpressionTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static string BuildTweetJson(string id, string text = "tweet") =>
        JsonSerializer.Serialize(new
        {
            id,
            text,
            url = $"https://x.com/user/status/{id}",
            createdAt = "Mon Jan 01 00:00:00 +0000 2026",
            likeCount = 0, retweetCount = 0, replyCount = 0, quoteCount = 0, viewCount = 0,
            isReply = false, inReplyToId = (string?)null,
            author = new { userName = "user", name = "User", isBlueVerified = false, followers = 0 }
        });

    private static string BuildResponse(IEnumerable<string> tweetJsons, bool hasNextPage = false) =>
        $"{{\"tweets\":[{string.Join(",", tweetJsons)}],\"has_next_page\":{(hasNextPage ? "true" : "false")}}}";

    private static string EmptyResponse() => "{\"tweets\":[],\"has_next_page\":false}";

    /// <summary>
    /// Creates a client whose HTTP handler records every request URL and returns
    /// the provided responses in order. The handler cycles through responses if
    /// there are more requests than responses.
    /// </summary>
    private static (TwitterApiIoSearchClient client, List<string> capturedUrls)
        CreateClient(TwitterApiIoOptions options, IReadOnlyList<string> responses)
    {
        var capturedUrls = new List<string>();
        var callIndex    = 0;

        var handler = new FakeHandler(req =>
        {
            capturedUrls.Add(req.RequestUri!.PathAndQuery);
            var body = responses[callIndex % responses.Count];
            callIndex++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl) };
        var client = new TwitterApiIoSearchClient(
            httpClient,
            new OptionsWrapper<TwitterApiIoOptions>(options),
            NullLogger<TwitterApiIoSearchClient>.Instance);

        return (client, capturedUrls);
    }

    private static TwitterApiIoOptions SeparateRequestsOptions(bool enableTop = false) => new()
    {
        BaseUrl              = "https://api.twitterapi.io",
        ApiKey               = "test-key",
        UseMock              = false,
        EnableTopSearch      = enableTop,
        DelayBetweenQueryTypesSeconds = 0,
        HashtagSearchMode    = "SeparateRequests"
    };

    private static TwitterApiIoOptions OrQueryOptions(bool enableTop = false) => new()
    {
        BaseUrl              = "https://api.twitterapi.io",
        ApiKey               = "test-key",
        UseMock              = false,
        EnableTopSearch      = enableTop,
        DelayBetweenQueryTypesSeconds = 0,
        HashtagSearchMode    = "OrQuery"
    };

    private readonly DateTime _since = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly DateTime _until = new(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);

    // ──────────────────────────────────────────────────────────────────────────
    // TC-8 / TC-4: SeparateRequests sends both digit variants
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeparateRequests_WithAsciiHashtag_SendsTwoLatestRequests()
    {
        var opts = SeparateRequestsOptions(enableTop: false);
        var (client, urls) = CreateClient(opts, new[] { EmptyResponse(), EmptyResponse() });

        await client.SearchHashtagAsync("#تحدي5", null, _since, _until, CancellationToken.None);

        // One Latest request per variant (ASCII + Arabic-Indic)
        Assert.Equal(2, urls.Count);
        Assert.Contains(urls, u => u.Contains("%235") || u.Contains("5"));  // ASCII variant
        Assert.Contains(urls, u => u.Contains("%D9%A5") || u.Contains("٥")); // Arabic variant encoded
    }

    [Fact]
    public async Task SeparateRequests_HashtagWithNoDigits_SendsOneLatestRequest()
    {
        var opts = SeparateRequestsOptions(enableTop: false);
        var (client, urls) = CreateClient(opts, new[] { EmptyResponse() });

        await client.SearchHashtagAsync("#تحدي", null, _since, _until, CancellationToken.None);

        Assert.Single(urls);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-5: OrQuery merges all variants into a single OR expression
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OrQuery_WithAsciiHashtag_SendsSingleRequestContainingBothVariants()
    {
        var opts = OrQueryOptions(enableTop: false);
        var (client, urls) = CreateClient(opts, new[] { EmptyResponse() });

        await client.SearchHashtagAsync("#تحدي5", null, _since, _until, CancellationToken.None);

        // Single request for Latest
        Assert.Single(urls);
        var decoded = Uri.UnescapeDataString(urls[0]);
        Assert.Contains("#تحدي5", decoded);
        Assert.Contains("#تحدي٥", decoded);
        Assert.Contains(" OR ", decoded);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-6: Duplicate tweet IDs from different variants are deduplicated
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeparateRequests_SameTweetFromBothVariants_DedupedToOne()
    {
        var opts = SeparateRequestsOptions(enableTop: false);
        var sharedTweetId = "111111111111111111";
        var response = BuildResponse(new[] { BuildTweetJson(sharedTweetId) });

        // Both variant requests return the same tweet
        var (client, _) = CreateClient(opts, new[] { response, response });

        var result = await client.SearchHashtagAsync("#تحدي5", null, _since, _until, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(sharedTweetId, result[0].Id);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-7: Latest + Top do not produce duplicate tweets
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeparateRequests_LatestAndTop_NoduplicateTweets()
    {
        var opts = SeparateRequestsOptions(enableTop: true);
        var sharedId   = "222222222222222222";
        var uniqueId   = "333333333333333333";
        var bothResponse = BuildResponse(new[] { BuildTweetJson(sharedId) });
        var topResponse  = BuildResponse(new[] { BuildTweetJson(sharedId), BuildTweetJson(uniqueId) });

        // Calls: ASCII-Latest, Arabic-Latest, ASCII-Top, Arabic-Top
        var (client, _) = CreateClient(opts, new[] { bothResponse, bothResponse, topResponse, bothResponse });

        var result = await client.SearchHashtagAsync("#تحدي5", null, _since, _until, CancellationToken.None);

        var ids = result.Select(t => t.Id).ToList();
        Assert.Equal(ids.Distinct().Count(), ids.Count); // no duplicates
        Assert.Contains(sharedId, ids);
        Assert.Contains(uniqueId, ids);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TC-8: Secondary hashtag still works alongside primary variants
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeparateRequests_WithSecondary_SendsVariantsForBoth()
    {
        var opts = SeparateRequestsOptions(enableTop: false);
        var responses = Enumerable.Repeat(EmptyResponse(), 10).ToList();
        var (client, urls) = CreateClient(opts, responses);

        // Primary "#تحدي5" → 2 variants, Secondary "#تحدي" → 1 variant → 3 Latest requests
        await client.SearchHashtagAsync("#تحدي5", "#تحدي", _since, _until, CancellationToken.None);

        Assert.Equal(3, urls.Count);
    }

    [Fact]
    public async Task SeparateRequests_SecondaryWithNoDigits_SingleSecondaryRequest()
    {
        var opts = SeparateRequestsOptions(enableTop: false);
        var responses = Enumerable.Repeat(EmptyResponse(), 10).ToList();
        var (client, urls) = CreateClient(opts, responses);

        // Secondary "#secondary" has no digits → 1 variant
        await client.SearchHashtagAsync("#تحدي5", "#secondary", _since, _until, CancellationToken.None);

        // 2 (primary variants) + 1 (secondary) = 3
        Assert.Equal(3, urls.Count);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Fake HttpMessageHandler
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_handler(request));
    }
}
