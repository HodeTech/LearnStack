using FluentAssertions;
using LearnStack.Infrastructure.DataProtection;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using Xunit.Abstractions;

namespace LearnStack.Tests.Architecture;

public sealed class DataProtectionConventionTests(ITestOutputHelper output)
{
    private const string ModelConnection = "Host=model-only;Database=model-only;Username=model-only";

    /// <summary>ADR-0073: the complete actual production-model population requires explicit classification.</summary>
    [Fact]
    public void Mapped_Properties_Have_Explicit_Personal_Data_Classification()
    {
        var discovered = ProductionAssemblies.All().SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && !type.ContainsGenericParameters && typeof(DbContext).IsAssignableFrom(type))
            .ToArray();
        var covered = new List<Type>();
        var properties = new List<IReadOnlyProperty>();
        foreach (var create in ModelFactories())
        {
            using var context = create();
            covered.Add(context.GetType());
            var model = context.GetService<IDesignTimeModel>().Model;
            if (context is PlatformDbContext)
            {
                model.GetEntityTypes().Should().BeEmpty(
                    "Platform is deliberately migration-only; SQL-only stores retain their separate inventory");
                continue;
            }

            var mapped = model.GetEntityTypes().SelectMany(entity => entity.GetProperties()).ToArray();
            mapped.Should().NotBeEmpty("an empty module model cannot qualify the classification census");
            output.WriteLine("{0}: {1} entities, {2} scalar properties.",
                context.GetType().Name, model.GetEntityTypes().Count(), mapped.Length);
            properties.AddRange(mapped);
            model.FindAnnotation(PersonalDataClassificationExtensions.ValidatedModelAnnotation)?.Value.Should().Be(true,
                "Fix: opt every production mapped context into the finalizing convention");
            PersonalDataModelCensus.Inspect(model).Should().BeEmpty(
                "Fix: explicitly declare each mapped property, including inherited and shadow properties");
        }

