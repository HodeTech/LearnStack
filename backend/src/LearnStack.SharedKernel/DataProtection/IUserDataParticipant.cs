using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// One module's part in subject data requests: export its rows for a subject and group, erase
/// what it may erase, and describe what it must retain
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 1</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A registration, not a module-to-module call.</b> Each applicable module implements this
/// port inside its own boundary and the composition root collects the implementations, as it
/// collects <c>IAuditCatalogSource</c> and <c>IOperationPolicySource</c>; no module references
/// another module's participant type, so the four cross-module mechanisms of ADR-0010 stay four.
/// </para>
/// <para>
/// <b>The subject predicate is the participant's.</b> Every participant selects rows by the
/// subject's <see cref="UserId"/> and the request's group, inside the tenant's ambient context as
/// <c>learnstack_app</c>. A row naming another person as a different party is included only with
/// that reference replaced by <c>third_party</c>; a field holding another person's data is omitted
/// (ADR-0106 § 5). Personal-data category is never disclosure authority.
/// </para>
/// <para>
/// <b>One transaction per participant.</b> The fulfilling job runs each participant's
/// <see cref="EraseAsync"/> in its own transaction and records its completion in the request's
/// progress record under <see cref="Key"/> before the next, so no transaction writes two modules'
/// tables and a resumed job skips completed participants.
/// </para>
/// </remarks>
public interface IUserDataParticipant
{
    /// <summary>
    /// The participant's stable key: a lower snake_case token (<see cref="DataRequestLimits.IsParticipantKey"/>),
    /// unique across the composition root, used as the progress-record key and the bundle section name.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// The participant's own section cap in bytes, or <see langword="null"/> for none beyond the bundle
    /// ceiling. The audit section declares <see cref="DataRequestLimits.AuditSectionBytes"/> (ADR-0107 § 3);
    /// a record that would pass it is refused with <see cref="UserDataExportAppend.SectionCapReached"/>, and
    /// the participant declares the truncation and stops.
    /// </summary>
    long? ExportSectionCapBytes => null;

    /// <summary>Streams the participant's section of an export bundle into a bounded writer (ADR-0107 § 3).</summary>
    /// <remarks>
    /// The writer belongs to this participant's section alone. A participant never builds the bundle,
    /// encrypts or stores it, and stops appending when the writer reports a cap.
    /// </remarks>
    Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken);

    /// <summary>Erases what this participant may erase for the subject in the group (ADR-0107 § 5).</summary>
    /// <remarks>
    /// The default does nothing: a participant whose rows are all retained (consent evidence,
    /// attribution, audit) only describes them. An implementation is idempotent, because a resumed
    /// job may call it again after a crash before its completion was recorded.
    /// </remarks>
    Task EraseAsync(UserDataSubject subject, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Reports what survives for the subject in the group, as closed kinds and counts
    /// (ADR-0107 § 6); never a row identifier.
    /// </summary>
    Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(
        UserDataSubject subject, CancellationToken cancellationToken);
}

/// <summary>What one participant call concerns: the request's immutable subject and group snapshot.</summary>
/// <remarks>
/// Built from the request row, never from the caller's context, so removing the Membership strands
/// neither pending work nor its scope (ADR-0106 § 8). <see cref="MembershipIds"/> are the frozen
/// Membership identifiers of the subject in the group, including Revoked tombstones; ADR-0108 § 4
/// selects audit rows by them.
/// </remarks>
public sealed record UserDataSubject
{
    /// <exception cref="ArgumentException">A member is unassigned, or the snapshot is empty, duplicated or too large.</exception>
    public UserDataSubject(Guid requestId, DataRequestKind kind, DataRequestOrigin origin, UserId subjectUserId,
        DataRequestScopeGroup group, IEnumerable<Guid> membershipIds)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(membershipIds);
        if (requestId == Guid.Empty || !Enum.IsDefined(kind) || !Enum.IsDefined(origin))
        {
            throw new ArgumentException("A participant subject needs its request identity, kind and origin.", nameof(requestId));
        }

