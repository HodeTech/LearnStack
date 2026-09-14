using Microsoft.EntityFrameworkCore;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class EducationStructureTests
{
    private sealed class ClrRootProbe
    {
        public int Id { get; set; }
        public string SlugKey { get; set; } = "";
        public string Sluggish { get; set; } = "";
        public string DescriptionEn { get; set; } = "";
        public string SeoDescription { get; set; } = "";
        public string CourseSlug { get; set; } = "";
        public string SeoSlug { get; set; } = "";
        public string CourseLocale { get; set; } = "";
        public string LocalizedLocale { get; set; } = "";
    }

    private sealed class MappedRootProbe
    {
        public int Id { get; set; }
        public string SlugKey { get; set; } = "";
    }

    private sealed class PatternProbeContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseNpgsql("Host=model-only;Database=model-only;Username=model-only");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ClrRootProbe>(builder =>
            {
                builder.Property(root => root.SlugKey).HasColumnName("slug_key");
                builder.Property(root => root.Sluggish).HasColumnName("sluggish");
                // Ignore the offenders in EF so only the CLR leg can detect them.
                builder.Ignore(root => root.DescriptionEn);
                builder.Ignore(root => root.SeoDescription);
                builder.Ignore(root => root.CourseSlug);
                builder.Ignore(root => root.SeoSlug);
                builder.Ignore(root => root.CourseLocale);
                builder.Ignore(root => root.LocalizedLocale);
            });
            modelBuilder.Entity<MappedRootProbe>(builder =>
            {
                builder.Property(root => root.SlugKey).HasColumnName("slug_key");
                // Innocuous shadow-property names force the mapped-column leg to
                // detect each violation; the CLR has no localized property.
                builder.Property<string>("Projection1").HasColumnName("description_en");
                builder.Property<string>("Projection2").HasColumnName("seo_description");
                builder.Property<string>("Projection3").HasColumnName("course_slug");
                builder.Property<string>("Projection4").HasColumnName("seo_slug");
                builder.Property<string>("Projection5").HasColumnName("course_locale");
                builder.Property<string>("Projection6").HasColumnName("localized_locale");
            });
        }
    }
}
