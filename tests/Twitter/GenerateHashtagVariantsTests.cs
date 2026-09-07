using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CompetitionManagementSystem.Options;
using CompetitionManagementSystem.Services.Twitter;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CompetitionManagementSystem.Tests.Twitter;

/// <summary>
/// Tests for GenerateHashtagSearchVariants / ApplyHamzaVariant via reflection.
/// TwitterAPI.io does NOT normalise Arabic hamza — الإلقاء ≠ الالقاء in search.
/// These tests confirm we generate all necessary variant expressions.
/// </summary>
public class GenerateHashtagVariantsTests
{
    // ── Reflection helpers ───────────────────────────────────────────────────

    private static readonly Type _variantsType =
        typeof(TwitterApiIoSearchClient).Assembly.GetType(
            "CompetitionManagementSystem.Services.Twitter.TwitterHashtagVariants")
        ?? throw new InvalidOperationException("TwitterHashtagVariants not found");

    private static IReadOnlyList<string> SearchVariants(string? hashtag)
    {
        var method = _variantsType.GetMethod(
            "GenerateHashtagSearchVariants", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("GenerateHashtagSearchVariants not found");
        return (IReadOnlyList<string>)method.Invoke(null, new object?[] { hashtag })!;
    }

    private static string? HamzaVariant(string hashtag)
    {
        var method = _variantsType.GetMethod(
            "ApplyHamzaVariant", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ApplyHamzaVariant not found");
        return (string?)method.Invoke(null, new object[] { hashtag });
    }

    // ── ApplyHamzaVariant ────────────────────────────────────────────────────

    // HV-1: الالقاء → الإلقاء  (الا → الإ pattern)
    [Fact]
    public void HV1_AlalqaaPattern_ProducesHamzaBelow()
    {
        var result = HamzaVariant("#تحدي_الالقاء_للاطفال5");
        Assert.NotNull(result);
        Assert.Contains("الإلقاء", result);
    }

    // HV-2: للاطفال → للأطفال  (للا → للأ pattern)
    [Fact]
    public void HV2_LilAtfalPattern_ProducesHamzaAbove()
    {
        var result = HamzaVariant("#تحدي_الالقاء_للاطفال5");
        Assert.NotNull(result);
        Assert.Contains("للأطفال", result);
    }

    // HV-3: No الا or للا patterns → returns null (no hamza variant)
    [Fact]
    public void HV3_NoHamzaPatterns_ReturnsNull()
    {
        Assert.Null(HamzaVariant("#تحدي5"));
        Assert.Null(HamzaVariant("#abc123"));
    }

    // HV-4: Full competition hashtag → 4 variants
    [Fact]
    public void HV4_FullCompetitionHashtag_ProducesFourVariants()
    {
        var result = SearchVariants("#تحدي_الالقاء_للاطفال5");

        Assert.Equal(4, result.Count);
        Assert.Contains("#تحدي_الالقاء_للاطفال5",  result, StringComparer.Ordinal);
        Assert.Contains("#تحدي_الالقاء_للاطفال٥",  result, StringComparer.Ordinal);
        Assert.Contains("#تحدي_الإلقاء_للأطفال5", result, StringComparer.Ordinal);
        Assert.Contains("#تحدي_الإلقاء_للأطفال٥", result, StringComparer.Ordinal);
    }

    // HV-5: Arabic-digit input → same 4 variants as ASCII-digit input
    [Fact]
    public void HV5_ArabicDigitInput_SameFourVariants()
    {
        var fromAscii  = SearchVariants("#تحدي_الالقاء_للاطفال5");
        var fromArabic = SearchVariants("#تحدي_الالقاء_للاطفال٥");

        Assert.Equal(fromAscii.OrderBy(x => x), fromArabic.OrderBy(x => x));
    }

    // HV-6: Secondary hashtag (no number) + hamza patterns → 2 variants
    [Fact]
    public void HV6_SecondaryHashtagNoDigits_ProducesTwoVariants()
    {
        var result = SearchVariants("#تحدي_الالقاء_للاطفال");

        Assert.Equal(2, result.Count);
        Assert.Contains("#تحدي_الالقاء_للاطفال",  result, StringComparer.Ordinal);
        Assert.Contains("#تحدي_الإلقاء_للأطفال", result, StringComparer.Ordinal);
    }

    // HV-7: Non-Arabic hashtag with digit → only 2 digit variants, no hamza
    [Fact]
    public void HV7_NonArabicHashtag_OnlyDigitVariants()
    {
        var result = SearchVariants("#test5");

        Assert.Equal(2, result.Count);
        Assert.Contains("#test5",  result, StringComparer.Ordinal);
        Assert.Contains("#test٥", result, StringComparer.Ordinal);
    }

    // HV-8: No digits, no hamza patterns → single variant
    [Fact]
    public void HV8_NoDigitsNoHamzaPatterns_SingleVariant()
    {
        var result = SearchVariants("#تحدي");
        Assert.Single(result);
        Assert.Equal("#تحدي", result[0]);
    }

    // HV-9: No duplicate expressions in result
    [Fact]
    public void HV9_NoDuplicatesInResult()
    {
        var result = SearchVariants("#تحدي_الالقاء_للاطفال5");
        Assert.Equal(result.Count, result.Distinct(StringComparer.Ordinal).Count());
    }
}

/// <summary>
/// Integration-level tests: SearchHashtagAsync sends correct number of HTTP requests
/// and deduplicates tweets across hamza + digit variant queries.
/// </summary>
public class SearchHashtagHamzaExpressionTests
{
    private static string BuildTweetJson(string id, string text = "tweet") =>
        JsonSerializer.Serialize(new
        {
            id, text,
            url = $"https://x.com/user/status/{id}",
            createdAt = "Mon Jan 01 00:00:00 +0000 2026",
            likeCount = 0, retweetCount = 0, replyCount = 0, quoteCount = 0, viewCount = 0,
            isReply = false, inReplyToId = (string?)null,
            author = new { userName = "user", name = "User", isBlueVerified = false, followers = 0 }
        });

    private static string BuildResponse(IEnumerable<string> tweets, bool hnp = false) =>
        $"{{\"tweets\":[{string.Join(",", tweets)}],\"has_next_page\":{(hnp ? "true" : "false")}}}";

    private static string EmptyResponse() => "{\"tweets\":[],\"has_next_page\":false}";

    private static (TwitterApiIoSearchClient client, List<string> urls)
        CreateClient(bool enableTop = false)
    {
        var urls = new List<string>();
        var handler = new FakeHandler(req =>
        {
            urls.Add(req.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EmptyResponse(), Encoding.UTF8, "application/json")
            };
        });
        var opts = new TwitterApiIoOptions
        {
            BaseUrl = "https://api.twitterapi.io",
            ApiKey  = "test-key",
            UseMock = false,
            EnableTopSearch = enableTop,
            DelayBetweenQueryTypesSeconds = 0,
            HashtagSearchMode = "SeparateRequests"
        };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(opts.BaseUrl) };
        var client = new TwitterApiIoSearchClient(
            httpClient,
            new OptionsWrapper<TwitterApiIoOptions>(opts),
            NullLogger<TwitterApiIoSearchClient>.Instance);
        return (client, urls);
    }

