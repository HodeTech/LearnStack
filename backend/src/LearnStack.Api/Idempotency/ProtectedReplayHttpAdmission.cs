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
        long version = 0;
        if (descriptor.Creates ? http.Request.Headers.IfMatch.Count != 0
            : !ProtectedEntityTag.TryRead(http.Request, out version)) return Result<None>.Fail(ProtectedEntityTag.Invalid());
        if (!context.IsResolved || !StronglyTypedId.IsAssigned(context.TenantId) || context.TenantId == TenantId.PlatformSentinel
            || context.UserId is not { } actor || !StronglyTypedId.IsAssigned(actor) || actor == UserId.SystemActor
            || candidates.ForValidatedScope(context.TenantId, context.OrganizationId) is not { IsActive: true } candidate
            || candidate.Actor != actor)
            return await operations().CheckHttpAsync(declared.RequestType, endpoint!, http.RequestAborted);

        // Authorization precedes MVC filters, including RequestSizeLimit. Bound
        // actual bytes here before fingerprinting, without consuming model input.
        if (!AdministrationHttpInput.SupportsJsonMedia(http.Request)) return Failure("lockey_validation_failed");
        await AdministrationHttpInput.ReadBodyBytesAsync(http);
        long? expected = descriptor.Creates ? null : version;
        var fingerprint = await ProtectedIdempotencyFilter.ComputeFingerprintAsync(http, context, descriptor, expected, http.RequestAborted);
        var binding = new RequestBinding(http, endpoint!, classifier, declared, descriptor, context, candidate, key, fingerprint, expected);
        IdempotentResponse? stored;
        try { stored = await store.ReadCompletedAsync(context.TenantId, key, fingerprint, http.RequestAborted); }
        catch (IdempotencyStoreException) { return Failure("lockey_dependency_unavailable"); }
        if (!binding.Matches(http, context, descriptor, key, fingerprint, expected)) return Failure("lockey_forbidden");
        if (stored is not null && !ProtectedIdempotencyFilter.ValidStoredResponse(stored, descriptor, context.TenantId))
            return Failure("lockey_dependency_unavailable");
        var frozen = stored is null ? null : Snapshot(stored);
        var requestType = frozen is null ? declared.RequestType : ReceiptRequest(descriptor.Operation);
        var result = await operations().CheckHttpAsync(requestType, endpoint!, http.RequestAborted);
        if (result.IsFailure) return result;
        if (!binding.Matches(http, context, descriptor, key, fingerprint, expected)) return Failure("lockey_forbidden");
        // Both modes are bound, so deleting/replacing the feature cannot make a
        // production read admission fall back to the mutation branch.
        http.Features.Set(new Admission(binding, frozen));
        return result;
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
        ProtectedAdministrationOperation.SetMembershipProfile => typeof(ReadMemberProfileReceiptQuery),
        ProtectedAdministrationOperation.SetOwnMembershipProfile => typeof(ReadOwnMembershipProfileReceiptQuery),
        ProtectedAdministrationOperation.ChangeMembershipStatus or ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments => typeof(ReadMembershipReceiptQuery),
        ProtectedAdministrationOperation.ChangeRoleGrants or ProtectedAdministrationOperation.AdoptRoleGrantManifest => typeof(ReadRoleReceiptQuery),
        ProtectedAdministrationOperation.CreateOrganization or ProtectedAdministrationOperation.RenameOrganization
            or ProtectedAdministrationOperation.ArchiveOrganization => typeof(ReadOrganizationReceiptQuery),
        ProtectedAdministrationOperation.SelectDefaultOrganization => typeof(ReadDefaultOrganizationReceiptQuery),
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
        AuthorizationCandidate candidate, string key, string fingerprint, long? expected)
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
            && scope.UserId == _actor && scope.Origin == _origin && candidate.IsActive
            && candidate.Actor == _actor && candidate.TenantId == _tenant && candidate.OrganizationId == _organization
            && descriptor == operation && key == currentKey && fingerprint == currentFingerprint && expected == currentExpected
            && classifier.Classify(endpoint, out var currentPolicy) == HttpOperationClassification.Application
            && ReferenceEquals(policy, currentPolicy)
            && AdministrationHttpInput.SupportsJsonMedia(current.Request);
    }
}
