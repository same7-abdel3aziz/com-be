namespace CompetitionManagementSystem.Models;

/// <summary>
/// Dictionary of Arabic first names → gender. Populated at startup from
/// Data/Seed/Arabic_names.csv. NormalizedName is the key used for lookups.
/// Names that appear as both Male AND Female in the source after normalization
/// are stored with Gender = "Ambiguous" so the inference service never guesses.
/// </summary>
public sealed class GenderNameDictionary
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;   // Male | Female | Unknown | Ambiguous
    public string Source { get; set; } = "Arabic_names.csv";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public static class GenderValues
{
    public const string Male = "Male";
    public const string Female = "Female";
    public const string Unknown = "Unknown";
    public const string Ambiguous = "Ambiguous";

    public const string MaleArabic = "ذكر";
    public const string FemaleArabic = "أنثى";
    public const string UnknownArabic = "غير معروف";

    public static string ToArabic(string gender) => gender switch
    {
        Male => MaleArabic,
        Female => FemaleArabic,
        _ => UnknownArabic,    // Ambiguous and Unknown both render as "غير معروف" in the report
    };
}
