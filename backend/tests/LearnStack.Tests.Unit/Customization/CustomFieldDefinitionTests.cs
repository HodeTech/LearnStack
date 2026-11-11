using FluentAssertions;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Xunit;

namespace LearnStack.Tests.Unit.Customization;

public sealed class CustomFieldDefinitionTests
{
    private static readonly FixedClock Clock = new FixedClock();
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static TenantCustomFieldDef Draft(PersonalDataCategory category = PersonalDataCategory.None) => TenantCustomFieldDef.Create(
        TenantCustomFieldDefId.From(Guid.CreateVersion7()), TenantId.From(Guid.CreateVersion7()), "field", 1,
        "{}", "{\"en\":\"Label\"}", category, false, 0,
        category == PersonalDataCategory.Sensitive ? "purpose" : null, category == PersonalDataCategory.Sensitive ? "{\"en\":\"Notice\"}" : null,
        242, Clock, Actor);

    [Theory]
    [InlineData(PersonalDataCategory.None)]
    [InlineData(PersonalDataCategory.Identity)]
    [InlineData(PersonalDataCategory.Behaviour)]
    [InlineData(PersonalDataCategory.Sensitive)]
    public void An_explicit_supported_category_is_preserved(PersonalDataCategory category) => Draft(category).PiiCategory.Should().Be(category);

    [Theory]
    [InlineData((PersonalDataCategory)0)]
    [InlineData(PersonalDataCategory.Payment)]
    [InlineData(PersonalDataCategory.Audit)]
    [InlineData((PersonalDataCategory)99)]
    public void Missing_or_unsupported_classification_refuses(PersonalDataCategory category) =>
        ((Action)(() => Draft(category))).Should().Throw<ArgumentOutOfRangeException>();

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void Invalid_value_budget_identifies_the_budget_parameter_without_mutating_the_draft(int maximumValueBytes)
    {
        var row = Draft();
        var revise = () => row.ReviseDraft("{}", "{\"en\":\"Changed\"}", PersonalDataCategory.Identity,
            true, 2, null, null, maximumValueBytes, Clock, Actor);

        revise.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(maximumValueBytes));
        row.MaximumValueBytes.Should().Be(242);
        row.LabelsJson.Should().Be("{\"en\":\"Label\"}");
        row.Version.Should().Be(0);
    }

    [Fact]
    public void Publishing_freezes_all_authored_metadata_and_retirement_preserves_exact_history()
    {
        var row = Draft();
        row.Publish(Clock, Actor);
        var revise = () => row.ReviseDraft("{}", "{\"en\":\"Changed\"}", PersonalDataCategory.Identity, true, 2, null, null, 20, Clock, Actor);
        revise.Should().Throw<InvalidOperationException>();
        row.Retire(Clock, Actor);
        row.Status.Should().Be(CustomizationStatus.Deprecated);
        row.PiiCategory.Should().Be(PersonalDataCategory.None);
        row.LabelsJson.Should().Be("{\"en\":\"Label\"}");
        ((Action)(() => row.Publish(Clock, Actor))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Discard_keeps_revision_identity_and_reservation_but_cannot_be_published()
    {
        var row = Draft(); var id = row.Id;
        row.Discard(Clock, Actor);
        row.Id.Should().Be(id); row.SchemaVersion.Should().Be(1); row.Key.Should().Be("field");
        row.DeletedAt.Should().Be(Clock.UtcNow);
        ((Action)(() => row.Publish(Clock, Actor))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Real_model_keeps_deleted_reservations_in_the_scoped_query()
    {
        using var db = new CustomizationDbContext(new DbContextOptionsBuilder<CustomizationDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only;Username=model-only").Options,
            StaticTenantContextAccessor.Unresolved);
        var filters = db.Model.FindEntityType(typeof(TenantCustomFieldDef))!.GetDeclaredQueryFilters().ToArray();
        filters.Should().NotBeEmpty();
        // EF substitutes the current context at query execution. Compiling the cached
        // expression directly would instead inspect whichever context built the model.
        foreach (var filter in filters)
        {
            filter.Expression!.ToString().Should().Contain(nameof(TenantCustomFieldDef.TenantId));
            filter.Expression.ToString().Should().NotContain(nameof(TenantCustomFieldDef.DeletedAt));
        }
    }

    [Theory]
    [InlineData("constructor")]
    [InlineData("prototype")]
    [InlineData("__proto__")]
    [InlineData("field_key")]
    public void Prototype_and_unbounded_map_keys_are_not_custom_field_keys(string key) => CustomFieldKey.IsValid(key).Should().BeFalse();

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    }
}
