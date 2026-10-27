using System.Collections.Immutable;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Roles;

public sealed class ChangeRoleGrantsCommandHandler(IRoleWriteStore store, ITenantContext context, IIdentityMutationLocks locks,
    IOperationInvocationContext invocations, Func<RoleGrantAuthorization> authorization, IAuditSubject subject, IClock clock)
    : RoleGrantCommandHandlerBase(store, context, locks, invocations, authorization, subject, clock),
        IRequestHandler<ChangeRoleGrantsCommand, Result<long>>
{
    public Task<Result<long>> Handle(ChangeRoleGrantsCommand request, CancellationToken cancellationToken) => ExecuteAsync(request, cancellationToken);
}

public sealed class AdoptRoleGrantManifestCommandHandler(IRoleWriteStore store, ITenantContext context, IIdentityMutationLocks locks,
    IOperationInvocationContext invocations, Func<RoleGrantAuthorization> authorization, IAuditSubject subject, IClock clock)
    : RoleGrantCommandHandlerBase(store, context, locks, invocations, authorization, subject, clock),
        IRequestHandler<AdoptRoleGrantManifestCommand, Result<long>>
{
    public Task<Result<long>> Handle(AdoptRoleGrantManifestCommand request, CancellationToken cancellationToken) => ExecuteAsync(request, cancellationToken);
}

public abstract class RoleGrantCommandHandlerBase(IRoleWriteStore store, ITenantContext context, IIdentityMutationLocks locks,
    IOperationInvocationContext invocations, Func<RoleGrantAuthorization> authorization, IAuditSubject subject, IClock clock)
{
    protected async Task<Result<long>> ExecuteAsync(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (invocations.CurrentInvocation is not { IsActive: true, EntryKind: OperationEntryKind.Application } invocation
            || !ReferenceEquals(invocation.Operation, request)
            || invocations.CurrentDecision is not { Outcome: ProtectedOperationOutcome.Allowed, IsActive: true } admitted
            || !ReferenceEquals(admitted.Invocation, invocation)) return Fail("lockey_forbidden");
        var gate = authorization();
        if (!gate.Matches(request, invocation)) return Fail("lockey_forbidden");
        var (rawId, version) = request switch
        {
            ChangeRoleGrantsCommand change => (change.RoleId, change.ExpectedRoleVersion),
            AdoptRoleGrantManifestCommand adopt => (adopt.RoleId, adopt.ExpectedRoleVersion),
            _ => throw new ArgumentException("Role grant request is invalid.", nameof(request)),
        };
        if (!RoleGrantInput.ValidId(rawId) || version < 0) return Fail("lockey_validation_failed");
        if (request is AdoptRoleGrantManifestCommand manifest
            && (manifest.ManifestVersion != IdentityFoundationManifest.Version1.Version
                || manifest.ManifestFingerprint != IdentityFoundationManifest.Version1.Fingerprint)) return Fail("lockey_forbidden");
        if (request is ChangeRoleGrantsCommand { Grants.IsDefault: true }) return Fail("lockey_validation_failed");
        try
        {
            var id = RoleId.From(rawId);
            var before = await store.FindAsync(id, cancellationToken);
            if (!Visible(before)) return Fail("lockey_not_found");
            if (before!.Version != version) return Fail("lockey_concurrency_conflict");
            var desired = Desired(request, before);
            var check = await gate.RecheckAsync(request, invocation, before, desired, cancellationToken);
            if (check.Decision.Outcome != ProtectedOperationOutcome.Allowed) return Refused(gate, check);
            var plan = new IdentityMutationLockPlan([], context.TenantId, RequiredMode(before, desired));
            await locks.AcquireAsync(plan, cancellationToken);
            var target = await store.FindAsync(id, cancellationToken);
            if (!Visible(target)) return Fail("lockey_not_found");
            if (target!.Version != version || target.BuiltInKey != before.BuiltInKey || target.PermissionScope != before.PermissionScope)
                return Fail("lockey_concurrency_conflict");
            desired = Desired(request, target);
            if (RequiredMode(target, desired) == IdentityTenantLockMode.Exclusive && plan.TenantMode != IdentityTenantLockMode.Exclusive)
                return Fail("lockey_forbidden");
            check = await gate.RecheckAsync(request, invocation, target, desired, cancellationToken);
            if (check.Decision.Outcome != ProtectedOperationOutcome.Allowed) return Refused(gate, check);
            locks.RequireCurrent(plan);
            gate.ReportRecheck(check);
            subject.Designate(target);
            var changed = target.ReplaceGrants(desired, clock.UtcNow, context.UserId!.Value);
            if (changed.IsFailure) return Fail("lockey_forbidden");
            if (target.Version != version)
            {
                await store.UpdateAsync(target, cancellationToken);
                await store.ValidateGraphAsync(cancellationToken);
            }
            return Result<long>.Ok(target.Version);
        }
        catch (IdentityFoundationStorageException failure)
        {
            return Fail(failure.Failure == IdentityFoundationStorageFailure.Conflict ? "lockey_concurrency_conflict"
                : failure.Failure == IdentityFoundationStorageFailure.Plan ? "lockey_dependency_unavailable" : "lockey_forbidden");
        }
    }

    private bool Visible(Role? role) => role is { DeletedAt: null, DeletedBy: null } && role.TenantId == context.TenantId
        && IdentityFoundationManifest.Version1.FindTemplate(role.BuiltInKey, role.PermissionScope) is not null;

    private static ImmutableArray<PermissionDefinition> Desired(object request, Role role) => request switch
    {
        ChangeRoleGrantsCommand change => change.Grants,
        AdoptRoleGrantManifestCommand => IdentityFoundationManifest.Version1.FindTemplate(role.BuiltInKey, role.PermissionScope)!.Grants,
        _ => throw new ArgumentException("Role grant request is invalid.", nameof(request)),
    };

    private static IdentityTenantLockMode RequiredMode(Role target, IEnumerable<PermissionDefinition> desired) =>
        target.BuiltInKey == BuiltInRoleKey.TenantAdmin || target.Grants.Any(grant => grant.PermissionKey == IdentityFoundationManifest.RoleAdmin)
            || desired.Any(definition => definition?.Key == IdentityFoundationManifest.RoleAdmin)
                ? IdentityTenantLockMode.Exclusive : IdentityTenantLockMode.Shared;

    private static Result<long> Refused(RoleGrantAuthorization gate, RoleGrantAssessment check)
    {
        if (check.Decision.Outcome is ProtectedOperationOutcome.NotCovered or ProtectedOperationOutcome.Forbidden) gate.ReportRefusal(check);
        return Fail(check.Decision.Outcome switch
        {
            ProtectedOperationOutcome.NotCovered => "lockey_not_found",
            ProtectedOperationOutcome.Forbidden => "lockey_forbidden",
            _ => "lockey_dependency_unavailable",
        });
    }

    private static Result<long> Fail(string key) => Result<long>.Fail(new Error(LocalizedMessage.Of(key)));
}
