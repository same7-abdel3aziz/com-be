using System.Text.RegularExpressions;

namespace CompetitionManagementSystem.Services.Reporting;

/// <summary>
/// Lowercases Latin-script names, strips digits/punctuation, collapses whitespace.
/// Also extracts meaningful tokens from messy handles like "SaraMoh44003727" or "Hanan_TAD".
/// </summary>
public static class LatinNameNormalizer
{
    private static readonly Regex NonLatinSpace = new(@"[^a-z\s]+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex CamelCaseBoundary = new(@"(?<=[a-z])(?=[A-Z])", RegexOptions.Compiled);
    private static readonly Regex SeparatorRun = new(@"[_\-.\d]+", RegexOptions.Compiled);

    /// <summary>Canonical key for a single name string already known to be Latin.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim().ToLowerInvariant();
        s = NonLatinSpace.Replace(s, " ");
        s = Whitespace.Replace(s, " ").Trim();
        return s;
    }

    /// <summary>
    /// Pulls candidate name tokens out of a Latin display name or username. Splits on
    /// whitespace, underscores, dots, hyphens, digits, AND CamelCase boundaries.
    /// "Hanan_TAD"        → ["hanan", "tad"]
    /// "SaraMoh44003727"  → ["sara", "moh"]
    /// "FatimahSa2"       → ["fatimah", "sa"]
    /// "AishaAlfaifi2"    → ["aisha", "alfaifi"]
    /// "Zerriouh Mohammed ayoub" → ["zerriouh", "mohammed", "ayoub"]
    /// "Fatimah Abdullah" → ["fatimah", "abdullah"]
    /// </summary>
    public static IReadOnlyList<string> ExtractTokens(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        // Step 1: insert spaces at CamelCase boundaries BEFORE lowercasing.
        var s = CamelCaseBoundary.Replace(raw.Trim(), " ");
        // Step 2: replace separators (digits, _, -, .) with spaces.
        s = SeparatorRun.Replace(s, " ");
        // Step 3: lowercase, strip remaining non-letters, collapse.
        s = s.ToLowerInvariant();
        s = NonLatinSpace.Replace(s, " ");
        s = Whitespace.Replace(s, " ").Trim();

        if (s.Length == 0) return Array.Empty<string>();

        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(tokens.Length);
        foreach (var t in tokens)
        {
            if (t.Length >= 2) result.Add(t);
        }
        return result;
    }

    /// <summary>True if the string contains any ASCII Latin letter.</summary>
    public static bool ContainsLatinLetter(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        foreach (var c in raw)
        {
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return true;
        }
        return false;
    }

    /// <summary>True if the string contains any Arabic character (U+0600..U+06FF or U+0750..U+077F).</summary>
    public static bool ContainsArabicLetter(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        foreach (var c in raw)
        {
            if ((c >= '؀' && c <= 'ۿ') || (c >= 'ݐ' && c <= 'ݿ')) return true;
        }
        return false;
    }
}
