using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.Identity;

/// <summary>Finite database observation; this tuple alone grants no authority.</summary>
public sealed record InvitationEntryObservation(Guid InvitationId, TenantId TenantId, OrganizationId? OrganizationId,
    long Version, Guid GenerationId, long Generation, UserId Actor, bool InviterGloballyAdmitted,
    bool Accepted, Guid? ResultMembershipId, UserId? AcceptedBy, string? Disposition);

public interface IInvitationEntryScopeSource
{
    InvitationEntryScopeWitness? ForInvocation(OperationInvocation invocation);
}

/// <summary>One exact source, operation and physical owner; no Membership or tenant-wide read is implied.</summary>
public sealed class InvitationEntryScopeWitness
{
    private readonly MintingSource _source;
    private readonly object _evidence;
    private readonly object _gate = new();
    private (IUnitOfWork Unit, DbTransaction Transaction, long Generation)? _binding;

    private InvitationEntryScopeWitness(MintingSource source, object evidence, OperationInvocation invocation,
        InvitationEntryObservation offer, GlobalIdentitySubject subject, string digest, TransactionMode mode, string host)
    {
        if (offer.InvitationId.Version != 7 || offer.GenerationId.Version != 7 || offer.Version < 0 || offer.Generation <= 0
            || !StronglyTypedId.IsAssigned(offer.Actor) || offer.Actor == UserId.SystemActor
            || !StronglyTypedId.IsAssigned(offer.TenantId) || offer.TenantId == TenantId.PlatformSentinel
            || !StronglyTypedId.IsAssignedWhenPresent(offer.OrganizationId)
            || digest.Length != 64 || !digest.All(char.IsAsciiHexDigitLower)
            || mode is not (TransactionMode.ReadOnly or TransactionMode.ReadWrite)
            || string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Invitation provenance refused.");
        _source = source; _evidence = evidence; Invocation = invocation; Offer = offer;
        AuthorityId = subject.AuthorityId; SubjectKey = subject.SubjectKey; VerifierDigest = digest; Mode = mode; Host = host;
    }
    public OperationInvocation Invocation { get; }
    public InvitationEntryObservation Offer { get; }
    public IdentityAuthorityId AuthorityId { get; }
    [PiiSensitive] public IdentitySubjectKey SubjectKey { get; }
    [PiiSensitive] public string VerifierDigest { get; }
    public TransactionMode Mode { get; }
    public string Host { get; }
    public bool IsActive => _source.Current(_evidence, Invocation) && Invocation.IsActive
        && Invocation.Policy.Kind == OperationPolicyKind.InvitationEntry
        && Invocation.Context.Origin == TenantContextOrigin.HostAndInvitationOffer
        && Invocation.Actor == Offer.Actor && Invocation.TenantId == Offer.TenantId
        && Invocation.OrganizationId == Offer.OrganizationId;
    public bool TryBindTo(IUnitOfWork unit, DbTransaction transaction, long generation, TransactionMode mode)
    {
        lock (_gate)
        {
            if (!IsActive || Invocation.EntryKind != OperationEntryKind.Application || _binding is not null
                || generation <= 0 || mode != Mode) return false;
            _binding = (unit, transaction, generation); return true;
        }
    }
    public bool IsBoundTo(IUnitOfWork unit, DbTransaction transaction, long generation) => IsActive
        && _binding is { } binding && ReferenceEquals(binding.Unit, unit)
        && ReferenceEquals(binding.Transaction, transaction) && binding.Generation == generation;
    public override string ToString() => nameof(InvitationEntryScopeWitness);

#pragma warning disable CA1034 // The exact API source census closes private witness construction.
    [SuppressMessage("Design", "CA1063:Implement IDisposable Correctly", Justification = "Nonvirtual disposal closes captured provenance.")]
    public abstract class MintingSource : IDisposable
#pragma warning restore CA1034
    {
        private int _closed;
        internal bool Current(object evidence, OperationInvocation invocation) => Volatile.Read(ref _closed) == 0
            && IsEvidenceCurrent(evidence, invocation);
        protected abstract bool IsEvidenceCurrent(object evidence, OperationInvocation invocation);
        protected InvitationEntryScopeWitness Capture(object evidence, OperationInvocation invocation,
            InvitationEntryObservation offer, GlobalIdentitySubject subject, string digest, TransactionMode mode, string host)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            return new(this, evidence, invocation, offer, subject, digest, mode, host);
        }
        public void Dispose() { Interlocked.Exchange(ref _closed, 1); GC.SuppressFinalize(this); }
    }
}
