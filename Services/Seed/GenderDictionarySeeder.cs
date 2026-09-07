using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Models;
using CompetitionManagementSystem.Services.Reporting;
using Microsoft.EntityFrameworkCore;

namespace CompetitionManagementSystem.Services.Seed;

/// <summary>
/// Idempotent file-based importer for the gender name dictionary.
/// Reads <c>Data/Seed/Arabic_names.csv</c> AND <c>Data/Seed/Latin_names.csv</c>
/// (relative to ContentRoot) and upserts rows into <see cref="GenderNameDictionary"/>.
/// Rows missing from the CSVs are NOT deleted. Same normalized name appearing as both
/// M and F → stored as Ambiguous.
/// </summary>
public static class GenderDictionarySeeder
{
    private const string ArabicRelativePath = "Data/Seed/Arabic_names.csv";
    private const string LatinRelativePath = "Data/Seed/Latin_names.csv";

    public static async Task SeedAsync(
        ApplicationDbContext db,
        IHostEnvironment env,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var arabicTarget = ParseFile(
            Path.Combine(env.ContentRootPath, ArabicRelativePath),
            ArabicRelativePath,
            isArabic: true,
            logger,
            out var arabicStats);

        var latinTarget = ParseFile(
            Path.Combine(env.ContentRootPath, LatinRelativePath),
            LatinRelativePath,
            isArabic: false,
            logger,
            out var latinStats);

        // Merge into a single target map keyed by NormalizedName. Conflicts across the two
        // files (same normalized key produced by both) → mark Ambiguous.
        var target = new Dictionary<string, TargetEntry>(StringComparer.Ordinal);
        MergeInto(target, arabicTarget, "Arabic_names.csv");
        MergeInto(target, latinTarget, "Latin_names.csv");

        if (target.Count == 0)
        {
            logger.LogWarning("GenderDictionarySeeder: produced zero usable entries; nothing imported.");
            return;
        }

        var existing = await db.GenderNameDictionary.ToListAsync(cancellationToken);
        var byKey = existing.ToDictionary(r => r.NormalizedName, StringComparer.Ordinal);

        var inserts = 0;
        var updates = 0;
        var unchanged = 0;
        var now = DateTime.UtcNow;

        foreach (var (normalized, entry) in target)
        {
            if (byKey.TryGetValue(normalized, out var row))
            {
                var changed = false;
                if (row.Gender != entry.Gender) { row.Gender = entry.Gender; changed = true; }
                if (row.Name != entry.Name) { row.Name = entry.Name; changed = true; }
                if (row.Source != entry.Source) { row.Source = entry.Source; changed = true; }
                if (!row.IsActive) { row.IsActive = true; changed = true; }

                if (changed)
                {
                    row.UpdatedAtUtc = now;
                    updates++;
                }
                else
                {
                    unchanged++;
                }
            }
            else
            {
                db.GenderNameDictionary.Add(new GenderNameDictionary
                {
                    Name = entry.Name,
                    NormalizedName = normalized,
                    Gender = entry.Gender,
                    Source = entry.Source,
                    IsActive = true,
                    CreatedAtUtc = now
                });
                inserts++;
            }
        }

        if (inserts > 0 || updates > 0)
            await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "GenderDictionarySeeder: arabic(src={ASrc} valid={AValid} invalid={AInvalid} ambig={AAmbig}) latin(src={LSrc} valid={LValid} invalid={LInvalid} ambig={LAmbig}) | dict inserts={Ins} updates={Upd} unchanged={Same}",
            arabicStats.TotalSourceLines, arabicStats.ValidRows, arabicStats.InvalidRows, arabicStats.AmbiguousCount,
            latinStats.TotalSourceLines, latinStats.ValidRows, latinStats.InvalidRows, latinStats.AmbiguousCount,
            inserts, updates, unchanged);
    }

    public sealed record SeedStats(int TotalSourceLines, int ValidRows, int InvalidRows, int AmbiguousCount);

    private sealed record TargetEntry(string Name, string Gender, string Source);

    private static Dictionary<string, TargetEntry> ParseFile(
        string csvPath,
        string sourceTag,
        bool isArabic,
        ILogger logger,
        out SeedStats stats)
    {
        var target = new Dictionary<string, TargetEntry>(StringComparer.Ordinal);
        stats = new SeedStats(0, 0, 0, 0);

        if (!File.Exists(csvPath))
        {
            logger.LogInformation("GenderDictionarySeeder: {Source} not present at {Path}; skipping.", sourceTag, csvPath);
            return target;
        }

        var lines = File.ReadAllLines(csvPath, new System.Text.UTF8Encoding(false));
        if (lines.Length == 0 ||
            !string.Equals(lines[0].Trim(), "names,sex", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("GenderDictionarySeeder: unexpected header in {Source} (expected 'names,sex'); skipping.", sourceTag);
            return target;
        }

        var observed = new Dictionary<string, (HashSet<string> Sex, string RawName)>(StringComparer.Ordinal);
        var valid = 0;
        var invalid = 0;

        for (var i = 1; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var parts = raw.Split(',', 2);
            if (parts.Length != 2) { invalid++; continue; }

            var name = parts[0].Trim();
            var sex = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(name)) { invalid++; continue; }
            if (sex != "M" && sex != "F") { invalid++; continue; }

            var key = isArabic ? ArabicNameNormalizer.Normalize(name) : LatinNameNormalizer.Normalize(name);
            if (key.Length == 0) { invalid++; continue; }

            if (!observed.TryGetValue(key, out var slot))
            {
                slot = (new HashSet<string>(StringComparer.Ordinal), name);
                observed[key] = slot;
            }
            slot.Sex.Add(sex);
            valid++;
        }

        var ambiguous = 0;
        foreach (var (key, slot) in observed)
        {
            string gender;
            if (slot.Sex.Count > 1)
            {
                gender = GenderValues.Ambiguous;
                ambiguous++;
            }
            else
            {
                gender = slot.Sex.Contains("M") ? GenderValues.Male : GenderValues.Female;
            }
            target[key] = new TargetEntry(slot.RawName, gender, sourceTag);
        }

        stats = new SeedStats(lines.Length, valid, invalid, ambiguous);
        return target;
    }

    private static void MergeInto(
        Dictionary<string, TargetEntry> target,
        Dictionary<string, TargetEntry> source,
        string sourceTag)
    {
        foreach (var (key, entry) in source)
        {
            if (!target.TryGetValue(key, out var existing))
            {
                target[key] = entry;
                continue;
            }

            // Already exists from another source. If they agree, keep; if not, mark Ambiguous.
            if (existing.Gender == entry.Gender)
                continue;

            if (existing.Gender == GenderValues.Ambiguous || entry.Gender == GenderValues.Ambiguous)
            {
                target[key] = existing with { Gender = GenderValues.Ambiguous };
            }
            else
            {
                target[key] = existing with { Gender = GenderValues.Ambiguous };
            }
        }
    }
}
