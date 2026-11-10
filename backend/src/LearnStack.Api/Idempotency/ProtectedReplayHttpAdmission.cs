using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Api.Idempotency;

internal enum ProtectedHttpAdmissionKind { Unbound, Mutation, Replay, Refused }
internal sealed record ProtectedHttpAdmission(ProtectedHttpAdmissionKind Kind, IdempotentResponse? Response = null);

/// <summary>Current HTTP disclosure admission binds one retained snapshot; it never authorizes a mutation.</summary>
internal static class ProtectedReplayHttpAdmission
{
    internal static async Task<Result<None>> AuthorizeAsync(HttpContext http, OperationPolicy declared,
        OperationHttpClassifier classifier, ITenantContext context, IIdempotencyStore store,
        TenantAuthorizationCandidateSource candidates, Func<OperationAuthorizationCoordinator> operations)
    {
        var endpoint = http.GetEndpoint();
        if (classifier.Classify(endpoint, out var canonical) != HttpOperationClassification.Application
            || !ReferenceEquals(canonical, declared)) return Failure("lockey_forbidden");
        var descriptor = ProtectedAdministrationDescriptor.ForRequest(declared.RequestType);
        if (descriptor is null) return await operations().CheckHttpAsync(declared.RequestType, endpoint!, http.RequestAborted);
        if (http.Features.Get<Admission>() is not null) return Failure("lockey_forbidden");
        if (!IdempotencyFilter.TryReadKey(http, out var key))
            return Result<None>.Fail(new Error(LocalizedMessage.Of("lockey_validation_failed"),
                new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
                {
                    [IdempotentAttribute.ErrorsKey] = [LocalizedMessage.Of("lockey_idempotency_key_invalid")],
                }));
        if (!descriptor.TryReadPrecondition(http.Request, out var expected)) return Result<None>.Fail(ProtectedEntityTag.Invalid());
        if (!context.IsResolved || !StronglyTypedId.IsAssigned(context.TenantId) || context.TenantId == TenantId.PlatformSentinel
            || context.UserId is not { } actor || !StronglyTypedId.IsAssigned(actor) || actor == UserId.SystemActor)
            return await operations().CheckHttpAsync(declared.RequestType, endpoint!, http.RequestAborted);
        ConsentSubjectSource? consent = null;
        AuthorizationCandidate? candidate = null;
        if (descriptor.Operation == ProtectedAdministrationOperation.WithdrawOwnFieldConsent)
        {
            consent = http.Features.Get<ConsentSubjectSource>();
            if (declared.Kind != OperationPolicyKind.OwnConsentEntry || context.Origin != TenantContextOrigin.HostAndConsentSubjectScope
                || consent is null || consent.Actor != actor || !consent.MatchesScope(http, context.TenantId, context.OrganizationId)
                || consent.Request is not WithdrawOwnMembershipFieldConsentCommand)
                return Failure("lockey_forbidden");
        }
        else
        {
            candidate = candidates.ForValidatedScope(context.TenantId, context.OrganizationId);
            if (candidate is not { IsActive: true } || candidate.Actor != actor)
                return await operations().CheckHttpAsync(declared.RequestType, endpoint!, http.RequestAborted);
        }

        // Authorization precedes MVC filters, including RequestSizeLimit. Bound
        // actual bytes here before fingerprinting, without consuming model input.
        if (!AdministrationHttpInput.SupportsJsonMedia(http.Request)) return Failure("lockey_validation_failed");
        if (consent is not null && !await consent.MatchesCapturedWithdrawalAsync(http)) return Failure("lockey_forbidden");
        await AdministrationHttpInput.ReadBodyBytesAsync(http);
        string fingerprint;
        try { fingerprint = await ProtectedIdempotencyFilter.ComputeFingerprintAsync(http, context, descriptor, expected, http.RequestAborted); }
        catch (Exception failure) when (failure is CustomFieldFingerprintUnavailableException or IdempotencyStoreException)
        { return Failure("lockey_dependency_unavailable"); }
        var binding = new RequestBinding(http, endpoint!, classifier, declared, descriptor, context, candidate, consent, key, fingerprint, expected);
        IdempotentResponse? stored;
        try { stored = await store.ReadCompletedAsync(context.TenantId, key, fingerprint, http.RequestAborted); }
        catch (IdempotencyStoreException) { return Failure("lockey_dependency_unavailable"); }
        if (!binding.Matches(http, context, descriptor, key, fingerprint, expected)) return Failure("lockey_forbidden");
        var current = await ConsentInputMatchesAsync();
        if (current.IsFailure) return current;
        if (stored is not null && !ProtectedIdempotencyFilter.ValidStoredResponse(stored, descriptor, context.TenantId))
            return Failure("lockey_dependency_unavailable");
        var frozen = stored is null ? null : Snapshot(stored);
        var requestType = frozen is null ? declared.RequestType : ReceiptRequest(descriptor.Operation);
        if (consent is not null && frozen is not null
            && (frozen.ReplayTarget is not { } target || !consent.SelectWithdrawalReceipt(http, target.Id)))
            return Failure("lockey_forbidden");
        var result = await operations().CheckHttpAsync(requestType, endpoint!, http.RequestAborted);
        if (result.IsFailure) return result;
        if (!binding.Matches(http, context, descriptor, key, fingerprint, expected)) return Failure("lockey_forbidden");
        current = await ConsentInputMatchesAsync();
        if (current.IsFailure) return current;
        // Both modes are bound, so deleting/replacing the feature cannot make a
        // production read admission fall back to the mutation branch.
        http.Features.Set(new Admission(binding, frozen));
        return result;

        async Task<Result<None>> ConsentInputMatchesAsync()
        {
            if (consent is null) return Result.Ok(None.Value);
            if (!await consent.MatchesCapturedWithdrawalAsync(http) || !IdempotencyFilter.TryReadKey(http, out var currentKey)
                || currentKey != key) return Failure("lockey_forbidden");
            try
            {
                var currentFingerprint = await ProtectedIdempotencyFilter.ComputeFingerprintAsync(http, context, descriptor, expected, http.RequestAborted);
                return fingerprint == currentFingerprint && await consent.MatchesCapturedWithdrawalAsync(http)
                    && IdempotencyFilter.TryReadKey(http, out currentKey) && currentKey == key
                    ? Result.Ok(None.Value) : Failure("lockey_forbidden");
            }
            catch (Exception failure) when (failure is CustomFieldFingerprintUnavailableException or IdempotencyStoreException)
            { return Failure("lockey_dependency_unavailable"); }
        }
    }