        if (!StronglyTypedId.IsAssigned(subjectUserId) || subjectUserId == UserId.SystemActor)
        {
            throw new ArgumentException("A participant subject needs a real person.", nameof(subjectUserId));
        }

        var memberships = membershipIds.ToImmutableArray();
        if (memberships.IsEmpty || memberships.Length > DataRequestLimits.MaximumSnapshotMemberships
            || memberships.Contains(Guid.Empty) || memberships.Distinct().Count() != memberships.Length)
        {
            throw new ArgumentException("A participant subject needs its bounded frozen Membership snapshot.", nameof(membershipIds));
        }

        RequestId = requestId;
        Kind = kind;
        Origin = origin;
        SubjectUserId = subjectUserId;
        Group = group;
        MembershipIds = memberships;
    }

    public Guid RequestId { get; }
    public DataRequestKind Kind { get; }
    public DataRequestOrigin Origin { get; }
    public UserId SubjectUserId { get; }
    public DataRequestScopeGroup Group { get; }
    public ImmutableArray<Guid> MembershipIds { get; }

    /// <summary>Never prints the subject, the group or the snapshot.</summary>
    public override string ToString() => nameof(UserDataSubject);
}

/// <summary>One closed retained-reference kind and how many references of it survive.</summary>
public sealed record RetainedReferenceCount
{
    /// <exception cref="ArgumentOutOfRangeException">The kind is undefined or the count is negative.</exception>
    public RetainedReferenceCount(RetainedReferenceKind kind, long count)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "The retained kind is not a member of the closed vocabulary.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Kind = kind;
        Count = count;
    }

    public RetainedReferenceKind Kind { get; }
    public long Count { get; }
}

/// <summary>The closed outcome of appending one record to an export section.</summary>
public enum UserDataExportAppend
{
    /// <summary>The record is in the bundle.</summary>
    Appended = 1,

    /// <summary>
    /// The section's own cap is reached (the audit section's 8 MiB, ADR-0107 § 3); the record was not
    /// written, and the participant declares the truncation and stops.
    /// </summary>
    SectionCapReached = 2,

    /// <summary>
    /// The bundle ceiling (<c>DataExport:MaxBytes</c>) is reached; the record was not written and the
    /// request ends <c>failed</c> with <c>export_too_large</c> (ADR-0107 § 4).
    /// </summary>
    BundleCeilingReached = 3,
}

/// <summary>
/// The bounded writer of one participant's export section (ADR-0107 § 3).
/// </summary>
/// <remarks>
/// <para>
/// A record is one UTF-8 encoded JSON value appended to a named collection of the section
/// (<c>memberships</c>, <c>invitations</c>, <c>audit</c>, …); collection names follow the participant
/// key's spelling. The writer validates the record, counts its bytes against the section cap and
/// the bundle ceiling, and refuses rather than truncating a record in the middle.
/// </para>
/// <para>
/// The implementation, the bundle envelope (schema version, request identity, generation time) and
/// the encryption belong to the export job and the artifact store; this port is only the bound a
/// participant writes into.
/// </para>
/// </remarks>
public interface IUserDataExportWriter
{
    /// <summary>The bytes this section may still append before either bound is reached.</summary>
    long RemainingBytes { get; }

    /// <summary>Appends one record; never partially.</summary>
    ValueTask<UserDataExportAppend> AppendAsync(string collection, ReadOnlyMemory<byte> utf8JsonRecord,
        CancellationToken cancellationToken);

    /// <summary>
    /// States that a collection stopped at its cap: the bundle and the receipt say <c>truncated</c>
    /// with the records included and the records in total (ADR-0107 § 3).
    /// </summary>
    void DeclareTruncated(string collection, long includedRecords, long totalRecords);
}

/// <summary>
/// The composition root's closed collection of every registered participant, ordered ordinally by key.
/// </summary>
public interface IUserDataParticipantRegistry
{
    /// <summary>Every participant, ordered ordinally by <see cref="IUserDataParticipant.Key"/>.</summary>
    IReadOnlyList<IUserDataParticipant> Participants { get; }

    /// <summary>Finds a participant by its exact key.</summary>
    bool TryGet(string key, [NotNullWhen(true)] out IUserDataParticipant? participant);
}
