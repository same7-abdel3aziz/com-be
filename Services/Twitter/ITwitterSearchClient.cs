namespace CompetitionManagementSystem.Services.Twitter;

public interface ITwitterSearchClient
{
    /// <summary>
    /// High-level search: fetches all variants (digit + secondary) and returns deduplicated tweets.
    /// Used by regular ingestion (<see cref="Services.Ingestion.IngestionService.RunForCompetitionAsync"/>).
    /// </summary>
    Task<IReadOnlyList<RawTweetDto>> SearchHashtagAsync(
        string hashtag,
        string? secondaryHashtag,
        DateTime sinceUtc,
        DateTime untilUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Low-level single-expression, single-page fetch with saturation detection.
    /// Used by <see cref="Services.Ingestion.IngestionService.BackfillRangeAsync"/> for adaptive
    /// window splitting: if <see cref="TwitterSearchPageResult.IsSaturated"/> is true the caller
    /// should split the window and retry smaller sub-windows.
    /// Includes retry logic for 429 (Retry-After) and 5xx (exponential back-off, max 3 attempts).
    /// </summary>
    Task<TwitterSearchPageResult> FetchPageAsync(
        string expression,
        DateTime sinceUtc,
        DateTime untilUtc,
        string queryType,
        CancellationToken cancellationToken);
}
