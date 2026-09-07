namespace CompetitionManagementSystem.Services.Twitter;

/// <summary>
/// Result of a single-page Advanced Search call.
/// IsSaturated is true when the API returned >= SaturationThreshold (20) tweets,
/// indicating the window may contain more tweets than one page can hold.
/// HasNextPageAdvisory is true when the API returned has_next_page=true but the
/// tweet count is below SaturationThreshold — logged for diagnostics only, does NOT
/// trigger recursive splitting.
/// </summary>
public sealed record TwitterSearchPageResult(
    IReadOnlyList<RawTweetDto> Tweets,
    bool IsSaturated,
    bool HasNextPageAdvisory = false);