    internal static ProtectedHttpAdmission Consume(HttpContext http, ITenantContext context,
        ProtectedAdministrationDescriptor descriptor, string key, string fingerprint, long? expected)
    {
        if (http.Features.Get<Admission>() is not { } admission)
            return new(OperationHttpClassifier.RequiresProtectedAdmission(http.GetEndpoint())
                ? ProtectedHttpAdmissionKind.Refused : ProtectedHttpAdmissionKind.Unbound);
        if (Interlocked.Exchange(ref admission.Consumed, 1) != 0
            || !admission.Binding.Matches(http, context, descriptor, key, fingerprint, expected))
            return new(ProtectedHttpAdmissionKind.Refused);
        return admission.Response is { } stored
            ? new(ProtectedHttpAdmissionKind.Replay, stored) : new(ProtectedHttpAdmissionKind.Mutation);
    }

    internal static Type ReceiptRequest(ProtectedAdministrationOperation operation) => operation switch
    {
        ProtectedAdministrationOperation.SetMembershipProfile or ProtectedAdministrationOperation.SetMemberCustomFields => typeof(ReadMemberProfileReceiptQuery),
        ProtectedAdministrationOperation.SetOwnMembershipProfile or ProtectedAdministrationOperation.SetOwnCustomFields => typeof(ReadOwnMembershipProfileReceiptQuery),
        ProtectedAdministrationOperation.ChangeMembershipStatus or ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments => typeof(ReadMembershipReceiptQuery),
        ProtectedAdministrationOperation.ChangeRoleGrants or ProtectedAdministrationOperation.AdoptRoleGrantManifest => typeof(ReadRoleReceiptQuery),
        ProtectedAdministrationOperation.CreateOrganization or ProtectedAdministrationOperation.RenameOrganization
            or ProtectedAdministrationOperation.ArchiveOrganization => typeof(ReadOrganizationReceiptQuery),
        ProtectedAdministrationOperation.SelectDefaultOrganization => typeof(ReadDefaultOrganizationReceiptQuery),
        ProtectedAdministrationOperation.CreateInvitation or ProtectedAdministrationOperation.RevokeInvitation or ProtectedAdministrationOperation.ReissueInvitation => typeof(ReadInvitationReceiptQuery),
        ProtectedAdministrationOperation.SetTenantInvitationLifetime => typeof(ReadInvitationPolicyReceiptQuery),
        ProtectedAdministrationOperation.RegisterCustomFieldDefinition or ProtectedAdministrationOperation.ReviseCustomFieldDraft
            or ProtectedAdministrationOperation.PublishCustomFieldRevision or ProtectedAdministrationOperation.RetireCustomFieldRevision
            or ProtectedAdministrationOperation.DiscardCustomFieldDraft => typeof(ReadCustomFieldDefinitionReceiptQuery),
        ProtectedAdministrationOperation.GrantOwnFieldConsent => typeof(ReadOwnGrantedFieldConsentReceiptQuery),
        ProtectedAdministrationOperation.WithdrawOwnFieldConsent => typeof(ReadOwnWithdrawnFieldConsentReceiptQuery),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static Result<None> Failure(string key) => Result<None>.Fail(new Error(LocalizedMessage.Of(key)));
    private static IdempotentResponse Snapshot(IdempotentResponse response) => response with
    {
        Body = response.Body.ToArray(),
        Headers = response.Headers.ToDictionary(item => item.Key,
            item => (IReadOnlyList<string>)item.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
    };

    private sealed class Admission(RequestBinding binding, IdempotentResponse? response)
    {
        internal RequestBinding Binding { get; } = binding;
        internal IdempotentResponse? Response { get; } = response;
        internal int Consumed;
    }

    private sealed class RequestBinding(HttpContext http, Endpoint endpoint, OperationHttpClassifier classifier,
        OperationPolicy policy, ProtectedAdministrationDescriptor descriptor, ITenantContext context,
        AuthorizationCandidate? candidate, ConsentSubjectSource? consent, string key, string fingerprint, long? expected)
    {
        private readonly TenantId _tenant = context.TenantId;
        private readonly OrganizationId? _organization = context.OrganizationId;
        private readonly UserId? _actor = context.UserId;
        private readonly TenantContextOrigin? _origin = context.Origin;

        internal bool Matches(HttpContext current, ITenantContext scope, ProtectedAdministrationDescriptor operation,
            string currentKey, string currentFingerprint, long? currentExpected) =>
            ReferenceEquals(http, current) && ReferenceEquals(endpoint, current.GetEndpoint())
            && ReferenceEquals(context, scope) && !current.RequestAborted.IsCancellationRequested
            && scope.IsResolved && scope.TenantId == _tenant && scope.OrganizationId == _organization
            && scope.UserId == _actor && scope.Origin == _origin
            && (consent is not null ? candidate is null && consent.Actor == _actor && consent.MatchesScope(current, _tenant, _organization)
                : candidate is { IsActive: true } && candidate.Actor == _actor
                    && candidate.TenantId == _tenant && candidate.OrganizationId == _organization)
            && descriptor == operation && key == currentKey && fingerprint == currentFingerprint && expected == currentExpected
            && IdempotencyFilter.TryReadKey(current, out var headerKey) && headerKey == key
            && classifier.Classify(endpoint, out var currentPolicy) == HttpOperationClassification.Application
            && ReferenceEquals(policy, currentPolicy)
            && AdministrationHttpInput.SupportsJsonMedia(current.Request);
    }
}
