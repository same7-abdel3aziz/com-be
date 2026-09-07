using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace CompetitionManagementSystem.Services.Reporting;

public sealed record GenderInferenceResult(string Gender, string GenderArabic, string? MatchedNormalizedName);

public interface IGenderInferenceService
{
    /// <summary>
    /// Loads the active gender dictionary into an in-memory map keyed by NormalizedName.
    /// Call once per report export; do NOT call per row.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> LoadDictionaryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Infers gender for a participant. Order:
    ///   1) Full display-name lookup (Arabic or Latin depending on script).
    ///   2) Each individual token from the display name (left to right).
    ///   3) Tokens from the username as a final fallback.
    /// Returns Unknown when no token matches or the matched entry is Ambiguous.
    /// </summary>
    GenderInferenceResult Infer(string? displayName, string? userName, IReadOnlyDictionary<string, string> dictionary);
}

public sealed class GenderInferenceService : IGenderInferenceService
{
    private readonly ApplicationDbContext _db;

    public GenderInferenceService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadDictionaryAsync(CancellationToken cancellationToken)
    {
        var rows = await _db.GenderNameDictionary
            .AsNoTracking()
            .Where(d => d.IsActive)
            .Select(d => new { d.NormalizedName, d.Gender })
            .ToListAsync(cancellationToken);

        var map = new Dictionary<string, string>(rows.Count, StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (map.TryGetValue(r.NormalizedName, out var existing))
            {
                if (existing == GenderValues.Ambiguous && r.Gender != GenderValues.Ambiguous)
                    map[r.NormalizedName] = r.Gender;
                continue;
            }
            map[r.NormalizedName] = r.Gender;
        }
        return map;
    }

    public GenderInferenceResult Infer(string? displayName, string? userName, IReadOnlyDictionary<string, string> dictionary)
    {
        var fromDisplay = TryInferFrom(displayName, dictionary);
        if (fromDisplay is not null) return fromDisplay;

        var fromUser = TryInferFrom(userName, dictionary);
        if (fromUser is not null) return fromUser;

        return new GenderInferenceResult(GenderValues.Unknown, GenderValues.UnknownArabic, null);
    }

    /// <summary>
    /// Returns a non-Unknown result if any token from the input resolves to Male/Female.
    /// Returns null if no usable signal was found (so the caller can try the next input).
    /// </summary>
    private static GenderInferenceResult? TryInferFrom(string? raw, IReadOnlyDictionary<string, string> dictionary)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Arabic path: keep prior behavior (whole-name + first token via ArabicNameNormalizer.FirstToken).
        if (LatinNameNormalizer.ContainsArabicLetter(raw))
        {
            // Try the WHOLE normalized name first (handles "أحمد المعافري" → "احمد المعافري" — if the full
            // normalized form happens to be in the dictionary, take it; otherwise fall through to tokens).
            var whole = ArabicNameNormalizer.Normalize(raw);
            if (whole.Length > 0)
            {
                if (TryLookup(whole, dictionary, out var wholeResult)) return wholeResult;

                // Walk each whitespace-separated Arabic token left to right.
                foreach (var token in whole.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Length < 2) continue;
                    if (TryLookup(token, dictionary, out var tokenResult)) return tokenResult;
                }
            }
            // Fall through to Latin tokens below too — display names like "أحمد Mohammed" benefit.
        }

        // Latin path.
        var tokens = LatinNameNormalizer.ExtractTokens(raw);
        if (tokens.Count == 0) return null;

        // 1) Try the full normalized form (e.g. "fatimah abdullah" might match a multi-word entry).
        var full = LatinNameNormalizer.Normalize(raw);
        if (full.Length > 0 && TryLookup(full, dictionary, out var fullResult)) return fullResult;

        // 2) Walk individual tokens.
        foreach (var t in tokens)
        {
            if (TryLookup(t, dictionary, out var r)) return r;
        }

        return null;
    }

    private static bool TryLookup(string key, IReadOnlyDictionary<string, string> dictionary, out GenderInferenceResult result)
    {
        if (dictionary.TryGetValue(key, out var raw)
            && (raw == GenderValues.Male || raw == GenderValues.Female))
        {
            result = new GenderInferenceResult(raw, GenderValues.ToArabic(raw), key);
            return true;
        }
        result = default!;
        return false;
    }
}
