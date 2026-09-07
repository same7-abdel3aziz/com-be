using System.Net;
using System.Text;
using CompetitionManagementSystem.Data;
using CompetitionManagementSystem.Dtos.Reports;
using Microsoft.EntityFrameworkCore;

namespace CompetitionManagementSystem.Services.Reporting;

public sealed class ReportService : IReportService
{
    private readonly ApplicationDbContext _db;
    private readonly IGenderInferenceService _gender;

    public ReportService(ApplicationDbContext db, IGenderInferenceService gender)
    {
        _db = db;
        _gender = gender;
    }

    public async Task<DashboardResponse> GetDashboardAsync(Guid? competitionId, CancellationToken cancellationToken)
    {
        var participationQuery = _db.Participations.AsNoTracking().Where(p => p.IsActive);
        var scoreQuery = _db.ParticipationScores.AsNoTracking();

        if (competitionId.HasValue)
        {
            participationQuery = participationQuery.Where(p => p.CompetitionId == competitionId);
            scoreQuery = scoreQuery.Where(s => s.CompetitionId == competitionId);
        }

        var today = DateTime.UtcNow.Date;

        var topCompetitionsRaw = await _db.Competitions
            .AsNoTracking()
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Hashtag,
                ParticipationsCount = _db.Participations.Count(p => p.CompetitionId == c.Id && p.IsActive),
                ScoresCount = _db.ParticipationScores.Count(s => s.CompetitionId == c.Id)
            })
            .OrderByDescending(x => x.ParticipationsCount)
            .ThenByDescending(x => x.ScoresCount)
            .Take(5)
            .ToListAsync(cancellationToken);

        var topCompetitions = topCompetitionsRaw
            .Select(x => new TopCompetitionActivityDto(
                x.Id,
                x.Name,
                x.Hashtag,
                x.ParticipationsCount,
                x.ScoresCount))
            .ToList();

        var highestScores = await participationQuery
            .Where(p => p.ScoreCount > 0)
            .OrderByDescending(p => p.FinalScore)
            .ThenByDescending(p => p.ScoreCount)
            .ThenByDescending(p => p.ViewCount)
            .Take(20)
            .Select(p => new HighestScoreDto(
                p.Id,
                p.AuthorUserName,
                p.Text,
                p.ExternalPostUrl,
                p.FinalScore,
                p.ScoreCount))
            .ToListAsync(cancellationToken);

        var topInteractors = await participationQuery
            .OrderByDescending(p => p.RetweetCount + p.ReplyCount + p.QuoteCount + p.LikeCount)
            .Take(20)
            .Select(p => new TopInteractorDto(
                p.AuthorUserName,
                p.AuthorDisplayName,
                p.AuthorFollowers,
                p.AuthorIsBlueVerified,
                p.RetweetCount + p.ReplyCount + p.QuoteCount + p.LikeCount))
            .ToListAsync(cancellationToken);

        var latest = await participationQuery
            .OrderByDescending(p => p.ImportedAtUtc)
            .Take(20)
            .Select(p => new LatestParticipationDto(
                p.Id,
                p.AuthorUserName,
                p.Text,
                p.ExternalPostUrl,
                p.ImportedAtUtc))
            .ToListAsync(cancellationToken);

        return new DashboardResponse(
            ActiveCompetitions: await _db.Competitions.CountAsync(c => c.IsActive, cancellationToken),
            TotalParticipations: await participationQuery.CountAsync(cancellationToken),
            NewParticipationsToday: await participationQuery.CountAsync(p => p.ImportedAtUtc >= today, cancellationToken),
            CompletedScores: await scoreQuery.CountAsync(cancellationToken),
            TopActiveCompetitions: topCompetitions,
            HighestScores: highestScores,
            TopInteractors: topInteractors,
            LatestParticipations: latest);
    }

    public async Task<byte[]> ExportParticipationsCsvAsync(Guid competitionId, CancellationToken cancellationToken)
    {
        var rows = await _db.Participations.AsNoTracking()
            .Where(p => p.CompetitionId == competitionId && p.IsActive)
            .OrderByDescending(p => p.ImportedAtUtc)
            .ToListAsync(cancellationToken);

        // Load the gender dictionary once per export — never per row.
        var genderMap = await _gender.LoadDictionaryAsync(cancellationToken);

        var sb = CreateHtmlTableStart("تقرير المشاركات");

        AddHeader(sb, [
            "معرّف المشاركة",
            "معرّف المنشور الخارجي",
            "رابط المنشور",
            "نص المشاركة",
            "تاريخ النشر على المنصة",
            "تاريخ الاستيراد",
            "اسم المستخدم",
            "اسم العرض",
            "النوع",
            "الدولة",
            "عدد المتابعين",
            "موثق",
            "الإعجابات",
            "إعادة النشر",
            "الردود",
            "الاقتباسات",
            "المشاهدات",
            "الحالة",
            "سبب الاستبعاد",
            "يحتوي على فيديو",
            "يحتوي على الوسم الأساسي",
            "يحتوي على الوسم الثانوي",
            "الدرجة النهائية",
            "عدد التقييمات"
        ]);

        foreach (var p in rows)
        {
            AddRow(sb, [
                p.Id.ToString(),
                p.ExternalPostId,
                p.ExternalPostUrl,
                p.Text,
                p.CreatedAtOnPlatformUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                p.ImportedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                p.AuthorUserName,
                p.AuthorDisplayName,
                _gender.Infer(p.AuthorDisplayName, p.AuthorUserName, genderMap).GenderArabic,
                p.AuthorLocation ?? "",
                p.AuthorFollowers.ToString(),
                p.AuthorIsBlueVerified ? "نعم" : "لا",
                p.LikeCount.ToString(),
                p.RetweetCount.ToString(),
                p.ReplyCount.ToString(),
                p.QuoteCount.ToString(),
                p.ViewCount.ToString(),
                TranslateStatus(p.Status.ToString()),
                p.ExclusionReason ?? "",
                p.HasVideo ? "نعم" : "لا",
                p.HasPrimaryHashtag ? "نعم" : "لا",
                p.HasSecondaryHashtag ? "نعم" : "لا",
                p.FinalScore.ToString("0.##"),
                p.ScoreCount.ToString()
            ]);
        }

        FinishHtmlTable(sb);
        return ToUtf8BomBytes(sb.ToString());
    }

    public async Task<byte[]> ExportScoresCsvAsync(Guid competitionId, CancellationToken cancellationToken)
    {
        var rows = await _db.ParticipationScores.AsNoTracking()
            .Where(s => s.CompetitionId == competitionId)
            .Include(s => s.Participation)
            .Include(s => s.JudgeUser)
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var genderMap = await _gender.LoadDictionaryAsync(cancellationToken);

        var sb = CreateHtmlTableStart("تقرير التقييمات");

        AddHeader(sb, [
            "معرّف التقييم",
            "معرّف المشاركة",
            "رابط التغريدة",
            "اسم المستخدم",
            "اسم العرض",
            "النوع",
            "بريد المحكم",
            "الدرجة",
            "ملاحظات المحكم",
            "التبرير",
            "عنوان IP",
            "تاريخ التقييم"
        ]);

        foreach (var s in rows)
        {
            AddRow(sb, [
                s.Id.ToString(),
                s.ParticipationId.ToString(),
                s.Participation?.ExternalPostUrl ?? "",
                s.Participation?.AuthorUserName ?? "",
                s.Participation?.AuthorDisplayName ?? "",
                _gender.Infer(s.Participation?.AuthorDisplayName, s.Participation?.AuthorUserName, genderMap).GenderArabic,
                s.JudgeUser?.Email ?? "",
                s.Score.ToString("0.##"),
                s.Notes ?? "",
                s.Justification,
                s.IpAddress ?? "",
                s.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss")
            ]);
        }

        FinishHtmlTable(sb);
        return ToUtf8BomBytes(sb.ToString());
    }

    public async Task<byte[]> ExportRawJsonAsync(Guid competitionId, CancellationToken cancellationToken)
    {
        var rows = await _db.Participations.AsNoTracking()
            .Where(p => p.CompetitionId == competitionId)
            .Select(p => new
            {
                p.Id,
                p.Platform,
                p.ExternalPostId,
                p.RawJsonData
            })
            .ToListAsync(cancellationToken);

        var json = System.Text.Json.JsonSerializer.Serialize(
            rows,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        return ToUtf8BomBytes(json);
    }

    public async Task<byte[]> ExportTopScoresCsvAsync(Guid competitionId, int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 500);

        var rows = await _db.Participations.AsNoTracking()
            .Where(p => p.CompetitionId == competitionId && p.IsActive && p.ScoreCount > 0)
            .OrderByDescending(p => p.FinalScore)
            .ThenByDescending(p => p.ScoreCount)
            .ThenByDescending(p => p.ViewCount)
            .Take(take)
            .ToListAsync(cancellationToken);

        var genderMap = await _gender.LoadDictionaryAsync(cancellationToken);

        var sb = CreateHtmlTableStart("تقرير أعلى الدرجات");

        AddHeader(sb, [
            "الترتيب",
            "معرّف المشاركة",
            "اسم المستخدم",
            "اسم العرض",
            "النوع",
            "رابط المنشور",
            "الدرجة النهائية",
            "عدد التقييمات",
            "الإعجابات",
            "إعادة النشر",
            "الردود",
            "الاقتباسات",
            "المشاهدات"
        ]);

        var rank = 1;

        foreach (var p in rows)
        {
            AddRow(sb, [
                rank++.ToString(),
                p.Id.ToString(),
                p.AuthorUserName,
                p.AuthorDisplayName,
                _gender.Infer(p.AuthorDisplayName, p.AuthorUserName, genderMap).GenderArabic,
                p.ExternalPostUrl,
                p.FinalScore.ToString("0.##"),
                p.ScoreCount.ToString(),
                p.LikeCount.ToString(),
                p.RetweetCount.ToString(),
                p.ReplyCount.ToString(),
                p.QuoteCount.ToString(),
                p.ViewCount.ToString()
            ]);
        }

        FinishHtmlTable(sb);
        return ToUtf8BomBytes(sb.ToString());
    }

    private static StringBuilder CreateHtmlTableStart(string title)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"ar\" dir=\"rtl\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\" />");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: Tahoma, Arial, sans-serif; direction: rtl; }");
        sb.AppendLine("table { border-collapse: collapse; width: 100%; direction: rtl; }");
        sb.AppendLine("th { background: #D9EAF7; font-weight: bold; text-align: center; border: 1px solid #999; padding: 6px; white-space: nowrap; }");
        sb.AppendLine("td { border: 1px solid #999; padding: 6px; mso-number-format:'\\@'; vertical-align: top; }");
        sb.AppendLine("h2 { text-align: center; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine($"<h2>{Html(title)}</h2>");
        sb.AppendLine("<table>");

        return sb;
    }

    private static void AddHeader(StringBuilder sb, string[] headers)
    {
        sb.AppendLine("<tr>");
        foreach (var header in headers)
            sb.AppendLine($"<th>{Html(header)}</th>");
        sb.AppendLine("</tr>");
    }

    private static void AddRow(StringBuilder sb, string?[] values)
    {
        sb.AppendLine("<tr>");
        foreach (var value in values)
            sb.AppendLine($"<td>{Html(value)}</td>");
        sb.AppendLine("</tr>");
    }

    private static void FinishHtmlTable(StringBuilder sb)
    {
        sb.AppendLine("</table>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
    }

    private static string Html(string? value)
    {
        return WebUtility.HtmlEncode(value ?? string.Empty);
    }

    private static byte[] ToUtf8BomBytes(string value)
    {
        return Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(value))
            .ToArray();
    }

    private static string TranslateStatus(string? status)
    {
        return status switch
        {
            "PendingApproval" => "بانتظار الاعتماد",
            "ApprovedForJudging" => "معتمدة للتحكيم",
            "ReservedForJudging" => "محجوزة للتحكيم",
            "Scored" => "تم تقييمها",
            "AutoExcluded" => "مستبعدة تلقائيًا",
            "ManuallyExcluded" => "مستبعدة يدويًا",
            _ => status ?? ""
        };
    }
}