using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.SharedKernel.Permissions;

/// <summary>One fixed origin-completion check, separate from pristine continuation and human authority.</summary>
public sealed class SeedCompletionReadWitness
{
    private readonly MintingSource _source;
    private readonly IIdentityMutationLocks _locks;
    private readonly ImmutableDictionary<Type, object> _declarations;
    private readonly object _preparedTarget;
    private readonly IUnitOfWork _expectedUnit;
    private readonly DbTransaction _expectedTransaction;
    private readonly long _expectedGeneration;
    private readonly object _bindingGate = new();
    private ReadBinding? _binding;

    private SeedCompletionReadWitness(MintingSource source, OperationInvocation invocation,
        IdentityMutationLockPlan plan, IIdentityMutationLocks locks,
        ImmutableDictionary<Type, object> declarations, object preparedTarget,
        IUnitOfWork unit, DbTransaction transaction, long generation)
    {
        _source = source;
        Invocation = invocation;
        Plan = plan;
        _locks = locks;
        _declarations = declarations;
        _preparedTarget = preparedTarget;
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

    /// <summary>Pure lifetime and captured-pin validation; never enters unit getters or the lock service.</summary>
    public bool IsActive => _source.IsActive && Invocation.IsActive && _source.MatchesCurrent(this);

    /// <summary>The exact owning reader interprets its independently copied immutable declaration.</summary>
    public T GetDeclaration<T>() where T : class =>
        _declarations.TryGetValue(typeof(T), out var declaration) && declaration is T value ? value
            : throw new InvalidOperationException("The fixed seed completion declaration is incompatible.");

    /// <summary>The source and Tenancy bridge alone inspect actual locked target preparation.</summary>
    public T GetPreparedTarget<T>() where T : class => _preparedTarget as T
        ?? throw new InvalidOperationException("The fixed seed completion target is incompatible.");

    /// <summary>Explicitly rechecks the actual held plan outside owner scope-view getters.</summary>
    public void RequireCurrent()
    {
        if (!IsActive) throw new InvalidOperationException("The fixed seed completion source is no longer current.");
        _locks.RequireCurrent(Plan);
        if (!IsActive) throw new InvalidOperationException("The fixed seed completion source is no longer current.");
    }

    /// <summary>Pure expected-owner validation for narrow owning reads; neither binds nor consumes the witness.</summary>
    public bool IsExpectedOn(IUnitOfWork unit, DbTransaction transaction, long generation) =>
        IsActive && generation > 0 && ReferenceEquals(unit, _expectedUnit)
        && ReferenceEquals(transaction, _expectedTransaction) && generation == _expectedGeneration;

    public bool TryBindTo(IUnitOfWork unit, DbTransaction transaction, long generation)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(transaction);
        lock (_bindingGate)
        {
            if (!IsExpectedOn(unit, transaction, generation) || _binding is not null) return false;
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

    public override string ToString() => nameof(SeedCompletionReadWitness);

    private sealed record ReadBinding(IUnitOfWork Unit, DbTransaction Transaction, long Generation);

#pragma warning disable CA1034 // The exact compiled Seeder minter census closes this separate source.
    [SuppressMessage("Design", "CA1063:Implement IDisposable Correctly",
        Justification = "The nonvirtual disposal transition revokes every witness from this confined source.")]
    public abstract class MintingSource : IDisposable
#pragma warning restore CA1034
    {
        private int _closed;
        internal bool IsActive => Volatile.Read(ref _closed) == 0;
        internal bool MatchesCurrent(SeedCompletionReadWitness witness) => IsCurrent(witness);

        /// <summary>The sole Seeder minter supplies its exact fixed declarations and actual prepared target.</summary>
        protected SeedCompletionReadWitness ForCompletionRead(OperationInvocation invocation,
            IdentityMutationLockPlan plan, IIdentityMutationLocks locks,
            ImmutableDictionary<Type, object> declarations, object preparedTarget,
            IUnitOfWork unit, DbTransaction transaction, long generation)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(locks);
            ArgumentNullException.ThrowIfNull(declarations);
            ArgumentNullException.ThrowIfNull(preparedTarget);
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
                || invocation.OrganizationId is not null || invocation.Context.HostScope is not null
                || plan.TenantId != tenant || plan.TenantMode != IdentityTenantLockMode.Exclusive
                || !plan.UserIds.IsEmpty || generation <= 0 || declarations.IsEmpty
                || declarations.Any(pair => pair.Value is null || pair.Key != pair.Value.GetType())
                || !ReferenceEquals(unit.Transaction, transaction)
                || unit.GetTenantContextAnnouncement(transaction) is not { } announced
                || announced.Generation != generation || announced.TenantId != tenant
                || announced.OrganizationId is not null)
                throw new InvalidOperationException("Fixed seed completion authority does not match its invocation.");
            var witness = new SeedCompletionReadWitness(this, invocation, plan, locks, declarations,
                preparedTarget, unit, transaction, generation);
            if (!witness.IsActive)
                throw new InvalidOperationException("The fixed seed completion source does not match its declaration.");
            return witness;
        }

        /// <summary>Checks only source lifetime, exact invocation/request and immutable declaration/target pins.</summary>
        protected abstract bool IsCurrent(SeedCompletionReadWitness witness);

        public void Dispose()
        {
            Interlocked.Exchange(ref _closed, 1);
            GC.SuppressFinalize(this);
        }
    }
}
