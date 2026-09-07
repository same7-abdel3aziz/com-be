using CompetitionManagementSystem.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CompetitionManagementSystem.Data;

public sealed class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Competition> Competitions => Set<Competition>();
    public DbSet<Participation> Participations => Set<Participation>();
    public DbSet<ParticipationScore> ParticipationScores => Set<ParticipationScore>();
    public DbSet<ParticipationExternalUrl> ParticipationExternalUrls => Set<ParticipationExternalUrl>();
    public DbSet<IngestionRun> IngestionRuns => Set<IngestionRun>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Winner> Winners => Set<Winner>();
    public DbSet<AdminAllowedIpRange> AdminAllowedIpRanges => Set<AdminAllowedIpRange>();
    public DbSet<AdminSecuritySetting> AdminSecuritySettings => Set<AdminSecuritySetting>();
    public DbSet<GenderNameDictionary> GenderNameDictionary => Set<GenderNameDictionary>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<GenderNameDictionary>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            // Binary collation so distinct C# normalized strings (Ordinal) remain distinct in SQL —
            // SQL_Latin1_General_CP1_CI_AS would treat some Arabic forms as equal and fail
            // the unique index on names that our normalization considers different.
            entity.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired().UseCollation("Latin1_General_BIN2");
            entity.Property(x => x.Gender).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Source).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.NormalizedName).IsUnique();
            entity.HasIndex(x => new { x.Gender, x.IsActive });
        });

        builder.Entity<AdminAllowedIpRange>(entity =>
        {
            entity.Property(x => x.IpRange).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(200);
            entity.Property(x => x.Notes).HasMaxLength(1000);
            entity.Property(x => x.CreatedByUserId).HasMaxLength(450);
            entity.Property(x => x.UpdatedByUserId).HasMaxLength(450);
            entity.HasIndex(x => x.IpRange).IsUnique();
        });

        builder.Entity<AdminSecuritySetting>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.UpdatedByUserId).HasMaxLength(450);
            // Seed the singleton row so PUT /api/admin/security/settings always has a target.
            entity.HasData(new AdminSecuritySetting
            {
                Id = 1,
                EnableAdminIpRestriction = false,
                RequireMfaForAdmins = false,
                UpdatedAtUtc = null,
                UpdatedByUserId = null
            });
        });

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(x => x.TotpSecretEncrypted).HasColumnType("nvarchar(max)");
            entity.Property(x => x.TotpRecoveryCodesEncrypted).HasColumnType("nvarchar(max)");
        });

        builder.Entity<Competition>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Hashtag).HasMaxLength(120).IsRequired();
            entity.Property(x => x.SecondaryHashtag).HasMaxLength(120);
            entity.Property(x => x.Description).HasMaxLength(1000);
            entity.HasIndex(x => new { x.IsActive, x.Hashtag });
        });

        builder.Entity<Participation>(entity =>
        {
            entity.Property(x => x.Platform).HasMaxLength(20).IsRequired();
            entity.Property(x => x.ExternalPostId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ExternalPostUrl).HasMaxLength(1000);
            entity.Property(x => x.Text).HasMaxLength(4000);
            entity.Property(x => x.AuthorUserName).HasMaxLength(200);
            entity.Property(x => x.AuthorDisplayName).HasMaxLength(300);
            entity.Property(x => x.AuthorDescription).HasMaxLength(1000);
            entity.Property(x => x.AuthorLocation).HasMaxLength(500);
            entity.Property(x => x.InReplyToId).HasMaxLength(100);
            entity.Property(x => x.RawJsonData).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
            entity.Property(x => x.ExclusionReason).HasMaxLength(1000);
            entity.Property(x => x.ApprovedByUserId).HasMaxLength(450);
            entity.Property(x => x.ReservedByJudgeUserId).HasMaxLength(450);
            entity.Property(x => x.ScoredByJudgeUserId).HasMaxLength(450);
            entity.Property(x => x.CachedMediaUrlsJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.CachedPostSnapshotJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.TieSelectionReason).HasMaxLength(1000);
            entity.Property(x => x.TieSelectedByUserId).HasMaxLength(450);
            entity.Property(x => x.ScoreSum).HasColumnType("decimal(18,2)");
            entity.Property(x => x.FinalScore).HasColumnType("decimal(18,2)");

            entity.HasIndex(x => new { x.CompetitionId, x.Platform, x.ExternalPostId }).IsUnique();
            entity.HasIndex(x => new { x.CompetitionId, x.IsActive, x.FinalScore });
            entity.HasIndex(x => new { x.CompetitionId, x.Status });
            entity.HasIndex(x => new { x.CompetitionId, x.ReservedByJudgeUserId });
            entity.HasIndex(x => new { x.CompetitionId, x.ScoredByJudgeUserId });
            entity.HasIndex(x => new { x.CompetitionId, x.ImportedAtUtc });
            entity.HasIndex(x => x.AuthorUserName);
            entity.HasOne(x => x.Competition)
                .WithMany(x => x.Participations)
                .HasForeignKey(x => x.CompetitionId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<ParticipationScore>(entity =>
        {
            entity.Property(x => x.JudgeUserId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.Score).HasColumnType("decimal(5,2)");
            entity.Property(x => x.Notes).HasMaxLength(1000);
            entity.Property(x => x.Justification).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.IpAddress).HasMaxLength(100);
            entity.HasIndex(x => new { x.CompetitionId, x.ParticipationId }).IsUnique();
            entity.HasIndex(x => new { x.CompetitionId, x.CreatedAtUtc });

            entity.HasOne(x => x.Competition)
                .WithMany()
                .HasForeignKey(x => x.CompetitionId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(x => x.Participation)
                .WithMany(x => x.Scores)
                .HasForeignKey(x => x.ParticipationId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(x => x.JudgeUser)
                .WithMany()
                .HasForeignKey(x => x.JudgeUserId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<ParticipationExternalUrl>(entity =>
        {
            entity.Property(x => x.Url).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.ExpandedUrl).HasMaxLength(1000);
            entity.Property(x => x.DisplayUrl).HasMaxLength(1000);
            entity.Property(x => x.RawJsonData).HasColumnType("nvarchar(max)");

            entity.HasOne(x => x.Participation)
                .WithMany(x => x.ExternalUrls)
                .HasForeignKey(x => x.ParticipationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<IngestionRun>(entity =>
        {
            entity.Property(x => x.ErrorMessage).HasMaxLength(4000);
            entity.Property(x => x.Status).HasMaxLength(40);
            entity.HasIndex(x => new { x.CompetitionId, x.StartedAtUtc });
            entity.HasOne(x => x.Competition)
                .WithMany()
                .HasForeignKey(x => x.CompetitionId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<AuditLog>(entity =>
        {
            entity.Property(x => x.EntityType).HasMaxLength(120).IsRequired();
            entity.Property(x => x.EntityId).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(160).IsRequired();
            entity.Property(x => x.OldValueJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.NewValueJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.UserId).HasMaxLength(450);
            entity.Property(x => x.UserEmail).HasMaxLength(256);
            entity.Property(x => x.UserRole).HasMaxLength(120);
            entity.Property(x => x.IpAddress).HasMaxLength(100);
            entity.HasIndex(x => new { x.EntityType, x.EntityId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.Action, x.CreatedAtUtc });
        });
    }
}
