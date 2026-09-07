namespace CompetitionManagementSystem.Services.Twitter;

/// <summary>
/// Shared helpers for hashtag normalisation and digit variant generation.
/// Extracted so both TwitterApiIoSearchClient and IngestionService can use them
/// without duplication.
/// </summary>
internal static class TwitterHashtagVariants
{
    internal static string NormalizeHashtag(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(v)) return string.Empty;
        return v.StartsWith('#') ? v : "#" + v;
    }

    /// <summary>
    /// Returns the ASCII-digit variant and the Arabic-Indic-digit variant.
    /// If the hashtag contains no digits, only one element is returned.
    /// </summary>
    internal static IReadOnlyList<string> GenerateDigitVariants(string? hashtag)
    {
        var normalized = NormalizeHashtag(hashtag);
        if (string.IsNullOrEmpty(normalized)) return Array.Empty<string>();

        var ascii  = ArabicIndicToAscii(normalized);
        var arabic = AsciiToArabicIndic(normalized);

        return string.Equals(ascii, arabic, StringComparison.Ordinal)
            ? (IReadOnlyList<string>)new[] { ascii }
            : new[] { ascii, arabic };
    }

    /// <summary>
    /// Generates the hamza variant of a hashtag by applying the most common Arabic
    /// hamza restoration patterns. Returns null when no change is needed.
    ///
    /// TwitterAPI.io does NOT normalise hamza — searching #الالقاء misses #الإلقاء.
    /// Patterns covered:
    ///   الا → الإ  (ال definite article + alef-kasra verbal-noun stem)
    ///   للا → للأ  (لل double-lam + alef-fatha plural stem)
    /// </summary>
    internal static string? ApplyHamzaVariant(string hashtag)
    {
        var result = hashtag
            .Replace("الا", "الإ")
            .Replace("للا", "للأ");
        return string.Equals(result, hashtag, StringComparison.Ordinal) ? null : result;
    }

    /// <summary>
    /// Returns all search-query variants for a hashtag:
    ///   • bare-alef form  × {ASCII digit, Arabic-Indic digit}
    ///   • hamza form      × {ASCII digit, Arabic-Indic digit}  (when applicable)
    ///
    /// For #تحدي_الالقاء_للاطفال5 this produces four expressions:
    ///   #تحدي_الالقاء_للاطفال5  #تحدي_الالقاء_للاطفال٥
    ///   #تحدي_الإلقاء_للأطفال5  #تحدي_الإلقاء_للأطفال٥
    /// </summary>
    internal static IReadOnlyList<string> GenerateHashtagSearchVariants(string? hashtag)
    {
        var normalized = NormalizeHashtag(hashtag);
        if (string.IsNullOrEmpty(normalized)) return Array.Empty<string>();

        // Normalise input to ASCII digits before generating variants so we
        // always start from a consistent base form regardless of what was stored.
        var baseAscii = ArabicIndicToAscii(normalized);

        var textVariants = new List<string> { baseAscii };
        var hamza = ApplyHamzaVariant(baseAscii);
        if (hamza != null) textVariants.Add(hamza);

        var result = new List<string>();
        foreach (var tv in textVariants)
        {
            var ascii  = tv; // already ASCII digits (baseAscii was converted above)
            var arabic = AsciiToArabicIndic(tv);
            result.Add(ascii);
            if (!string.Equals(ascii, arabic, StringComparison.Ordinal))
                result.Add(arabic);
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Builds the deduplicated list of search expressions for a primary + optional secondary hashtag,
    /// ready to be passed to FetchPageAsync one by one.
    /// </summary>
    internal static IReadOnlyList<string> BuildExpressions(string? primary, string? secondary)
    {
        var primaryVariants = GenerateHashtagSearchVariants(primary);

        var normalizedSecondary = NormalizeHashtag(secondary);
        var hasSecondary = !string.IsNullOrEmpty(normalizedSecondary)
            && !string.Equals(NormalizeHashtag(primary), normalizedSecondary, StringComparison.OrdinalIgnoreCase);

        if (!hasSecondary) return primaryVariants;

        var secondaryVariants = GenerateHashtagSearchVariants(secondary)
            .Where(v => !primaryVariants.Contains(v, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return primaryVariants.Concat(secondaryVariants).ToList();
    }

    internal static string ArabicIndicToAscii(string s) => s
        .Replace('٠', '0').Replace('١', '1').Replace('٢', '2').Replace('٣', '3').Replace('٤', '4')
        .Replace('٥', '5').Replace('٦', '6').Replace('٧', '7').Replace('٨', '8').Replace('٩', '9');

    internal static string AsciiToArabicIndic(string s) => s
        .Replace('0', '٠').Replace('1', '١').Replace('2', '٢').Replace('3', '٣').Replace('4', '٤')
        .Replace('5', '٥').Replace('6', '٦').Replace('7', '٧').Replace('8', '٨').Replace('9', '٩');
}
