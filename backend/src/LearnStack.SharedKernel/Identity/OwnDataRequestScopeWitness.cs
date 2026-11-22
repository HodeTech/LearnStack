using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.Identity;

/// <summary>
/// The closed purposes of the retained own data-request scope (ADR-0106 § 9). <see cref="List"/> discovers the
/// subject's groups on the real host; every other purpose acts in one exact group the function returned.
/// </summary>
public enum OwnDataRequestPurpose { List = 1, Create = 2, Read = 3, Cancel = 4, Ticket = 5, Chunk = 6 }

/// <summary>One page of the subject's retained groups on the real host, ordered as the function returned them.</summary>
public sealed record OwnDataRequestScopePage(ImmutableArray<DataRequestScopeGroup> Items, string? NextCursor);

/// <summary>The one source that captures a verified subject, real host and retained group for an invocation.</summary>
public interface IOwnDataRequestScopeSource
{
    OwnDataRequestScopeWitness? ForInvocation(OperationInvocation invocation);
}

/// <summary>
/// One verified subject, real host and closed data-request purpose, bound to the group
/// <c>fn_identity_own_data_request_scopes</c> returned for that subject; no ordinary Membership authority.
/// </summary>
/// <remarks>
/// Modelled on <see cref="ConsentSubjectScopeWitness"/>: a single-use, immutable, privately constructed
/// witness that only an enumerated minting source captures, that the actual subsequent owner binds once
/// to its physical transaction, and that stays live only while its operation invocation, policy kind
/// (<see cref="OperationPolicyKind.OwnDataRequestEntry"/>), origin
/// (<see cref="TenantContextOrigin.HostAndOwnDataRequestScope"/>), actor, group and real host all still agree.
/// </remarks>
public sealed class OwnDataRequestScopeWitness
{
    /// <summary>The function's own page bound; the source never asks for more.</summary>
    public const int MaximumPage = 20;

    private readonly MintingSource _source;
    private readonly object _evidence;
    private readonly object _gate = new();
    private (IUnitOfWork Unit, DbTransaction Transaction, long Generation)? _binding;

    private OwnDataRequestScopeWitness(MintingSource source, object evidence, OperationInvocation invocation,
        GlobalIdentitySubject subject, UserId actor, TenantId hostTenant, OrganizationId? hostOrganization,
        string host, OwnDataRequestPurpose purpose, DataRequestScopeGroup? group, OwnDataRequestScopePage? scopePage)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(subject);
        if (!StronglyTypedId.IsAssigned(actor) || actor == UserId.SystemActor
            || !StronglyTypedId.IsAssigned(hostTenant) || hostTenant == TenantId.PlatformSentinel
            || !StronglyTypedId.IsAssignedWhenPresent(hostOrganization) || string.IsNullOrWhiteSpace(host)
            || !Enum.IsDefined(purpose) || (purpose == OwnDataRequestPurpose.List) != (scopePage is not null)
            || (purpose == OwnDataRequestPurpose.List) != (group is null))
            throw new ArgumentException("Own data request subject provenance refused.");
        bool WithinHost(DataRequestScopeGroup scope) => scope.TenantId == hostTenant
            && StronglyTypedId.IsAssignedWhenPresent(scope.OrganizationId)
            && (hostOrganization is null || scope.OrganizationId == hostOrganization);
        if (group is not null && !WithinHost(group)
            || scopePage is not null && (scopePage.Items.IsDefault || scopePage.Items.Length > MaximumPage
                || scopePage.Items.Any(scope => scope is null || !WithinHost(scope))
                || scopePage.Items.Distinct().Count() != scopePage.Items.Length
                || scopePage.NextCursor is { Length: > 1024 }))
            throw new ArgumentException("Own data request subject scope refused.");
        _source = source; _evidence = evidence; Invocation = invocation; Actor = actor;
        AuthorityId = subject.AuthorityId; SubjectKey = subject.SubjectKey; Host = host;
        HostTenantId = hostTenant; HostOrganizationId = hostOrganization; Purpose = purpose;
        Group = group; ScopePage = scopePage;
        TenantId = group?.TenantId ?? hostTenant;
        OrganizationId = group is null ? hostOrganization : group.OrganizationId;
    }

    public OperationInvocation Invocation { get; }
    public UserId Actor { get; }
    public IdentityAuthorityId AuthorityId { get; }
    [PiiSensitive] public IdentitySubjectKey SubjectKey { get; }
    /// <summary>The announced tenant: the group's, or the host's for <see cref="OwnDataRequestPurpose.List"/>.</summary>
    public TenantId TenantId { get; }
    /// <summary>The announced organization: the group's, or the host's for <see cref="OwnDataRequestPurpose.List"/>.</summary>
    public OrganizationId? OrganizationId { get; }
    public TenantId HostTenantId { get; }
    public OrganizationId? HostOrganizationId { get; }
    /// <summary>The exact retained group; <see langword="null"/> only for <see cref="OwnDataRequestPurpose.List"/>.</summary>
    public DataRequestScopeGroup? Group { get; }
    public string Host { get; }
    public OwnDataRequestPurpose Purpose { get; }
    public OwnDataRequestScopePage? ScopePage { get; }

    /// <summary>Discovery and status reads are physically read-only; filing, cancelling and retrieval write.</summary>
    public TransactionMode Mode => Purpose is OwnDataRequestPurpose.List or OwnDataRequestPurpose.Read
        ? TransactionMode.ReadOnly : TransactionMode.ReadWrite;

    public bool IsActive => _source.Current(_evidence, Invocation) && Invocation.IsActive
        && Invocation.Policy.Kind == OperationPolicyKind.OwnDataRequestEntry
        && Invocation.Context.Origin == TenantContextOrigin.HostAndOwnDataRequestScope
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

    public override string ToString() => nameof(OwnDataRequestScopeWitness);

#pragma warning disable CA1034 // A closed source census owns the sole production minter.
    [SuppressMessage("Design", "CA1063:Implement IDisposable Correctly", Justification = "Nonvirtual disposal closes captured provenance.")]
    public abstract class MintingSource : IDisposable
#pragma warning restore CA1034
    {
        private int _closed;
        protected bool IsOpen => Volatile.Read(ref _closed) == 0;
        internal bool Current(object evidence, OperationInvocation invocation) => Volatile.Read(ref _closed) == 0
            && IsEvidenceCurrent(evidence, invocation);
        protected abstract bool IsEvidenceCurrent(object evidence, OperationInvocation invocation);
        protected OwnDataRequestScopeWitness Capture(object evidence, OperationInvocation invocation,
            GlobalIdentitySubject subject, UserId actor, TenantId hostTenant, OrganizationId? hostOrganization,
            string host, OwnDataRequestPurpose purpose, DataRequestScopeGroup? group, OwnDataRequestScopePage? page)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            return new(this, evidence, invocation, subject, actor, hostTenant, hostOrganization, host, purpose, group, page);
        }
        public void Dispose() { Interlocked.Exchange(ref _closed, 1); GC.SuppressFinalize(this); }
    }
}
