namespace CompetitionManagementSystem.Options;

public sealed class TwitterApiIoOptions
{
    public string BaseUrl { get; set; } = "https://api.twitterapi.io";
    public string ApiKey { get; set; } = string.Empty;
    public bool UseMock { get; set; } = true;
    public string QueryType { get; set; } = "Top";
    public int LookbackMinutes { get; set; } = 10;

    // When false, skip the "Top" search call and only run "Latest".
    // Used to reduce request volume against twitterapi.io during testing/rate-limit incidents.
    public bool EnableTopSearch { get; set; } = true;

    // Seconds to wait between the "Latest" and "Top" requests when both are enabled.
    // Helps avoid back-to-back bursts that trigger 429 from twitterapi.io.
    public int DelayBetweenQueryTypesSeconds { get; set; } = 6;

    // How the search expression is built when a secondary hashtag is configured.
    // "OrQuery"          -> one request per queryType using "(#A OR #B)".
    // "SeparateRequests" -> one request per hashtag per queryType, merged and deduplicated by tweet id.
    // Default is SeparateRequests because twitterapi.io's OR support has been observed to return zero results.
    public string HashtagSearchMode { get; set; } = "SeparateRequests";
}