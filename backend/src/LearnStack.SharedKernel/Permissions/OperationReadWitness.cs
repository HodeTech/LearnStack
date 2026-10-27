using System.Data.Common;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.SharedKernel.Permissions;

/// <summary>Immutable tenant-read authority; consumption state belongs to its exact invocation owner.</summary>
public sealed class OperationReadWitness
{
    private readonly OperationInvocation _invocation;
    private readonly AuthorizationCandidate _candidate;

    private OperationReadWitness(OperationInvocation invocation, AuthorizationCandidate candidate)
    {
        _invocation = invocation;
        _candidate = candidate;
    }

    public UserId Actor => _candidate.Actor;
    public TenantId TenantId => _candidate.TenantId;
    public OrganizationId? OrganizationId => _candidate.OrganizationId;
    public OperationPolicy Policy => _invocation.Policy;
    public OperationInvocation Invocation => _invocation;
    public bool IsActive => _invocation.IsActive && _candidate.IsActive;

    /// <summary>Only the actual ordinary transaction owner consumes this once, before announcement.</summary>
    public bool TryBindTo(IUnitOfWork unit, DbTransaction transaction, long generation) =>
        IsActive && _invocation.TryBindRead(unit, transaction, generation);

    public bool IsBoundTo(IUnitOfWork unit, DbTransaction transaction, long generation) =>
        IsActive && _invocation.IsReadBoundTo(unit, transaction, generation);

    public override string ToString() => nameof(OperationReadWitness);

#pragma warning disable CA1034 // The decision minter alone owns read-witness creation; exact callers are counted.
    public abstract class MintingSource
#pragma warning restore CA1034
    {
        protected static OperationReadWitness ForTenantRead(OperationInvocation invocation, AuthorizationCandidate candidate)
        {
            if (!invocation.IsActive || !candidate.IsActive || !ReferenceEquals(invocation.Candidate, candidate)
                || invocation.Policy is not
                {
                    Kind: OperationPolicyKind.Protected, ReadScope: OperationReadScope.Tenant,
                    RequiredPermission.Scope: PermissionScope.Tenant
                }
                || invocation.Actor != candidate.Actor || invocation.TenantId != candidate.TenantId
                || invocation.OrganizationId != candidate.OrganizationId)
                throw new InvalidOperationException("Operation read authority does not match its invocation.");
            return new(invocation, candidate);
        }
    }
}

/// <summary>Live physical-owner state only, never a writable context or request flag.</summary>
public interface IOperationReadScopeView
{
    bool IsTenantWideRead { get; }
}
