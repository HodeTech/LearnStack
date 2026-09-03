using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;

namespace LearnStack.Modules.Tenancy.Application.Tenant;

/// <summary>
/// What provisioning refuses before a transaction is opened.
/// </summary>
/// <remarks>
/// <para>
/// <b>It shares the aggregates' guards rather than copying or skipping them.</b> Slug
/// shape and the two mapped widths are declared once, in the domain — <see
/// cref="UrlSlug.IsUrlSafe"/>, <see cref="UrlSlug.MaxLength"/>, <see
/// cref="MappedLength.DisplayName"/> — and read here as well as at the factories. The
/// factories are still the authority and still throw; this is the layer that turns the
/// same refusal into an answer a caller can act on.
/// </para>
/// <para>
/// <b>Why the duplication would have been worse than the gap.</b> Leaving shape to the
/// factories alone was the first shape of this file, and it was measured wrong:
/// <c>ArgumentException</c> has no entry in <c>HttpStatusMap</c>, so a mistyped slug
/// became a 500 — after ValidationBehavior passed it, after TransactionBehavior opened a
/// transaction, and after the tenant was announced on the connection. Copying the regex
/// and the numbers here instead would have been a second place to change and a second
/// place to drift; a shared constant is neither.
/// </para>
/// <para>
/// The pipeline runs this at step 1, so a refusal costs no transaction and no
/// announcement. A failure here is <c>Result.Fail(validation_failed)</c> and never an
/// exception, per the shipped behavior's contract.
/// </para>
/// </remarks>
public sealed class ProvisionTenantCommandValidator : AbstractValidator<ProvisionTenantCommand>
{
    public ProvisionTenantCommandValidator()
    {
        // Cascade(Stop), because the rules below are not independent of the first: the
        // shape predicate runs a regex, and a regex against a null slug throws
        // ArgumentNullException out of the validator itself — which is the 500 this file
        // exists to prevent, relocated one step earlier. `string` being non-nullable in
        // the command is a compile-time promise, and a deserializer is not bound by it.
        RuleFor(command => command.Slug)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(UrlSlug.MaxLength)
            .Must(UrlSlug.IsUrlSafe!)
            .WithMessage(SlugShape);

        RuleFor(command => command.DefaultOrganizationSlug)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(UrlSlug.MaxLength)
            .Must(UrlSlug.IsUrlSafe!)
            .WithMessage(SlugShape);

        RuleFor(command => command.DisplayName)
            .NotEmpty()
            .MaximumLength(MappedLength.DisplayName);

        RuleFor(command => command.DefaultOrganizationDisplayName)
            .NotEmpty()
            .MaximumLength(MappedLength.DisplayName);

        // The one cross-field rule, and the reason it is here rather than in an
        // aggregate: neither Tenant nor Organization can see the other's id, so neither
        // can notice that a caller sent the same Guid for both. The two rows are
        // different things in different tables and a shared id would read as a
        // relationship that does not exist.
        RuleFor(command => command)
            .Must(command => command.TenantId.Value != command.DefaultOrganizationId.Value)
            .WithMessage(
                "A tenant and its default organization are separate rows and must not "
                + "share an id.");
    }

    private const string SlugShape =
        "'{PropertyValue}' is not a URL-safe slug: lowercase letters, digits and single "
        + "interior hyphens only.";
}
