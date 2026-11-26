using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.Permissions;

/// <summary>Proof that one operator act is current on its own owner and holds its ordered locks.</summary>
public sealed class OperatorActAdmission
{
    internal OperatorActAdmission(OperationInvocation invocation, object request, TenantId tenantId, IdentityMutationLockPlan plan)
    {
        Invocation = invocation;
        Request = request;
        TenantId = tenantId;
        Plan = plan;
    }

    /// <summary>The invocation the act runs under.</summary>
    public OperationInvocation Invocation { get; }

    /// <summary>The exact request object.</summary>
    public object Request { get; }

    /// <summary>The tenant the operator named and the context carries.</summary>
    public TenantId TenantId { get; }

    /// <summary>The ordered lock plan held for the act.</summary>
    public IdentityMutationLockPlan Plan { get; }

    /// <inheritdoc />
    public override string ToString() => nameof(OperatorActAdmission);
}

/// <summary>
/// The handler-side check of an operator act, the counterpart of the seed guards (ADR-0102 § 1, § 7).
/// </summary>
/// <remarks>
/// <para>
/// The pipeline has already refused any request whose policy does not list the operator source and
/// whose descriptor was not minted for an admitted act. This guard restates that on the handler's own
/// owner, so a handler can be called only from that pipeline: the invocation is the live one for this
/// exact request object and context, its policy is finite and names the operator source alone, its
/// descriptor still matches, the context is the ambient one the executor installed for the tenant the
/// act names (no host scope, no user, and no organization unless the act is a closure's fan-out), and the
/// transaction is the single ordinary read-write owner that announced exactly that tenant and organization.
/// </para>
/// <para>
/// <b>The one organization.</b> ADR-0102 Amendment 2 extends the confined announcement to a group's
/// organization for <c>fan_out_closure</c> alone: a context that carries an organization is admitted only
/// when the operator descriptor was minted for that operation, by reference, and the announcement names the
/// same organization. Every onboarding act still runs with none.
/// </para>
/// <para>
/// It then takes the ordered locks of the plan before any read the act decides on, and
/// <see cref="RequireCurrent"/> asks again before each write, as the seed guards do.
/// </para>
/// </remarks>
public sealed class OperatorActGuard(
    IOperationInvocationContext execution,
    ITenantContext context,
    IUnitOfWork unit,
    IIdentityMutationLocks locks)
{
    private int _replayed;

    /// <summary>
    /// Whether the act found its own earlier result and wrote nothing, so the executor can count a retry
    /// apart from a first run (ADR-0102 § 11).
    /// </summary>
    public bool Replayed => Volatile.Read(ref _replayed) != 0;

    /// <summary>Records that the act is answering from state a previous run of the same act wrote.</summary>
    public void MarkReplayed() => Volatile.Write(ref _replayed, 1);

    /// <summary>
    /// Whether <paramref name="request"/> is the live operator act in its own context and transaction, with no lock
    /// taken and nothing read, for a handler that must not touch a confined read before it knows it is one.
    /// </summary>
    public Result<None> RequireLiveAct(object request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invocation = execution.CurrentInvocation;
        return context.IsResolved && HasInvocation(invocation, request) && HasOwner(invocation!, context.TenantId)
            ? Result.Ok(None.Value)
            : RefusedNone();
    }

    /// <summary>Checks the act and acquires the plan; a refusal is a Problem Details-ready failure.</summary>
    public async Task<Result<OperatorActAdmission>> AdmitAsync(
        object request, IdentityMutationLockPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        var invocation = execution.CurrentInvocation;
        if (!HasInvocation(invocation, request) || !HasOwner(invocation!, plan.TenantId))
        {
            return Refused();
        }

        var admission = new OperatorActAdmission(invocation!, request, plan.TenantId, plan);
        try
        {
            await locks.AcquireAsync(plan, cancellationToken);
        }
        catch (IdentityMutationLockException)
        {
            unit.MarkRollbackOnly();
            return Unavailable();
        }

        return RequireCurrent(admission) is { IsFailure: true } failure
            ? Result<OperatorActAdmission>.Fail(failure.Error)
            : Result.Ok(admission);
    }

    /// <summary>Asks again, before a write, whether the act is still the live one and its locks still held.</summary>
    public Result<None> RequireCurrent(OperatorActAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (!ReferenceEquals(execution.CurrentInvocation, admission.Invocation)
            || !HasInvocation(admission.Invocation, admission.Request) || !HasOwner(admission.Invocation, admission.TenantId))
        {
            return RefusedNone();
        }

        try
        {
            locks.RequireCurrent(admission.Plan);
            return Result.Ok(None.Value);
        }
        catch (IdentityMutationLockException)
        {
            unit.MarkRollbackOnly();
            return Result<None>.Fail(new Error(new LocalizedMessage("lockey_dependency_unavailable")));
        }
    }

    private bool HasInvocation(OperationInvocation? invocation, object request) => invocation is { IsActive: true, EntryKind: OperationEntryKind.Application }
        && ReferenceEquals(invocation.Operation, request) && ReferenceEquals(invocation.Context, context)
        && invocation.Policy.RequestType == request.GetType() && invocation.Policy.Kind == OperationPolicyKind.FiniteTrusted
        && invocation.Policy.AllowedSources is [AuthorizationSourceKind.PlatformOperator]
        && invocation.TrustedSource is { SourceKind: AuthorizationSourceKind.PlatformOperator } source
        && source.Matches(request.GetType(), context);

    private bool HasOwner(OperationInvocation invocation, TenantId tenantId) => context.IsResolved && context.TenantId == tenantId
        && (context.OrganizationId is null || AnnouncesOrganization(invocation))
        && context.HostScope is null && context.UserId is null
        && unit.HasActiveTransaction && !unit.IsRollbackOnly && unit.Mode == TransactionMode.ReadWrite
        && unit.Transaction is not null && unit is not IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
        && unit.GetTenantContextAnnouncement(unit.Transaction) is { Generation: > 0 } announcement
        && announcement.TenantId == tenantId && announcement.OrganizationId == context.OrganizationId;

    /// <summary>
    /// Whether the act may run in an organization: only a closure's fan-out, by the operation its operator descriptor
    /// was minted for (ADR-0102 Amendment 2).
    /// </summary>
    private static bool AnnouncesOrganization(OperationInvocation invocation) =>
        invocation.TrustedSource?.Operator?.Operation is { } operation
        && ReferenceEquals(operation, PlatformOperations.ClosureFanOut);

    private static Result<OperatorActAdmission> Refused() =>
        Result<OperatorActAdmission>.Fail(new Error(new LocalizedMessage("lockey_forbidden")));

    private static Result<OperatorActAdmission> Unavailable() =>
        Result<OperatorActAdmission>.Fail(new Error(new LocalizedMessage("lockey_dependency_unavailable")));

    private static Result<None> RefusedNone() => Result<None>.Fail(new Error(new LocalizedMessage("lockey_forbidden")));
}
