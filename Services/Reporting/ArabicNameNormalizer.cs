using System.Text.RegularExpressions;

namespace CompetitionManagementSystem.Services.Reporting;

/// <summary>
/// Arabic name canonicalization for the gender dictionary lookup. Strips diacritics,
/// unifies common variants (أ/إ/آ → ا, ى → ي, ة → ه, …), removes tatweel, collapses
/// whitespace, lowercases, and extracts the first meaningful token.
/// </summary>
public static class ArabicNameNormalizer
{
    private static readonly Regex Tashkeel = new(@"[ؐ-ًؚ-ٰٟۖ-ۭ]", RegexOptions.Compiled);
    private static readonly Regex Punct = new(@"[\p{P}\p{S}]", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Full normalization for dictionary keys and CSV entries.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim().ToLowerInvariant();

        // Drop tashkeel/diacritics.
        s = Tashkeel.Replace(s, "");
        // Drop tatweel.
        s = s.Replace('ـ', ' ');
        // Letter unification.
        s = s
            .Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ٱ', 'ا')
            .Replace('ى', 'ي').Replace('ئ', 'ي')
            .Replace('ؤ', 'و')
            .Replace('ة', 'ه');
        // Arabic-Indic digits → ASCII.
        s = s
            .Replace('٠', '0').Replace('١', '1').Replace('٢', '2').Replace('٣', '3')
            .Replace('٤', '4').Replace('٥', '5').Replace('٦', '6').Replace('٧', '7')
            .Replace('٨', '8').Replace('٩', '9');
        // Punctuation / symbols / emojis → space.
        s = Punct.Replace(s, " ");
        // Collapse whitespace.
        s = Whitespace.Replace(s, " ").Trim();

        return s;
    }

    /// <summary>
    /// Returns the first meaningful token of a display name after normalization.
    /// Skips a single leading honorific if present (e.g. "د.", "م."). If nothing
    /// usable remains, returns an empty string.
    /// </summary>
    public static string FirstToken(string? displayName)
    {
        var normalized = Normalize(displayName);
        if (normalized.Length == 0) return string.Empty;

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return string.Empty;

        // Skip a single short honorific token if present.
        var skip = 0;
        if (tokens.Length > 1 && IsHonorific(tokens[0])) skip = 1;
        return tokens[skip];
    }

    private static bool IsHonorific(string token) => token switch
    {
        "د" or "م" or "أ" or "ا" or "mr" or "ms" or "mrs" or "dr" or "eng" => true,
        _ => false,
    };
}
