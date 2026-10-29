using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.SharedKernel.Permissions;

/// <summary>One closed fixed-seed prerequisite check, distinct from human read authority.</summary>
public sealed class SeedPrerequisiteReadWitness
{
    private readonly MintingSource _source;
    private readonly IIdentityMutationLocks _locks;
    private readonly object _declaration;
    private readonly IUnitOfWork _expectedUnit;
    private readonly DbTransaction _expectedTransaction;
    private readonly long _expectedGeneration;
    private readonly object _bindingGate = new();
    private ReadBinding? _binding;

    private SeedPrerequisiteReadWitness(MintingSource source, OperationInvocation invocation,
        IdentityMutationLockPlan plan, IIdentityMutationLocks locks, object declaration,
        IUnitOfWork unit, DbTransaction transaction, long generation)
    {
        _source = source;
        Invocation = invocation;
        Plan = plan;
        _locks = locks;
        _declaration = declaration;
        _expectedUnit = unit;
        _expectedTransaction = transaction;
        _expectedGeneration = generation;
        TenantId = invocation.TenantId!.Value;
        OrganizationId = invocation.OrganizationId;
    }

    public OperationInvocation Invocation { get; }
    public IdentityMutationLockPlan Plan { get; }
    public TenantId TenantId { get; }
    public OrganizationId? OrganizationId { get; }

    /// <summary>Pure lifetime/pin validation: never consults owner getters or lock services.</summary>
    public bool IsActive => _source.IsActive && Invocation.IsActive && _source.MatchesCurrent(this);

    /// <summary>The closed reader alone interprets the independently captured immutable declaration.</summary>
    public T GetDeclaration<T>() where T : class => _declaration as T
        ?? throw new InvalidOperationException("The fixed seed prerequisite declaration is incompatible.");

    /// <summary>Explicitly rechecks the actual held plan; do not call from an owner scope getter.</summary>
    public void RequireCurrent()
    {
        if (!IsActive) throw new InvalidOperationException("The fixed seed prerequisite source is no longer current.");
        _locks.RequireCurrent(Plan);
        if (!IsActive) throw new InvalidOperationException("The fixed seed prerequisite source is no longer current.");
    }

    public bool TryBindTo(IUnitOfWork unit, DbTransaction transaction, long generation)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(transaction);
        lock (_bindingGate)
        {
            if (!IsActive || _binding is not null || generation <= 0
                || !ReferenceEquals(unit, _expectedUnit) || !ReferenceEquals(transaction, _expectedTransaction)
                || generation != _expectedGeneration) return false;
            _binding = new(unit, transaction, generation);
            return true;
        }
    }

    public bool IsBoundTo(IUnitOfWork unit, DbTransaction transaction, long generation)
    {
        lock (_bindingGate)
            return IsActive && _binding is { } binding && ReferenceEquals(binding.Unit, unit)
                && ReferenceEquals(binding.Transaction, transaction) && binding.Generation == generation;
    }

    public override string ToString() => nameof(SeedPrerequisiteReadWitness);

    private sealed record ReadBinding(IUnitOfWork Unit, DbTransaction Transaction, long Generation);

#pragma warning disable CA1034 // The exact compiled Seeder minter census closes this source.
    [SuppressMessage("Design", "CA1063:Implement IDisposable Correctly",
        Justification = "The nonvirtual disposal transition revokes every witness from this confined source.")]
    public abstract class MintingSource : IDisposable
#pragma warning restore CA1034
    {
        private int _closed;
        internal bool IsActive => Volatile.Read(ref _closed) == 0;
        internal bool MatchesCurrent(SeedPrerequisiteReadWitness witness) => IsCurrent(witness);

        /// <summary>The sole Seeder minter supplies canonical policy, exact request values and immutable fixed pins.</summary>
        protected SeedPrerequisiteReadWitness ForPrerequisiteRead(OperationInvocation invocation,
            IdentityMutationLockPlan plan, IIdentityMutationLocks locks, object declaration,
            IUnitOfWork unit, DbTransaction transaction, long generation)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(locks);
            ArgumentNullException.ThrowIfNull(declaration);
            ArgumentNullException.ThrowIfNull(unit);
            ArgumentNullException.ThrowIfNull(transaction);
            ObjectDisposedException.ThrowIf(!IsActive, this);
            if (!invocation.IsActive || invocation.EntryKind != OperationEntryKind.Application
                || invocation.Candidate is not null || invocation.Policy.Kind != OperationPolicyKind.FiniteTrusted
                || invocation.Operation.GetType() != invocation.Policy.RequestType
                || invocation.Policy.AllowedSources.Length != 1
                || invocation.Policy.AllowedSources[0] != AuthorizationSourceKind.Seed
                || invocation.TrustedSource is not { SourceKind: AuthorizationSourceKind.Seed } source
                || !source.Matches(invocation.Operation.GetType(), invocation.Context)
                || !invocation.Context.IsResolved || invocation.TenantId is not { } tenant
                || !tenant.IsInitialized() || tenant.Value == Guid.Empty || tenant == TenantId.PlatformSentinel
                || invocation.Context.HostScope is not null || plan.TenantId != tenant
                || plan.TenantMode != IdentityTenantLockMode.Exclusive || generation <= 0
                || !ReferenceEquals(unit.Transaction, transaction)
                || unit.GetTenantContextAnnouncement(transaction) is not { } announced
                || announced.Generation != generation || announced.TenantId != tenant
                || announced.OrganizationId != invocation.OrganizationId)
                throw new InvalidOperationException("Fixed seed prerequisite authority does not match its invocation.");
            var witness = new SeedPrerequisiteReadWitness(this, invocation, plan, locks, declaration, unit, transaction, generation);
            if (!witness.IsActive)
                throw new InvalidOperationException("The fixed seed prerequisite source does not match its declaration.");
            return witness;
        }

        /// <summary>Checks only the source lifetime, exact invocation/request and immutable declaration pins.</summary>
        protected abstract bool IsCurrent(SeedPrerequisiteReadWitness witness);

        public void Dispose()
        {
            Interlocked.Exchange(ref _closed, 1);
            GC.SuppressFinalize(this);
        }
    }
}
