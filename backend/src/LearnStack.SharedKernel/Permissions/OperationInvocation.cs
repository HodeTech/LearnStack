using System.Data.Common;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.Permissions;

public enum OperationEntryKind { Application = 1, Http = 2 }

/// <summary>One exact policy invocation; its private owner supplies lifetime and current-frame authority.</summary>
public sealed class OperationInvocation
{
    private readonly MintingSource _source;
    private readonly CancellationToken _cancellationToken;
    private readonly object _bindingGate = new();
    private ReadBinding? _readBinding;
    private readonly bool _resolved;
    private readonly TenantContextOrigin? _origin;
    private readonly HostScope? _hostScope;

    private OperationInvocation(MintingSource source, object operation, OperationPolicy policy,
        ITenantContext context, AuthorizationCandidate? candidate, TrustedOperationDescriptor? trustedSource,
        OperationEntryKind entryKind, CancellationToken cancellationToken)
    {
        _source = source;
        Operation = operation;
        Policy = policy;
        Context = context;
        Candidate = candidate;
        TrustedSource = trustedSource;
        _cancellationToken = cancellationToken;
        EntryKind = entryKind;
        _resolved = context.IsResolved;
        TenantId = _resolved ? context.TenantId : null;
        OrganizationId = _resolved ? context.OrganizationId : null;
        Actor = context.UserId;
        _origin = context.Origin;
        _hostScope = context.HostScope;
    }

    public object Operation { get; }
    public OperationEntryKind EntryKind { get; }
    public OperationPolicy Policy { get; }
    public ITenantContext Context { get; }
    public TenantId? TenantId { get; }
    public OrganizationId? OrganizationId { get; }
    public UserId? Actor { get; }
    public AuthorizationCandidate? Candidate { get; }
    public TrustedOperationDescriptor? TrustedSource { get; }
    public bool IsActive => !_cancellationToken.IsCancellationRequested && _source.MatchesCurrent(this)
        && (Candidate?.IsActive ?? true) && (TrustedSource?.IsActive ?? true)
        && Context.IsResolved == _resolved && Context.UserId == Actor && Context.Origin == _origin
        && Context.HostScope == _hostScope
        && (!_resolved || (Context.TenantId == TenantId && Context.OrganizationId == OrganizationId));

    internal bool TryBindRead(IUnitOfWork unit, DbTransaction transaction, long generation)
    {
        lock (_bindingGate)
        {
            // The enumerated physical owner validates its private transaction and
            // generation. Calling its public getters here would recurse through
            // that same owner's live-witness validation.
            if (!IsActive || _readBinding is not null || generation <= 0) return false;
            _readBinding = new ReadBinding(unit, transaction, generation);
            return true;
        }
    }

    internal bool IsReadBoundTo(IUnitOfWork unit, DbTransaction transaction, long generation)
    {
        lock (_bindingGate)
            return IsActive && _readBinding is { } binding && ReferenceEquals(binding.Unit, unit)
                && ReferenceEquals(binding.Transaction, transaction) && binding.Generation == generation;
    }

    public override string ToString() => nameof(OperationInvocation);

    private sealed record ReadBinding(IUnitOfWork Unit, DbTransaction Transaction, long Generation);

#pragma warning disable CA1034 // A closed compiled source census owns invocation construction.
    public abstract class MintingSource
#pragma warning restore CA1034
    {
        internal bool MatchesCurrent(OperationInvocation invocation) => IsCurrent(invocation);

        protected OperationInvocation ForInvocation(object operation, OperationPolicy policy, ITenantContext context,
            AuthorizationCandidate? candidate, TrustedOperationDescriptor? trustedSource,
            CancellationToken cancellationToken, OperationEntryKind entryKind = OperationEntryKind.Application)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(context);
            if ((candidate is not null && trustedSource is not null) || !Enum.IsDefined(entryKind)
                || (entryKind == OperationEntryKind.Application && operation.GetType() != policy.RequestType))
                throw new ArgumentException("Operation invocation source is ambiguous.");
            return new(this, operation, policy, context, candidate, trustedSource, entryKind, cancellationToken);
        }

        protected abstract bool IsCurrent(OperationInvocation invocation);
    }
}
