using System.Reflection;
using CompetitionManagementSystem.Services.Twitter;

namespace CompetitionManagementSystem.Tests.Twitter;

/// <summary>
/// Unit tests for the private GenerateDigitVariants / ArabicIndicToAscii / AsciiToArabicIndic
/// helpers in TwitterApiIoSearchClient.
/// Accessed via reflection so the production class needs no visibility change.
/// </summary>
public class GenerateDigitVariantsTests
{
    private static IReadOnlyList<string> Variants(string? hashtag)
    {
        var method = typeof(TwitterApiIoSearchClient)
            .GetMethod("GenerateDigitVariants", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("GenerateDigitVariants not found");

        return (IReadOnlyList<string>)method.Invoke(null, new object?[] { hashtag })!;
    }

    // TC-1: ASCII digit 5 → two variants containing 5 and ٥
    [Fact]
    public void AsciiDigit_ProducesBothVariants()
    {
        var result = Variants("#تحدي_الالقاء_للاطفال5");

        Assert.Equal(2, result.Count);
        Assert.Contains("#تحدي_الالقاء_للاطفال5", result, StringComparer.Ordinal);
        Assert.Contains("#تحدي_الالقاء_للاطفال٥", result, StringComparer.Ordinal);
    }

    // TC-2: Arabic-Indic digit ٥ → two variants containing 5 and ٥
    [Fact]
    public void ArabicIndicDigit_ProducesBothVariants()
    {
        var result = Variants("#تحدي_الالقاء_للاطفال٥");

        Assert.Equal(2, result.Count);
        Assert.Contains("#تحدي_الالقاء_للاطفال5", result, StringComparer.Ordinal);
        Assert.Contains("#تحدي_الالقاء_للاطفال٥", result, StringComparer.Ordinal);
    }

    // TC-3: Hashtag with no digits → single variant
    [Fact]
    public void NoDigits_ProducesSingleVariant()
    {
        var result = Variants("#تحدي_الالقاء_للاطفال");

        Assert.Single(result);
        Assert.Equal("#تحدي_الالقاء_للاطفال", result[0]);
    }

    // TC-4: Hashtag without # prefix → # is added, variants are still produced
    [Fact]
    public void NoPrefixHashtag_HashIsAddedAndVariantsProduced()
    {
        var result = Variants("تحدي5");

        Assert.Equal(2, result.Count);
        Assert.All(result, v => Assert.StartsWith("#", v));
        Assert.Contains("#تحدي5", result);
        Assert.Contains("#تحدي٥", result);
    }

    // TC-5: null / empty → empty list
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrEmpty_ReturnsEmpty(string? input)
    {
        var result = Variants(input);

        Assert.Empty(result);
    }

    // TC-6: Mixed digits — all ASCII positions become Arabic and vice-versa
    [Fact]
    public void MultipleDigits_AllPositionsConverted()
    {
        var result = Variants("#test123");

        Assert.Equal(2, result.Count);
        Assert.Contains("#test123",  result);
        Assert.Contains("#test١٢٣", result);
    }

    // TC-7: Digit that is already in both forms — dedup produces correct pair
    [Fact]
    public void AsciiAndArabicVariantsAreDistinct()
    {
        var resultFromAscii  = Variants("#abc5xyz");
        var resultFromArabic = Variants("#abc٥xyz");

        // Both inputs must produce the same two-element set
        Assert.Equal(resultFromAscii.OrderBy(x => x), resultFromArabic.OrderBy(x => x));
    }
}
