using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.Identity;

public enum ConsentSubjectPurpose { Discover = 1, History = 2, Withdraw = 3 }

public sealed record ConsentSubjectScopeObservation(Guid MembershipId, TenantId TenantId, OrganizationId? OrganizationId);
public sealed record ConsentSubjectScopePage(ImmutableArray<ConsentSubjectScopeObservation> Items, string? NextCursor);

public interface IConsentSubjectScopeSource
{
    ConsentSubjectScopeWitness? ForInvocation(OperationInvocation invocation);
}

/// <summary>One verified subject/real host and closed consent purpose; no ordinary Membership authority.</summary>
public sealed class ConsentSubjectScopeWitness
{
    private readonly MintingSource _source;
    private readonly object _evidence;
    private readonly object _gate = new();
    private (IUnitOfWork Unit, DbTransaction Transaction, long Generation)? _binding;

    private ConsentSubjectScopeWitness(MintingSource source, object evidence, OperationInvocation invocation,
        GlobalIdentitySubject subject, UserId actor, TenantId hostTenant, OrganizationId? hostOrganization,
        string host, ConsentSubjectPurpose purpose, ConsentSubjectScopeObservation? selected,
        ConsentSubjectScopePage? scopePage)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (!StronglyTypedId.IsAssigned(actor) || actor == UserId.SystemActor
            || !StronglyTypedId.IsAssigned(hostTenant) || hostTenant == TenantId.PlatformSentinel
            || !StronglyTypedId.IsAssignedWhenPresent(hostOrganization) || string.IsNullOrWhiteSpace(host)
            || !Enum.IsDefined(purpose) || (purpose == ConsentSubjectPurpose.Discover) != (scopePage is not null)
            || (purpose == ConsentSubjectPurpose.Discover) != (selected is null))
            throw new ArgumentException("Consent subject provenance refused.");
        bool WithinHost(ConsentSubjectScopeObservation scope) => scope.MembershipId != Guid.Empty
            && scope.MembershipId.Version == 7 && scope.TenantId == hostTenant
            && StronglyTypedId.IsAssignedWhenPresent(scope.OrganizationId)
            && (hostOrganization is null || scope.OrganizationId == hostOrganization);
        if (selected is not null && !WithinHost(selected)
            || scopePage is not null && (scopePage.Items.IsDefault || scopePage.Items.Length > 20
                || scopePage.Items.Any(scope => scope is null || !WithinHost(scope))
                || scopePage.Items.Select(scope => scope.MembershipId).Distinct().Count() != scopePage.Items.Length
                || scopePage.NextCursor is { Length: > 1024 }))
            throw new ArgumentException("Consent subject scope refused.");
        _source = source; _evidence = evidence; Invocation = invocation; Actor = actor;
        AuthorityId = subject.AuthorityId; SubjectKey = subject.SubjectKey; Host = host;
        HostTenantId = hostTenant; HostOrganizationId = hostOrganization; Purpose = purpose;
        TenantId = selected?.TenantId ?? hostTenant;
        OrganizationId = selected?.OrganizationId ?? hostOrganization;
        MembershipId = selected?.MembershipId; ScopePage = scopePage;
    }

    public OperationInvocation Invocation { get; }
    public UserId Actor { get; }
    public IdentityAuthorityId AuthorityId { get; }
    [PiiSensitive] public IdentitySubjectKey SubjectKey { get; }
    public TenantId TenantId { get; }
    public OrganizationId? OrganizationId { get; }
    public TenantId HostTenantId { get; }
    public OrganizationId? HostOrganizationId { get; }
    public Guid? MembershipId { get; }
    public string Host { get; }
    public ConsentSubjectPurpose Purpose { get; }
    public ConsentSubjectScopePage? ScopePage { get; }
    public TransactionMode Mode => Purpose == ConsentSubjectPurpose.Withdraw ? TransactionMode.ReadWrite : TransactionMode.ReadOnly;
    public bool IsActive => _source.Current(_evidence, Invocation) && Invocation.IsActive
        && Invocation.Policy.Kind == OperationPolicyKind.OwnConsentEntry
        && Invocation.Context.Origin == TenantContextOrigin.HostAndConsentSubjectScope
        && Invocation.Actor == Actor && Invocation.TenantId == TenantId && Invocation.OrganizationId == OrganizationId
        && Invocation.Context.HostScope is { } host && host.TenantId == HostTenantId && host.OrganizationId == HostOrganizationId;

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
        && _binding is { } bound && ReferenceEquals(bound.Unit, unit) && ReferenceEquals(bound.Transaction, transaction)
        && bound.Generation == generation;
    public override string ToString() => nameof(ConsentSubjectScopeWitness);

#pragma warning disable CA1034 // A closed source census owns the sole production minter.
    [SuppressMessage("Design", "CA1063:Implement IDisposable Correctly", Justification = "Nonvirtual disposal closes captured provenance.")]
    public abstract class MintingSource : IDisposable
#pragma warning restore CA1034
    {
        private int _closed;
        internal bool Current(object evidence, OperationInvocation invocation) => Volatile.Read(ref _closed) == 0
            && IsEvidenceCurrent(evidence, invocation);
        protected abstract bool IsEvidenceCurrent(object evidence, OperationInvocation invocation);
        protected ConsentSubjectScopeWitness Capture(object evidence, OperationInvocation invocation,
            GlobalIdentitySubject subject, UserId actor, TenantId hostTenant, OrganizationId? hostOrganization,
            string host, ConsentSubjectPurpose purpose, ConsentSubjectScopeObservation? selected, ConsentSubjectScopePage? page)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            return new(this, evidence, invocation, subject, actor, hostTenant, hostOrganization, host, purpose, selected, page);
        }
        public void Dispose() { Interlocked.Exchange(ref _closed, 1); GC.SuppressFinalize(this); }
    }
}
