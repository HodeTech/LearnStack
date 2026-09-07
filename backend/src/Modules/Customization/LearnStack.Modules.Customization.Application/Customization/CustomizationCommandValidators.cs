using FluentValidation;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Localization;

namespace LearnStack.Modules.Customization.Application.Customization;

/// <summary>
/// The guards a customization command fails before a transaction is opened.
/// </summary>
/// <remarks>
/// Each rule reads the constant or the function the aggregate itself throws on —
/// <see cref="CustomizationKey.MaxLength"/>, <see cref="UrlSlug.IsUrlSafe"/>,
/// <see cref="CompositeRendererKey.IsKnown"/>,
/// <see cref="LocalizedText.TryFrom"/>. Without them a mistyped key is an
/// <c>ArgumentException</c> out of a factory, which has no entry in
/// <c>HttpStatusMap</c> and therefore answers <c>500</c> for something the author
/// can fix.
/// </remarks>
internal static class CustomizationRules
{
    /// <summary>
    /// A customization or item key: the slug shape, at the wider customization cap.
    /// </summary>
    /// <remarks>
    /// <c>UrlSlug.IsUrlSafe</c> for the shape and
    /// <see cref="CustomizationKey.MaxLength"/> for the width, which is what
    /// <c>CustomizationKey.EnsureValid</c> itself composes — rather than a
    /// predicate of this file's own, which would be a second rule to keep in
    /// agreement with the first.
    /// </remarks>
    internal static IRuleBuilderOptions<TCommand, string> MustBeACustomizationKey<TCommand>(
        this IRuleBuilderInitial<TCommand, string> rule) =>
        rule.Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode("lockey_customization_key_required")
            .MaximumLength(CustomizationKey.MaxLength)
            .WithErrorCode("lockey_customization_key_too_long")
            .Must(UrlSlug.IsUrlSafe).WithErrorCode("lockey_customization_key_not_url_safe");

    /// <summary>A Pattern B localized map the aggregate will accept.</summary>
    internal static IRuleBuilderOptions<TCommand, IReadOnlyDictionary<string, string>>
        MustBeALocalizedName<TCommand>(
            this IRuleBuilderInitial<TCommand, IReadOnlyDictionary<string, string>> rule) =>
        rule.Cascade(CascadeMode.Stop)
            .NotNull().WithErrorCode("lockey_display_name_required")
            .Must(values => LocalizedText.TryFrom(values, out _))
            .WithErrorCode("lockey_display_name_not_localizable");

    /// <summary>
    /// The rule every caller-supplied identifier needs before anything reads it.
    /// </summary>
    /// <remarks>
    /// These contracts carry <c>Guid</c> rather than the module's own typed id, for
    /// the reason each of them records — so the sentinel to refuse is the all-zero
    /// value, which constructs cleanly and means nothing. The handler turns the
    /// accepted value into a typed id, whose factory refuses the same thing again.
    /// </remarks>
    internal static IRuleBuilderOptions<TCommand, Guid> MustBeAssigned<TCommand>(
        this IRuleBuilderInitial<TCommand, Guid> rule) =>
        rule.Cascade(CascadeMode.Stop)
            .NotEqual(Guid.Empty)
            .WithErrorCode("lockey_identifier_required");

    /// <summary>
    /// A revision number the versioned key can carry.
    /// </summary>
    /// <remarks>
    /// The aggregate refuses anything below 1 — version 0 is the value an
    /// uninitialized <c>int</c> already has, so accepting it would make "nobody set
    /// this" and "the first revision" the same row.
    /// </remarks>
    internal static IRuleBuilderOptions<TCommand, int> MustBeASchemaVersion<TCommand>(
        this IRuleBuilderInitial<TCommand, int> rule) =>
        rule.Cascade(CascadeMode.Stop)
            .GreaterThanOrEqualTo(1).WithErrorCode("lockey_schema_version_invalid");
}