        covered.Should().BeEquivalentTo(discovered,
            "Fix: add every new production DbContext to the census; no module or non-module context may disappear");
        covered.Count.Should().BeGreaterThan(4, "the sweep includes every mapped module and the empty Platform context");
        properties.Should().Contain(property => property.Name == "CreatedBy",
            "inherited actor references are part of the real mapped population");
        properties.Should().Contain(property => property.Name == "DeletedBy");
        output.WriteLine("Complete census: {0} contexts, {1} mapped scalar properties.", covered.Count, properties.Count);
    }

    [Fact]
    public void Production_model_refuses_a_neutral_property_added_after_normal_customization()
    {
        using var context = ModifiedTenancy<MissingPropertyCustomizer>();
        var build = () => context.Model;
        build.Should().Throw<InvalidOperationException>().WithMessage(PersonalDataModelCensus.FailureMessage);
    }

    [Fact]
    public void Production_model_accepts_the_same_neutral_property_with_an_explicit_declaration()
    {
        using var context = ModifiedTenancy<DeclaredPropertyCustomizer>();
        var model = context.GetService<IDesignTimeModel>().Model;
        var property = model.FindEntityType(typeof(Tenant))!.FindProperty("NeutralPayload")!;
        property.IsShadowProperty().Should().BeTrue("the control reaches a real added mapping rather than a CLR-name scan");
        property.FindAnnotation(PersonalDataClassificationExtensions.CategoryAnnotation)!.Value
            .Should().Be(PersonalDataCategory.Identity);
        PersonalDataModelCensus.Inspect(model).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, "A purpose does not make the default category valid.")]
    [InlineData(7, "Unknown category.")]
    [InlineData(1, "")]
    [InlineData(1, "   ")]
    public void Invalid_category_or_blank_purpose_is_refused(int category, string purpose)
    {
        var entity = ProbeModel().Entity<Probe>();
        var declare = () => entity.Property(probe => probe.NeutralPayload)
            .HasPersonalData((PersonalDataCategory)category, purpose);
        declare.Should().Throw<InvalidOperationException>().WithMessage(PersonalDataModelCensus.FailureMessage);
    }

    [Fact]
    public void Purpose_is_bounded_and_conflicting_declarations_cannot_overwrite_a_review()
    {
        var property = ProbeModel().Entity<Probe>().Property(probe => probe.NeutralPayload);
        var purpose = new string('a', PersonalDataClassificationExtensions.MaximumPurposeLength);
        property.HasPersonalData(PersonalDataCategory.Identity, purpose);
        property.HasPersonalData(PersonalDataCategory.Identity, purpose);

        var conflictingCategory = () => property.HasPersonalData(PersonalDataCategory.None, purpose);
        var conflictingPurpose = () => property.HasPersonalData(PersonalDataCategory.Identity, "Different purpose.");
        var excessivePurpose = () => property.HasPersonalData(PersonalDataCategory.Identity, purpose + "a");
        conflictingCategory.Should().Throw<InvalidOperationException>().WithMessage(PersonalDataModelCensus.FailureMessage);
        conflictingPurpose.Should().Throw<InvalidOperationException>().WithMessage(PersonalDataModelCensus.FailureMessage);
        excessivePurpose.Should().Throw<InvalidOperationException>().WithMessage(PersonalDataModelCensus.FailureMessage);
        property.Metadata.FindAnnotation(PersonalDataClassificationExtensions.PurposeAnnotation)!.Value.Should().Be(purpose);
    }

    [Fact]
    public void Declaration_of_an_unmapped_name_does_not_create_a_property_or_partially_classify()
    {
        var entity = ProbeModel().Entity<Probe>();
        var declare = () => entity.DeclarePersonalData(PersonalDataCategory.None, "Fixture structural fields.",
            nameof(Probe.NeutralPayload), "UnmappedPayload");
        declare.Should().Throw<InvalidOperationException>().WithMessage(PersonalDataModelCensus.FailureMessage);
        entity.Metadata.FindProperty("UnmappedPayload").Should().BeNull();
        entity.Metadata.FindProperty(nameof(Probe.NeutralPayload))!
            .FindAnnotation(PersonalDataClassificationExtensions.CategoryAnnotation).Should().BeNull();
    }

    [Fact]
    public void Census_is_deterministic_and_reports_no_annotation_or_property_values()
    {
        const string canary = "private-person-value-canary";
        var builder = ProbeModel();
        var property = builder.Entity<Probe>().Property(probe => probe.NeutralPayload);
        property.HasDefaultValue(canary);
        property.Metadata.SetAnnotation(PersonalDataClassificationExtensions.CategoryAnnotation, new UnprintableValue());
        property.Metadata.SetAnnotation(PersonalDataClassificationExtensions.PurposeAnnotation,
            canary + new string('x', PersonalDataClassificationExtensions.MaximumPurposeLength));

        var issues = PersonalDataModelCensus.Inspect(builder.Model);
        issues.Select(issue => issue.Code).Should().Equal("invalid_category", "invalid_purpose");
        PersonalDataModelCensus.Inspect(builder.Model).Should().Equal(issues);
        string.Join("|", issues).Should().NotContain(canary);
        var declare = () => property.HasPersonalData(PersonalDataCategory.Identity, canary + new string('x', 256));
        declare.Should().Throw<InvalidOperationException>().WithMessage(PersonalDataModelCensus.FailureMessage)
            .Which.ToString().Should().NotContain(canary);
    }

    [Fact]
    public void Classification_annotations_introduce_no_relational_schema_changes()
    {
        foreach (var create in Modules.Mapped.Select(module => module.Context))
        {
            using var context = create();
            var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
            snapshot.Should().NotBeNull("each scoped module has its existing migration baseline");
            var previous = context.GetService<IModelRuntimeInitializer>().Initialize(snapshot!.Model, designTime: true);
            var current = context.GetService<IDesignTimeModel>().Model;
            context.GetService<IMigrationsModelDiffer>()
                .GetDifferences(previous.GetRelationalModel(), current.GetRelationalModel())
                .Should().BeEmpty("classification is metadata only, and must not change the existing schema");
        }
    }

    private static IEnumerable<Func<DbContext>> ModelFactories() =>
        Modules.Mapped.Select(module => module.Context).Append(() => new PlatformDbContext(
            new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ModelConnection).Options));

    private static TenancyDbContext ModifiedTenancy<TCustomizer>() where TCustomizer : class, IModelCustomizer =>
        new(new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(ModelConnection)
            .ReplaceService<IModelCustomizer, TCustomizer>().Options, StaticTenantContextAccessor.Unresolved);

    private static ModelBuilder ProbeModel()
    {
        var builder = new ModelBuilder();
        builder.Entity<Probe>().Property(probe => probe.Id)
            .HasPersonalData(PersonalDataCategory.None, "Fixture structural key.");
        builder.Entity<Probe>().Property(probe => probe.NeutralPayload);
        return builder;
    }

    public sealed class MissingPropertyCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<Tenant>().Property<string>("NeutralPayload");
        }
    }

    public sealed class DeclaredPropertyCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<Tenant>().Property<string>("NeutralPayload")
                .HasPersonalData(PersonalDataCategory.Identity, "Fixture demonstrates explicit classification of a neutral name.");
        }
    }

    private sealed class Probe
    {
        public int Id { get; set; }
        public string? NeutralPayload { get; set; }
    }

    private sealed class UnprintableValue
    {
        public override string ToString() => throw new InvalidOperationException("Metadata values must never be rendered.");
    }
}
