using FluentAssertions;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>ADR-0095 and Standards 05: classification must be explicit, including None.</summary>
public sealed class CustomFieldDefinitionConventionTests
{
    [Fact]
    public void Every_TenantCustomFieldDef_Declares_PiiCategory()
    {
        using var db = new CustomizationDbContext(new DbContextOptionsBuilder<CustomizationDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only;Username=model-only").Options,
            StaticTenantContextAccessor.Unresolved);
        var entity = db.Model.FindEntityType(typeof(TenantCustomFieldDef));
        entity.Should().NotBeNull("Fix: map the delivered definition aggregate in its owning context.");
        IsExplicitClassification(entity!).Should().BeTrue(
            "Fix: persist a nonnullable PersonalDataCategory without a database default; None requires an explicit choice.");
        var property = typeof(TenantCustomFieldDef).GetProperty(nameof(TenantCustomFieldDef.PiiCategory))!;
        property.SetMethod!.IsPrivate.Should().BeTrue("classification is changed only through the aggregate's Draft invariant");
        var migrations = db.GetService<IMigrationsAssembly>();
        var created = migrations.Migrations.Values.Select(type => migrations.CreateMigration(type, db.Database.ProviderName!))
            .SelectMany(migration => migration.UpOperations).OfType<CreateTableOperation>()
            .Single(table => table.Name == "tenant_custom_field_defs");
        var column = created.Columns.Single(candidate => candidate.Name == "pii_category");
        column.IsNullable.Should().BeFalse("Fix: deploy classification as NOT NULL, not just an EF assertion.");
        column.DefaultValue.Should().BeNull("an omitted category cannot silently become None");
        column.DefaultValueSql.Should().BeNull();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("nullable")]
    [InlineData("default-none")]
    [InlineData("default-zero")]
    [InlineData("wrong-type")]
    public void The_category_detector_rejects_missing_nullable_defaulted_and_untyped_classification(string defect)
    {
        var model = new ModelBuilder();
        var entity = model.SharedTypeEntity<Dictionary<string, object>>("classification-control");
        entity.IndexerProperty<int>("Id");
        entity.HasKey("Id");
        if (defect == "nullable") entity.IndexerProperty<PersonalDataCategory?>("PiiCategory");
        else if (defect == "wrong-type") entity.IndexerProperty<string>("PiiCategory").IsRequired();
        else if (defect != "missing")
            entity.IndexerProperty<PersonalDataCategory>("PiiCategory")
                .HasDefaultValue(defect == "default-none" ? PersonalDataCategory.None : (PersonalDataCategory)0);
        IsExplicitClassification(entity.Metadata).Should().BeFalse();

        var clean = model.SharedTypeEntity<Dictionary<string, object>>("explicit-none-control");
        clean.IndexerProperty<int>("Id");
        clean.HasKey("Id");
        clean.IndexerProperty<PersonalDataCategory>("PiiCategory").IsRequired();
        IsExplicitClassification(clean.Metadata).Should().BeTrue("the same detector must accept an explicit category without defaults");
    }

    private static bool IsExplicitClassification(IReadOnlyEntityType entity) =>
        entity.FindProperty("PiiCategory") is { IsNullable: false, ClrType: var type } category
        && type == typeof(PersonalDataCategory)
        && category.FindAnnotation(RelationalAnnotationNames.DefaultValue) is null
        && category.FindAnnotation(RelationalAnnotationNames.DefaultValueSql) is null
        && category.FindAnnotation(RelationalAnnotationNames.ComputedColumnSql) is null;
}
