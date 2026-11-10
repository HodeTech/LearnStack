using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.FieldConsents;

public sealed class GrantOwnMembershipFieldConsentCommandHandler(OwnFieldConsentAuthorization authorization,
    ICustomFieldConsentMembershipReader memberships, IExactCustomFieldDefinitionReader definitions,
    ITenantLocaleEligibilityReader locales, IMembershipFieldConsentWriteStore store,
    ITenantContext context, IAuditSubject audit, IClock clock, IGuidFactory ids)
    : IRequestHandler<GrantOwnMembershipFieldConsentCommand, Result<MembershipFieldConsentReceipt>>
{
    public async Task<Result<MembershipFieldConsentReceipt>> Handle(GrantOwnMembershipFieldConsentCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Valid(request)) return Fail("lockey_validation_failed");
        try
        {
            var authorized = await authorization.AdmitGrantAsync(request, cancellationToken);
            if (authorized.IsFailure) return Result<MembershipFieldConsentReceipt>.Fail(authorized.Error);
            var admission = authorized.Value;
            var membership = await memberships.ReadOwnActiveAsync(admission.MembershipId, request.DefinitionId,
                request.DefinitionVersion, admission.Plan, cancellationToken);
            if (membership is null || membership.MembershipId != admission.MembershipId
                || membership.TenantId != context.TenantId || membership.OrganizationId != context.OrganizationId
                || membership.SubjectUserId != admission.Actor)
                return Fail("lockey_not_found");
            var pin = new CustomFieldDefinitionPin(request.DefinitionId, request.DefinitionVersion);
            var definition = await definitions.ReadAsync(pin, CustomFieldResolution.ExistingPin, admission.Plan, cancellationToken);
            if (definition is null || definition.PiiCategory != PersonalDataCategory.Sensitive
                || definition.IsDiscarded || definition.Status is not ("Active" or "Deprecated")
                || (definition.Status == "Deprecated" && !membership.HasExactRetainedValuePin)
                || definition.PurposeKey is null || definition.Notices is null)
                return Fail("lockey_not_found");
            var locale = await locales.ReadEligibleAsync(request.NoticeLocale, cancellationToken);
            if (locale.IsFailure || locale.Value != request.NoticeLocale
                || !definition.Notices.TryGetValue(locale.Value, out var notice)
                || MembershipFieldConsentNotice.ComputeHash(notice) != request.ExpectedNoticeHash)
                return Fail("lockey_validation_failed");
            if (!authorization.IsCurrent(request, admission)) return Fail("lockey_forbidden");
            var tuple = new MembershipFieldConsentTuple(membership.MembershipId, admission.Actor,
                definition.DefinitionId, definition.SchemaVersion, definition.PurposeKey);
            var state = await store.ReadForAppendAsync(admission.Plan, tuple, cancellationToken);
            var predecessor = request.ExpectedEvidenceId is { } previous ? MembershipFieldConsentId.From(previous) : (MembershipFieldConsentId?)null;
            var decision = MembershipFieldConsentTransition.Decide(state.Current, state.LatestGrant,
                request.ExpectedSequence, predecessor, MembershipFieldConsentState.Granted, DateTimeOffset.MaxValue);
            if (decision == MembershipFieldConsentTransitionDecision.Unchanged)
            {
                if (!authorization.IsCurrent(request, admission)) return Fail("lockey_forbidden");
                audit.Designate(state.Current!);
                return Result.Ok(Receipt(state.Current!));
            }
            if (decision != MembershipFieldConsentTransitionDecision.Append)
                return Fail(decision == MembershipFieldConsentTransitionDecision.Stale ? "lockey_concurrency_conflict" : "lockey_business_rule_violation");
            if (!authorization.IsCurrent(request, admission)) return Fail("lockey_forbidden");
            var consent = MembershipFieldConsent.Create(MembershipFieldConsentId.From(ids.NewUuidV7()), membership.TenantId,
                membership.OrganizationId, membership.MembershipId, admission.Actor, admission.Actor, definition.DefinitionId,
                definition.SchemaVersion, definition.PurposeKey, locale.Value, request.ExpectedNoticeHash,
                request.ExpectedSequence + 1, MembershipFieldConsentState.Granted, predecessor, request.ExpectedSequence, clock.UtcNow);
            audit.Designate(consent);
            await store.AddAsync(consent, cancellationToken);
            return Result.Ok(Receipt(consent));
        }
        catch (MembershipFieldConsentStorageException failure) { return Fail(Map(failure.Failure)); }
        catch (IdentityMutationLockException) { return Fail("lockey_dependency_unavailable"); }
        catch (TenantLocaleEligibilityReadException) { return Fail("lockey_dependency_unavailable"); }
    }

    private static bool Valid(GrantOwnMembershipFieldConsentCommand request) => request.Confirmed
        && request.DefinitionId != Guid.Empty && request.DefinitionId.Version == 7 && request.DefinitionVersion > 0
        && !string.IsNullOrEmpty(request.NoticeLocale) && request.NoticeLocale.Length <= 35
        && request.ExpectedNoticeHash is { Length: 64 } && request.ExpectedNoticeHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
        && request.ExpectedSequence is >= 0 and <= MembershipFieldConsentTransition.MaximumSequence
        && ((request.ExpectedSequence == 0 && request.ExpectedEvidenceId is null)
            || (request.ExpectedSequence > 0 && request.ExpectedEvidenceId is { } id && id != Guid.Empty && id.Version == 7));

    internal static MembershipFieldConsentReceipt Receipt(MembershipFieldConsent row) => new(row.Id.Value, row.Sequence,
        row.State == MembershipFieldConsentState.Granted);
    internal static string Map(MembershipFieldConsentStorageFailure failure) => failure switch
    {
        MembershipFieldConsentStorageFailure.Stale or MembershipFieldConsentStorageFailure.Conflict => "lockey_concurrency_conflict",
        MembershipFieldConsentStorageFailure.Cooldown or MembershipFieldConsentStorageFailure.State => "lockey_business_rule_violation",
        _ => "lockey_dependency_unavailable",
    };
    private static Result<MembershipFieldConsentReceipt> Fail(string code) => OwnFieldConsentAuthorization.Failure<MembershipFieldConsentReceipt>(code);
}
