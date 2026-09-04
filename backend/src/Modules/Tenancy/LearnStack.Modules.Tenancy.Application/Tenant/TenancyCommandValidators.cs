using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Tenancy.Application.Tenant;

/// <summary>
/// What adding an organization refuses before a transaction is opened.
/// </summary>
/// <remarks>
/// The same three guards as provisioning's, read from the same constants the aggregate
/// throws on — <see cref="UrlSlug.IsUrlSafe"/>, <see cref="UrlSlug.MaxLength"/>,
/// <see cref="MappedLength.DisplayName"/>. Without them a mistyped slug is an
/// <c>ArgumentException</c> from <c>Organization.Create</c>, which has no entry in
/// <c>HttpStatusMap</c> and therefore answers 500 for something the caller can fix.
/// </remarks>
internal sealed class CreateOrganizationCommandValidator
    : AbstractValidator<CreateOrganizationCommand>
{
    public CreateOrganizationCommandValidator()
    {
        RuleFor(command => command.OrganizationId).MustBeAssigned();

        // Cascade(Stop) keeps the shape regex off a null a deserializer could supply;
        // Pattern().IsMatch(null) throws out of the validator itself.
        RuleFor(command => command.Slug)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode("lockey_slug_required")
            .MaximumLength(UrlSlug.MaxLength).WithErrorCode("lockey_slug_too_long")
            .Must(value => UrlSlug.IsUrlSafe(value)).WithErrorCode("lockey_slug_not_url_safe");

        RuleFor(command => command.DisplayName)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode("lockey_display_name_required")
            .MaximumLength(MappedLength.DisplayName)
            .WithErrorCode("lockey_display_name_too_long");
    }
}

/// <summary>
/// What mapping a host refuses before a transaction is opened.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape check runs <see cref="EffectiveHost.Normalize"/>, not a regex of its
/// own.</b> That function is what a request's <c>Host</c> header is put through at
/// resolution time, so "a host this validator accepts" and "a host the resolver can match"
/// are the same set by construction rather than by two rules kept in agreement.
/// </para>
/// <para>
/// <b>Publicly-live-without-active is refused here as well as in the aggregate.</b> The
/// schema has no <c>CHECK</c> for it, and the combination would serve anonymous traffic
/// for a mapping the tenant does not yet own.
/// </para>
/// </remarks>
internal sealed class MapHostToTenantCommandValidator : AbstractValidator<MapHostToTenantCommand>
{
    public MapHostToTenantCommandValidator()
    {
        // Optional by contract — a tenant-wide host carries none — but an id that IS
        // supplied must be real, or PlatformHostMapping.Create throws where a refusal
        // belongs.
        RuleFor(command => command.OrganizationId).MustBeAssignedWhenPresent();

        RuleFor(command => command.Host)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode("lockey_host_required")
            .Must(host => EffectiveHost.Normalize(host) is not null)
            .WithErrorCode("lockey_host_not_resolvable");

        RuleFor(command => command)
            .Must(command => command.IsActive || !command.IsPubliclyLive)
            .WithErrorCode("lockey_host_live_before_active")
            .OverridePropertyName(nameof(MapHostToTenantCommand.IsPubliclyLive));
    }
}

/// <summary>
/// The rule every client-supplied strongly-typed id needs before anything reads it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two sentinels, and reading <c>.Value</c> on the first throws.</b> A Vogen id nobody
/// constructed is uninitialized, and touching <c>Value</c> raises from inside the id type
/// — so a validator that compares ids, or an aggregate factory that stores one, turns a
/// client's malformed input into a <c>500</c> rather than a refusal. The all-zero
/// <c>Guid</c> is the second sentinel: it constructs cleanly and means nothing.
/// </para>
/// <para>
/// <c>Cascade(Stop)</c> on every caller, because the rules that follow read the value.
/// </para>
/// </remarks>
internal static class IdentifierRules
{
    internal static IRuleBuilderOptions<TCommand, TId> MustBeAssigned<TCommand, TId>(
        this IRuleBuilderInitial<TCommand, TId> rule)
        where TId : struct, IStronglyTypedId<Guid> =>
        rule.Cascade(CascadeMode.Stop)
            .Must(id => id.IsInitialized() && id.Value != Guid.Empty)
            .WithErrorCode("lockey_identifier_required");

    internal static IRuleBuilderOptions<TCommand, TId?> MustBeAssignedWhenPresent<TCommand, TId>(
        this IRuleBuilderInitial<TCommand, TId?> rule)
        where TId : struct, IStronglyTypedId<Guid> =>
        rule.Cascade(CascadeMode.Stop)
            .Must(id => id is not { } value || (value.IsInitialized() && value.Value != Guid.Empty))
            .WithErrorCode("lockey_identifier_required");
}