internal sealed class RegisterTenantContentTypeCommandValidator
    : AbstractValidator<RegisterTenantContentTypeCommand>
{
    public RegisterTenantContentTypeCommandValidator()
    {
        RuleFor(command => command.ContentTypeId).MustBeAssigned();
        RuleFor(command => command.Key).MustBeACustomizationKey();
        RuleFor(command => command.SchemaVersion).MustBeASchemaVersion();
        RuleFor(command => command.DisplayName).MustBeALocalizedName();

        // Shape only. Whether the document is an admissible JSON Schema is
        // ADR-0043's four gates, which run in the handler — they answer with a JSON
        // pointer, and a FluentValidation rule keys its failures on a property name,
        // so a refusal carried from here would lose the location the author needs.
        RuleFor(command => command.JsonSchema)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode("lockey_json_schema_required");

        RuleFor(command => command.RendererKey)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode("lockey_renderer_key_required")
            .Must(CompositeRendererKey.IsKnown)
            .WithErrorCode("lockey_renderer_key_unknown");
    }
}

internal sealed class PublishTenantContentTypeCommandValidator
    : AbstractValidator<PublishTenantContentTypeCommand>
{
    public PublishTenantContentTypeCommandValidator() =>
        RuleFor(command => command.ContentTypeId).MustBeAssigned();
}

internal sealed class RegisterTenantLevelTaxonomyCommandValidator
    : AbstractValidator<RegisterTenantLevelTaxonomyCommand>
{
    public RegisterTenantLevelTaxonomyCommandValidator()
    {
        RuleFor(command => command.TaxonomyId).MustBeAssigned();
        RuleFor(command => command.Key).MustBeACustomizationKey();
        RuleFor(command => command.SchemaVersion).MustBeASchemaVersion();
        RuleFor(command => command.DisplayName).MustBeALocalizedName();

        // A vocabulary with no bands is not a smaller vocabulary; it is one the
        // renderer has nothing to draw. The aggregate has no equivalent guard,
        // because a taxonomy is legitimately empty for the instant between
        // construction and its first AddItem.
        RuleFor(command => command.Items)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithErrorCode("lockey_taxonomy_items_required")
            .Must(items => items.Count > 0).WithErrorCode("lockey_taxonomy_items_required")
            .Must(items => items.Count <= TenantLevelTaxonomy.MaxItems)
            .WithErrorCode("lockey_taxonomy_items_too_many");

        // NotNull first: `SetValidator` skips a null element rather than refusing
        // it, so `[null]` was valid input and the handler dereferenced it —
        // measured, a NullReferenceException out of FirstDuplicate, which is a 500
        // for a body a caller can fix.
        RuleForEach(command => command.Items)
            .NotNull().WithErrorCode("lockey_taxonomy_item_required")
            .SetValidator(new TaxonomyItemInputValidator());
    }
}

internal sealed class TaxonomyItemInputValidator : AbstractValidator<TaxonomyItemInput>
{
    public TaxonomyItemInputValidator()
    {
        RuleFor(item => item.Key).MustBeACustomizationKey();
        RuleFor(item => item.DisplayName).MustBeALocalizedName();

        RuleFor(item => item.Sort)
            .GreaterThanOrEqualTo((short)0).WithErrorCode("lockey_taxonomy_item_sort_invalid");

        // Null is the absence of metadata and is allowed; a present value has to be
        // JSON the column takes, because PostgreSQL would otherwise refuse it three
        // layers from here — 22P02 for the shape, 22P05 for a NUL — and it has to be
        // inside § 8.4's 256 KB, which nothing else on this path bounds.
        RuleFor(item => item.Metadata!)
            .Must(JsonValue.IsStorableRow)
            .When(item => item.Metadata is not null)
            .WithErrorCode("lockey_taxonomy_item_metadata_not_json");
    }
}

internal sealed class PublishTenantLevelTaxonomyCommandValidator
    : AbstractValidator<PublishTenantLevelTaxonomyCommand>
{
    public PublishTenantLevelTaxonomyCommandValidator() =>
        RuleFor(command => command.TaxonomyId).MustBeAssigned();
}
