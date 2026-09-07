using System.Text.Json;
using CompetitionManagementSystem.Options;
using CompetitionManagementSystem.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CompetitionManagementSystem.Controllers;

/// <summary>
/// DEBUG / DEVELOPMENT ONLY. Calls twitterapi.io directly and returns the raw response
/// so we can compare different query forms side-by-side without going through ingestion.
/// Disabled (returns 404) outside the Development environment.
/// </summary>
[ApiController]
[Route("api/debug/twitter-search")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{SystemRoles.SystemAdmin}")]
public sealed class DebugTwitterController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TwitterApiIoOptions _options;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<DebugTwitterController> _logger;

    public DebugTwitterController(
        IHttpClientFactory httpClientFactory,
        IOptions<TwitterApiIoOptions> options,
        IWebHostEnvironment env,
        ILogger<DebugTwitterController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _env = env;
        _logger = logger;
    }

    public sealed class DebugSearchRequest
    {
        public string Query { get; set; } = string.Empty;
        public string QueryType { get; set; } = "Latest";
    }

    public sealed class DebugSearchResponse
    {
        public string DecodedQuery { get; set; } = string.Empty;
        public string EncodedRequestUrl { get; set; } = string.Empty;
        public int HttpStatusCode { get; set; }
        public int ResponseBodyLength { get; set; }
        public string ResponseBody { get; set; } = string.Empty;
        public bool TweetsPropertyExists { get; set; }
        public int? TweetsCount { get; set; }
        public List<DebugTweetPreview> First3Tweets { get; set; } = new();
    }

    public sealed class DebugTweetPreview
    {
        public string Id { get; set; } = string.Empty;
        public string TextSnippet { get; set; } = string.Empty;
    }

    [HttpPost]
    public async Task<IActionResult> Search([FromBody] DebugSearchRequest body, CancellationToken cancellationToken)
    {
        // Development-only guard. Returns 404 in any other environment so the route effectively
        // does not exist in Staging/Production.
        if (!_env.IsDevelopment())
            return NotFound();

        if (body is null || string.IsNullOrWhiteSpace(body.Query))
            return BadRequest(new { error = "Query is required." });

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return StatusCode(500, new { error = "TwitterApiIo:ApiKey is missing." });

        var queryType = string.IsNullOrWhiteSpace(body.QueryType) ? "Latest" : body.QueryType.Trim();
        var decodedQuery = body.Query.Trim();
        var encodedQuery = Uri.EscapeDataString(decodedQuery);
        var encodedType = Uri.EscapeDataString(queryType);
        var relativeUrl = $"/twitter/tweet/advanced_search?query={encodedQuery}&queryType={encodedType}";

        var http = _httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/'));

        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Add("X-API-Key", _options.ApiKey);

        // Log without the API key.
        _logger.LogInformation(
            "Debug twitter-search. QueryType={QueryType} DecodedQuery={Decoded} Url={Url}",
            queryType,
            decodedQuery,
            relativeUrl);

        using var response = await http.SendAsync(request, cancellationToken);
        var bodyText = await response.Content.ReadAsStringAsync(cancellationToken);

        var result = new DebugSearchResponse
        {
            DecodedQuery = decodedQuery,
            EncodedRequestUrl = $"{_options.BaseUrl.TrimEnd('/')}{relativeUrl}",
            HttpStatusCode = (int)response.StatusCode,
            ResponseBodyLength = bodyText?.Length ?? 0,
            ResponseBody = bodyText ?? string.Empty,
            TweetsPropertyExists = false,
            TweetsCount = null
        };

        // Try to inspect the JSON shape, but never fail the endpoint on parse problems —
        // the raw body is what the caller needs to see.
        try
        {
            using var doc = JsonDocument.Parse(bodyText ?? string.Empty);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("tweets", out var tweets) &&
                tweets.ValueKind == JsonValueKind.Array)
            {
                result.TweetsPropertyExists = true;
                result.TweetsCount = tweets.GetArrayLength();

                foreach (var tweet in tweets.EnumerateArray().Take(3))
                {
                    var id = tweet.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                        ? (idEl.GetString() ?? string.Empty)
                        : string.Empty;
                    var text = tweet.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                        ? (textEl.GetString() ?? string.Empty)
                        : string.Empty;
                    if (text.Length > 200)
                        text = text.Substring(0, 200) + "…";
                    text = text.Replace("\n", " ").Replace("\r", " ");
                    result.First3Tweets.Add(new DebugTweetPreview { Id = id, TextSnippet = text });
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Debug twitter-search: response body was not valid JSON.");
        }

        return Ok(result);
    }
}