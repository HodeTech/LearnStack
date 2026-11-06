using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Identity.Application.Contracts.Invitations;

/// <summary>A fresh provider observation for this exact application invocation, never an email lookup.</summary>
public interface ICurrentInvitationRecipientReader
{
    Task<Result<CurrentInvitationRecipientEvidence>> ReadAsync(OperationInvocation invocation,
        CancellationToken cancellationToken = default);
}

/// <summary>Ephemeral verified provider claims; the handler independently applies the canonical mailbox policy.</summary>
public sealed class CurrentInvitationRecipientEvidence
{
    private readonly MintingSource _source;
    private readonly OperationInvocation _invocation;
    private readonly object _observation;

    private CurrentInvitationRecipientEvidence(MintingSource source, OperationInvocation invocation,
        string email, object observation)
    {
        _source = source;
        _invocation = invocation;
        _observation = observation;
        Email = email;
    }

    [PiiSensitive]
    public string Email { get; }
    public bool IsFor(OperationInvocation invocation) => ReferenceEquals(_invocation, invocation)
        && invocation.IsActive && _source.IsCurrent(_observation, invocation);
    public override string ToString() => nameof(CurrentInvitationRecipientEvidence);

#pragma warning disable CA1034 // Private construction and live provider-source binding confine this evidence.
    [SuppressMessage("Design", "CA1063:Implement IDisposable Correctly",
        Justification = "Disposal closes the evidence source and cannot be overridden.")]
    public abstract class MintingSource : IDisposable
#pragma warning restore CA1034
    {
        private int _closed;
        protected bool IsSourceOpen => Volatile.Read(ref _closed) == 0;
        internal bool IsCurrent(object observation, OperationInvocation invocation) => IsSourceOpen
            && IsObservationCurrent(observation, invocation);
        protected abstract bool IsObservationCurrent(object observation, OperationInvocation invocation);

        protected CurrentInvitationRecipientEvidence? Capture(OperationInvocation invocation, string email, object observation)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            ArgumentNullException.ThrowIfNull(email);
            ArgumentNullException.ThrowIfNull(observation);
            if (!invocation.IsActive || !IsCurrent(observation, invocation)) return null;
            return new(this, invocation, email, observation);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _closed, 1);
            GC.SuppressFinalize(this);
        }
    }
}
