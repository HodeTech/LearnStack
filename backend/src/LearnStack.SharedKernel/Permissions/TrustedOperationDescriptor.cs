using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.Permissions;

/// <summary>Captures only the current finite seed, delivery or job owner.</summary>
public interface ITrustedOperationSource
{
    TrustedOperationDescriptor? Capture(Type requestType, ITenantContext context);
}

/// <summary>Closed, lifetime-bound evidence for one exact finite application operation.</summary>
public sealed class TrustedOperationDescriptor
{
    private readonly MintingSource _source;
    private readonly ITenantContext _context;
    private readonly object? _evidence;

    private TrustedOperationDescriptor(MintingSource source, AuthorizationSourceKind kind,
        Type requestType, ITenantContext context, object? evidence)
    {
        if (kind is not (AuthorizationSourceKind.Seed or AuthorizationSourceKind.IntegrationEvent
            or AuthorizationSourceKind.BackgroundJob) || requestType.IsAbstract || requestType.ContainsGenericParameters)
            throw new ArgumentException("Trusted operation provenance is invalid.");
        _source = source;
        _context = context;
        _evidence = evidence;
        SourceKind = kind;
        RequestType = requestType;
    }

    public AuthorizationSourceKind SourceKind { get; }
    public Type RequestType { get; }
    public bool IsActive => _source.IsActive && _source.MatchesEvidence(_evidence);

    public bool Matches(Type requestType, ITenantContext context) =>
        RequestType == requestType && ReferenceEquals(_context, context) && IsActive;

    public override string ToString() => nameof(TrustedOperationDescriptor);

#pragma warning disable CA1034 // Private construction and the exact compiled minter/caller census close the source set.
    [SuppressMessage("Design", "CA1063:Implement IDisposable Correctly",
        Justification = "Disposal revokes the shared provenance lifetime; derived sources cannot replace that transition.")]
    public abstract class MintingSource : IDisposable
#pragma warning restore CA1034
    {
        private int _closed;
        internal bool IsActive => Volatile.Read(ref _closed) == 0;
        protected bool CanIssue => IsActive;
        internal bool MatchesEvidence(object? evidence) => IsEvidenceCurrent(evidence);
        protected abstract bool IsEvidenceCurrent(object? evidence);

        protected TrustedOperationDescriptor ForOperation(AuthorizationSourceKind kind,
            Type requestType, ITenantContext context, object? evidence = null)
        {
            ArgumentNullException.ThrowIfNull(requestType);
            ArgumentNullException.ThrowIfNull(context);
            ObjectDisposedException.ThrowIf(!IsActive, this);
            return new(this, kind, requestType, context, evidence);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _closed, 1);
            GC.SuppressFinalize(this);
        }
    }
}