    private readonly DateTime _since = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly DateTime _until = new(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);

    // HE-1: Arabic hashtag with both الا and للا patterns → 4 Latest requests
    [Fact]
    public async Task HE1_ArabicWithHamzaPattern_SendsFourRequests()
    {
        var (client, urls) = CreateClient(enableTop: false);

        await client.SearchHashtagAsync(
            "#تحدي_الالقاء_للاطفال5", null, _since, _until, CancellationToken.None);

        Assert.Equal(4, urls.Count);

        var decoded = urls.Select(u => Uri.UnescapeDataString(u)).ToList();
        Assert.Contains(decoded, u => u.Contains("الالقاء") && !u.Contains("الإلقاء") && u.Contains("للاطفال5"));
        Assert.Contains(decoded, u => u.Contains("الالقاء") && !u.Contains("الإلقاء") && u.Contains("للاطفال٥"));
        Assert.Contains(decoded, u => u.Contains("الإلقاء") && u.Contains("للأطفال5"));
        Assert.Contains(decoded, u => u.Contains("الإلقاء") && u.Contains("للأطفال٥"));
    }

    // HE-2: Tweet returned by multiple variant queries is deduplicated to one
    [Fact]
    public async Task HE2_SameTweetFromHamzaAndBareAlefVariant_DedupedToOne()
    {
        var sharedId = "999000111222333444";
        var response = BuildResponse(new[] { BuildTweetJson(sharedId) });

        var handler = new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        var opts = new TwitterApiIoOptions
        {
            BaseUrl = "https://api.twitterapi.io", ApiKey = "test-key",
            UseMock = false, EnableTopSearch = false,
            DelayBetweenQueryTypesSeconds = 0, HashtagSearchMode = "SeparateRequests"
        };
        var client = new TwitterApiIoSearchClient(
            new HttpClient(handler) { BaseAddress = new Uri(opts.BaseUrl) },
            new OptionsWrapper<TwitterApiIoOptions>(opts),
            NullLogger<TwitterApiIoSearchClient>.Instance);

        var result = await client.SearchHashtagAsync(
            "#تحدي_الالقاء_للاطفال5", null, _since, _until, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(sharedId, result[0].Id);
    }

    // HE-3: Secondary hashtag with hamza patterns adds 2 more unique expressions
    [Fact]
    public async Task HE3_SecondaryWithHamzaPattern_AddsVariantsWithoutDuplicates()
    {
        // Primary #تحدي_الالقاء_للاطفال5  → 4 variants
        // Secondary #تحدي_الالقاء_للاطفال → 2 variants (bare-alef + hamza, no number)
        // Total unique = 6 requests
        var (client, urls) = CreateClient(enableTop: false);

        await client.SearchHashtagAsync(
            "#تحدي_الالقاء_للاطفال5", "#تحدي_الالقاء_للاطفال",
            _since, _until, CancellationToken.None);

        Assert.Equal(6, urls.Count);
    }

    // HE-4: OrQuery with hamza patterns → single request containing all 4 variants
    [Fact]
    public async Task HE4_OrQuery_WithHamzaPattern_SingleRequestAllVariants()
    {
        var handler = new FakeHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EmptyResponse(), Encoding.UTF8, "application/json")
            });
        var opts = new TwitterApiIoOptions
        {
            BaseUrl = "https://api.twitterapi.io", ApiKey = "test-key",
            UseMock = false, EnableTopSearch = false,
            DelayBetweenQueryTypesSeconds = 0, HashtagSearchMode = "OrQuery"
        };
        var urls = new List<string>();
        var interceptHandler = new FakeHandler(req =>
        {
            urls.Add(req.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EmptyResponse(), Encoding.UTF8, "application/json")
            };
        });
        var client = new TwitterApiIoSearchClient(
            new HttpClient(interceptHandler) { BaseAddress = new Uri(opts.BaseUrl) },
            new OptionsWrapper<TwitterApiIoOptions>(opts),
            NullLogger<TwitterApiIoSearchClient>.Instance);

        await client.SearchHashtagAsync(
            "#تحدي_الالقاء_للاطفال5", null, _since, _until, CancellationToken.None);

        Assert.Single(urls); // single OR-combined request
        var decoded = Uri.UnescapeDataString(urls[0]);
        Assert.Contains(" OR ", decoded);
        Assert.Contains("الالقاء",  decoded);
        Assert.Contains("الإلقاء", decoded);
        Assert.Contains("للأطفال", decoded);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct) => Task.FromResult(_fn(req));
    }
}
