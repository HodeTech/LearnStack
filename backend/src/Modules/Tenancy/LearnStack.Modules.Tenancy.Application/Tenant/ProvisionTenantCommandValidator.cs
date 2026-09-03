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
/// <b>Every rule carries an explicit error code, and that is the whole of what a caller
/// receives.</b> <c>ValidationBehavior</c> builds the response from
/// <c>failure.ErrorCode ?? failure.ErrorMessage</c>, and FluentValidation always
/// populates <c>ErrorCode</c> with the validator's own name — so a rule left to its
/// defaults reaches the caller as <c>lockey_predicatevalidator</c>, and anything passed
/// to <c>WithMessage</c> is never read at all. Without the codes below, a malformed slug
/// and a tenant sharing its organization's id were byte-identical on the wire. Hardcoded
/// English would have been worse than useless here: it would have been invisible.
/// </para>
/// <para>
/// The pipeline runs this at step 1, so a refusal costs no transaction and no
/// announcement. A failure here is <c>Result.Fail(validation_failed)</c> and never an
/// exception, per the shipped behavior's contract.
/// </para>
/// </remarks>
internal sealed class ProvisionTenantCommandValidator : AbstractValidator<ProvisionTenantCommand>
{
    public ProvisionTenantCommandValidator()
    {
        // Cascade(Stop), because the rules are not independent of the first: the shape
        // predicate runs a regex, and a regex against a null slug throws
        // ArgumentNullException out of the validator itself — which is the 500 this file
        // exists to prevent, relocated one step earlier. `string` being non-nullable in
        // the command is a compile-time promise, and a deserializer is not bound by it.
        RuleForSlug(command => command.Slug);
        RuleForSlug(command => command.DefaultOrganizationSlug);

        RuleForDisplayName(command => command.DisplayName);
        RuleForDisplayName(command => command.DefaultOrganizationDisplayName);

        // The one cross-field rule, and the reason it is here rather than in an
        // aggregate: neither Tenant nor Organization can see the other's id, so neither
        // can notice that a caller sent the same Guid for both. The two rows are
        // different things in different tables and a shared id would read as a
        // relationship that does not exist.
        //
        // Hung off the organization id rather than off the command, because the property
        // name is what keys the RFC 7807 `errors` map — `RuleFor(command => command)`
        // yields the empty string, and an error under a "" key names nothing a client
        // can highlight.
        RuleFor(command => command.DefaultOrganizationId)
            .Must((command, organizationId) => command.TenantId.Value != organizationId.Value)
            .WithErrorCode("lockey_tenant_and_organization_share_an_id");
    }

    private void RuleForSlug(
        System.Linq.Expressions.Expression<Func<ProvisionTenantCommand, string>> slug) =>
        RuleFor(slug)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode("lockey_slug_required")
            .MaximumLength(UrlSlug.MaxLength).WithErrorCode("lockey_slug_too_long")
            .Must(value => UrlSlug.IsUrlSafe(value)).WithErrorCode("lockey_slug_not_url_safe");

    private void RuleForDisplayName(
        System.Linq.Expressions.Expression<Func<ProvisionTenantCommand, string>> displayName) =>
        RuleFor(displayName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode("lockey_display_name_required")
            .MaximumLength(MappedLength.DisplayName)
            .WithErrorCode("lockey_display_name_too_long");
}
