using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Application.Administration;
using LearnStack.Modules.Tenancy.Application.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Tenancy.Application.Authorization;

/// <summary>Same-invocation fresh Identity evaluation and retained Tenancy mutation fences.</summary>
public sealed class TenancyAdministrationAuthorization(IOperationPolicyCatalog catalog, IOperationInvocationContext invocations,
    ITenantContext context, IProtectedOperationAuthorizer authorizer, IAuthorizationRefusalReporter refusals,
    ITenancyAdministrationReader reader, IIdentityMutationLocks locks, IUnitOfWork unit)
{
    internal bool Matches(object request, OperationInvocation invocation)
    {
        var permission = Permission(request);
        var write = request is not (ListOrganizationsQuery or GetOrganizationDetailQuery
            or ReadOrganizationReceiptQuery or ReadDefaultOrganizationReceiptQuery);
        return permission is not null && ReferenceEquals(invocations.CurrentInvocation, invocation)
            && invocation is { IsActive: true, EntryKind: OperationEntryKind.Application, TrustedSource: null }
            && ReferenceEquals(invocation.Operation, request) && ReferenceEquals(invocation.Context, context)
            && invocation.Candidate is { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
            && catalog.TryGet(request.GetType(), out var policy) && ReferenceEquals(invocation.Policy, policy)
            && policy.RequestType == request.GetType() && policy.Kind == OperationPolicyKind.Protected
            && policy.RequiresOrglessWrite == write && !policy.RequiresOrganization
            && policy.ReadScope == OperationReadScope.Scoped && policy.Alternatives.Length == 1
            && policy.Alternatives[0].ReadScope == OperationReadScope.Scoped
            && policy.Alternatives[0].RequiredPermissions.Length == 1
            && policy.Alternatives[0].RequiredPermissions[0] == permission
            && context.IsResolved && StronglyTypedId.IsAssigned(context.TenantId) && context.TenantId != TenantId.PlatformSentinel
            && (!write || context.OrganizationId is null)
            && context.UserId is { } actor && StronglyTypedId.IsAssigned(actor) && actor != UserId.SystemActor
            && context.Origin is TenantContextOrigin.HostAndClaim or TenantContextOrigin.ClaimAndMembership
                or TenantContextOrigin.HostAndNativeSessionScope
            && candidate.Actor == actor && candidate.TenantId == context.TenantId && candidate.OrganizationId == context.OrganizationId
            && invocation.Actor == actor && invocation.TenantId == context.TenantId && invocation.OrganizationId == context.OrganizationId
            && invocations.CurrentDecision is { Outcome: ProtectedOperationOutcome.Allowed, IsActive: true } admitted
            && ReferenceEquals(admitted.Invocation, invocation);
    }

    internal async Task<Result<OperationInvocation>> AdmitReadAsync(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (invocations.CurrentInvocation is not { } invocation || !Matches(request, invocation))
            return TenancyAdministrationFailures.Code<OperationInvocation>("lockey_forbidden");
        if (!TenancyAdministrationInput.Valid(request))
            return TenancyAdministrationFailures.Code<OperationInvocation>("lockey_validation_failed");
        var check = await RecheckAsync(request, invocation, cancellationToken);
        return check.IsFailure ? Result<OperationInvocation>.Fail(check.Error) : Result.Ok(invocation);
    }

    internal async Task<Result<TenancyAdministrationAdmission>> AdmitWriteAsync(object request, CancellationToken cancellationToken)
    {
        var admitted = await AdmitReadAsync(request, cancellationToken);
        if (admitted.IsFailure) return Result<TenancyAdministrationAdmission>.Fail(admitted.Error);
        if (!await HasCurrentTenantAsync(cancellationToken))
            return TenancyAdministrationFailures.Code<TenancyAdministrationAdmission>("lockey_not_found");
        if (!Matches(request, admitted.Value)) return TenancyAdministrationFailures.Code<TenancyAdministrationAdmission>("lockey_forbidden");
        var mode = request is ArchiveOrganizationCommand or SelectDefaultOrganizationCommand or AddManagedTenantLocaleCommand
            or SetManagedDefaultTenantLocaleCommand or SetTenantLocaleEnabledCommand or RemoveTenantLocaleCommand
            ? IdentityTenantLockMode.Exclusive : IdentityTenantLockMode.Shared;
        var plan = new IdentityMutationLockPlan([], context.TenantId, mode);
        await locks.AcquireAsync(plan, cancellationToken);
        if (!Matches(request, admitted.Value)) return TenancyAdministrationFailures.Code<TenancyAdministrationAdmission>("lockey_forbidden");
        locks.RequireCurrent(plan);
        return Result.Ok(new TenancyAdministrationAdmission(request, admitted.Value, plan));
    }

    internal async Task<Result<None>> VerifyWriteAsync(TenancyAdministrationAdmission admission, CancellationToken cancellationToken)
    {
        if (!Matches(admission.Request, admission.Invocation)) return TenancyAdministrationFailures.Code<None>("lockey_forbidden");
        locks.RequireCurrent(admission.Plan);
        if (!await HasCurrentTenantAsync(cancellationToken)) return TenancyAdministrationFailures.Code<None>("lockey_not_found");
        var check = await RecheckAsync(admission.Request, admission.Invocation, cancellationToken);
        if (check.IsFailure) return check;
        locks.RequireCurrent(admission.Plan);
        return check;
    }

    internal async Task<Result<None>> RecheckAsync(object request, OperationInvocation invocation, CancellationToken cancellationToken)
    {
        if (!Matches(request, invocation)) return TenancyAdministrationFailures.Code<None>("lockey_forbidden");
        // Identity remains the sole evaluator. No copied permission algorithm or new decision source.
        var decision = await authorizer.AuthorizeAsync(invocation, invocation.Policy, invocation.Candidate!, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Matches(request, invocation) || !ReferenceEquals(decision.Invocation, invocation) || !decision.IsActive)
            return TenancyAdministrationFailures.Code<None>("lockey_dependency_unavailable");
        if (decision.Outcome == ProtectedOperationOutcome.Allowed)
        {
            refusals.ReportRecheck(decision);
            return Result.Ok(None.Value);
        }
        if (decision.Outcome is ProtectedOperationOutcome.NotCovered or ProtectedOperationOutcome.Forbidden)
            refusals.ReportResourceRefusal(decision, AuthorizationRefusalReason.Capability);
        return TenancyAdministrationFailures.Code<None>(decision.Outcome switch
        {
            ProtectedOperationOutcome.NotCovered => "lockey_not_found",
            ProtectedOperationOutcome.Forbidden => "lockey_forbidden",
            _ => "lockey_dependency_unavailable",
        });
    }

    internal Result<T> StorageFailure<T>(Exception failure)
    {
        unit.MarkRollbackOnly();
        if (failure is TenancyAdministrationStorageException { Failure: TenancyAdministrationStorageFailure.InvalidCursor })
            return TenantWriteFailures.Field<T>("lockey_validation_failed", "Cursor", "lockey_invalid_value");
        if (failure is TenancyAdministrationStorageException { Failure: TenancyAdministrationStorageFailure.SlugTaken })
            return TenantWriteFailures.Field<T>("lockey_business_rule_violation", "Slug", "lockey_slug_taken");
        return TenancyAdministrationFailures.Code<T>(failure switch
        {
            AggregateConcurrencyException => "lockey_concurrency_conflict",
            AggregateConflictException => "lockey_concurrency_conflict",
            TenancyAdministrationStorageException { Failure: TenancyAdministrationStorageFailure.Conflict } => "lockey_concurrency_conflict",
            TenancyAdministrationStorageException { Failure: TenancyAdministrationStorageFailure.Invariant } => "lockey_business_rule_violation",
            _ => "lockey_dependency_unavailable",
        });
    }

    private async Task<bool> HasCurrentTenantAsync(CancellationToken cancellationToken)
    {
        var tenant = await reader.ReadTenantAsync(cancellationToken);
        return tenant is { IsDeleted: false, Status: TenantStatus.Trial or TenantStatus.Active, DefaultOrganizationId: { } id }
            && tenant.Id == context.TenantId && StronglyTypedId.IsAssigned(id);
    }

    private static PermissionDefinition? Permission(object request) => request switch
    {
        ListOrganizationsQuery or GetOrganizationDetailQuery or ReadOrganizationReceiptQuery or ReadDefaultOrganizationReceiptQuery
            or CreateManagedOrganizationCommand or RenameOrganizationCommand
            or ArchiveOrganizationCommand or SelectDefaultOrganizationCommand => BoundedTenancyAdministrationPermissions.OrganizationAdministration,
        AddManagedTenantLocaleCommand or SetManagedDefaultTenantLocaleCommand or SetTenantLocaleEnabledCommand
            or RemoveTenantLocaleCommand => BoundedTenancyAdministrationPermissions.LocaleWrite,
        SetManagedTenantBrandingCommand => BoundedTenancyAdministrationPermissions.SettingWrite,
        _ => null,
    };
}

internal sealed record TenancyAdministrationAdmission(object Request, OperationInvocation Invocation, IdentityMutationLockPlan Plan)
{
    public override string ToString() => nameof(TenancyAdministrationAdmission);
}
