using System.Collections.Frozen;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// The confined owner of the operator's data-request acts (ADR-0107 § 8, ADR-0108): the metadata-only inspection of the
/// work that needs a person, and the completion of an erasure's privileged step.
/// </summary>
/// <remarks>
/// <para>
/// <b>The database bounds what it reads.</b> Each call is one entered platform scope on <c>learnstack_platform</c>,
/// which holds a closed-column <c>SELECT</c> of the request and artifact tables and nothing on their tickets and
/// chunks, so a statement that named a requester, a progress record, an artifact's subject or key version would be
/// refused by PostgreSQL. An inspection returns no subject or snapshot even though the completion reads them: it reads
/// and changes nothing, so its audit is the scope's entry row and it ends by disposal.
/// </para>
/// <para>
/// <b>A completion is one transaction.</b> The request is locked and read whole, the plan is built from that row and
/// never from the caller, the Audit-owned executor redacts on the scope's transaction, the request moves to
/// <c>completed</c> under the request guard's platform arm, and the result row names the request's own tenant; all of
/// it commits together or nothing does (ADR-0108 § 8). A request already completed answers its recorded result and
/// writes no second row; two concurrent completions serialize on the row lock and the second finds the first's.
/// </para>
/// <para>
/// <b>Failures.</b> A transient database fault before the commit is the unavailable owner, safe to repeat because the
/// redaction is idempotent; any failure once the commit was sent is the unknown outcome. Anything else, a refused
/// credential or a missing grant included, is the defect it is.
/// </para>
/// </remarks>
public sealed class DataRequestOperatorOperations(IPlatformAdminScope scope) : IDataRequestOperatorOperations
{
    private const int MaximumLimit = CursorPagination.MaxLimit;
    private const string EntityType = "DataSubjectRequest";

    /// <summary>
    /// The four retained kinds the audit log holds (ADR-0107 § 6, ADR-0108 § 4): the redaction's report sets these, and
    /// the request guard's platform arm refuses a completion that changes any other kind of the tenant side's record.
    /// </summary>
    private static readonly FrozenSet<RetainedReferenceKind> AuditKinds = new[]
    {
        RetainedReferenceKind.AuditActorReference, RetainedReferenceKind.AuditEntityReference,
        RetainedReferenceKind.AuditMetadataReference, RetainedReferenceKind.UnscopedAuditRows,
    }.ToFrozenSet();

    private const string RequestColumns =
        "r.id, r.tenant_id, r.organization_id, r.kind, r.origin, r.status, r.created_at, r.execute_at, r.completed_at, "
        + "r.last_refusal, r.failure_reason";

    // The ordering key of the three request lists: when the request fell due. An erasure or closure falls due when its
    // window ends and the rest when they are filed; a request cannot have started work before it, so a stuck one is
    // never missed. The text is the index's expression, character for character, or the planner cannot use the index.
    private const string DueAt = "COALESCE(r.execute_at, r.created_at)";

    private const string LockRequest = """
        SELECT id, tenant_id, organization_id, kind, origin, subject_user_id, status, membership_snapshot::text,
            result::text, row_version, completed_at
        FROM data_subject_requests WHERE id = @id FOR UPDATE
        """;

    // The guard's platform arm stamps completed_at, updated_at and updated_by from the database clock and the system
    // actor, so the statement sets only what the operator decides: the arrow, the merged result and the version step.
    private const string Complete = """
        UPDATE data_subject_requests SET status = 'completed', result = @result::jsonb, row_version = row_version + 1
        WHERE id = @id AND kind = 'erasure' AND status = 'awaiting_privileged_step' AND row_version = @version
        RETURNING completed_at
        """;

    /// <summary>
    /// The statement of one inspection page. It is exposed so that a test can ask PostgreSQL for the plan of the text
    /// the inspection really runs: every list must be an ordered scan of its partial index, with no sort.
    /// </summary>
    public static string InspectionSql(DataRequestInspectionState state, bool afterCursor) => state switch
    {
        DataRequestInspectionState.AwaitingPrivilegedStep => RequestPage("awaiting_privileged_step", null, afterCursor),
        DataRequestInspectionState.OverdueScheduled => RequestPage("scheduled", "now()", afterCursor),
        DataRequestInspectionState.StuckFulfilling => RequestPage("fulfilling", "now() - @stuck", afterCursor),
        DataRequestInspectionState.ExpiredArtifact =>
            $"SELECT {RequestColumns}, a.id, a.status, a.expires_at FROM data_export_artifacts a "
            + "JOIN data_subject_requests r ON r.tenant_id = a.tenant_id AND r.id = a.request_id "
            + "WHERE a.status IN ('available', 'expired') AND a.expires_at <= now()"
            + (afterCursor ? " AND (a.expires_at, a.id) > (@after, @key)" : string.Empty)
            + " ORDER BY a.expires_at, a.id LIMIT @size",
        _ => throw new ArgumentOutOfRangeException(nameof(state), "The inspection lists only its four closed states."),
    };

    private static string RequestPage(string status, string? dueBy, bool afterCursor) =>
        $"SELECT {RequestColumns}, NULL::uuid, NULL::text, NULL::timestamptz FROM data_subject_requests r "
        + $"WHERE r.status = '{status}'"
        + (dueBy is null ? string.Empty : $" AND {DueAt} <= {dueBy}")
        + (afterCursor ? $" AND ({DueAt}, r.id) > (@after, @key)" : string.Empty)
        + $" ORDER BY {DueAt}, r.id LIMIT @size";

    /// <inheritdoc />
    public async Task<InspectionPage<DataRequestInspectionItem>?> InspectAsync(
        PlatformAdmission admission, DataRequestInspectionQuery query, CancellationToken cancellationToken = default)
    {
        RequireOperation(admission, PlatformOperations.DataRequestInspect);
        ArgumentNullException.ThrowIfNull(query);
        var state = Enum.IsDefined(query.State)
            ? query.State
            : throw new ArgumentOutOfRangeException(nameof(query), "The inspection lists only its four closed states.");
        var size = Math.Clamp(query.Limit, 1, MaximumLimit);
        var scopeKey = state.ToString();
        long? after = null;
        var key = Guid.Empty;
        if (query.Cursor is not null)
        {
            if (!TryDecode(query.Cursor, scopeKey, out var ticks, out key))
            {
                return null;
            }

            after = ticks;
        }

        var parameters = new List<PlatformSqlParameter> { new("size", size + 1) };
        if (state == DataRequestInspectionState.StuckFulfilling)
        {
            parameters.Add(new PlatformSqlParameter("stuck", DataRequestLimits.StuckFulfillingAfter));
        }

        if (after is { } position)
        {
            parameters.Add(new PlatformSqlParameter("after", new DateTimeOffset(position, TimeSpan.Zero)));
            parameters.Add(new PlatformSqlParameter("key", key));
        }

        IReadOnlyList<DataRequestInspectionItem> rows;
        try
        {
            await using var handle = await scope.EnterAsync(admission, cancellationToken).ConfigureAwait(false);
            rows = await handle.QueryAsync(InspectionSql(state, after is not null), parameters, MapItem, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (OperatorRecoveryFaults.IsTransientDatabaseFault(failure, cancellationToken))
        {
            // A read is safe to repeat; the cause is for the route's log, never for the response.
            throw new OperatorRecoveryUnavailableException(cause: failure);
        }

        var items = rows.Take(size).ToList();
        var next = rows.Count > size && items.Count > 0 ? Encode(scopeKey, items[^1], state) : null;
        return new InspectionPage<DataRequestInspectionItem>(items, next);
    }

    /// <inheritdoc />
    public async Task<ErasureCompletionResult> CompleteErasureAsync(
        PlatformAdmission admission, Guid requestId, CancellationToken cancellationToken = default)
    {
        RequireOperation(admission, PlatformOperations.ErasureComplete);
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("An erasure completion names a request.", nameof(requestId));
        }

        var commitSent = false;
        try
        {
            return await CompleteCoreAsync(admission, requestId, () => commitSent = true, cancellationToken);
        }
        catch (Exception failure) when (commitSent && !IsFatal(failure))
        {
            // The commit was sent: it may have landed, so this is never "nothing changed, repeat it". A repeat after a
            // landed commit answers the recorded result with no second row, which is how the operator finds out.
            throw new OperatorRecoveryUnavailableException(commitOutcomeUnknown: true, cause: failure);
        }
        catch (Exception failure) when (!commitSent && OperatorRecoveryFaults.IsTransientDatabaseFault(failure, cancellationToken))
        {
            // Nothing committed, and the redaction's statements are idempotent, so a repeat is safe.
            throw new OperatorRecoveryUnavailableException(cause: failure);
        }
    }

    private async Task<ErasureCompletionResult> CompleteCoreAsync(
        PlatformAdmission admission, Guid requestId, Action sendingCommit, CancellationToken cancellationToken)
    {
        await using var handle = await scope.EnterAsync(admission, cancellationToken);

        // 1. The request, whole and locked: a concurrent completion waits here and then reads the committed outcome.
        var rows = await handle.QueryAsync(LockRequest, [new PlatformSqlParameter("id", requestId)], ReadRequest, cancellationToken);
        if (rows.Count == 0)
        {
            return new ErasureCompletionResult(ErasureCompletionOutcome.NotFound);
        }

        var request = rows[0];
        var status = DataRequestCodes.Statuses.ToCode(request.Status);
        if (request.Kind != DataRequestKind.Erasure)
        {
            return new ErasureCompletionResult(ErasureCompletionOutcome.NotErasure, request.Tenant, status);
        }

        if (request.Status == DataRequestStatus.Completed)
        {
            // ADR-0108 § 8: a re-run after the commit returns the recorded result with no second row. The scope ends by
            // disposal, which writes nothing beyond its entry row.
            return new ErasureCompletionResult(
                ErasureCompletionOutcome.AlreadyCompleted, request.Tenant, status, request.CompletedAt, Codes(request.Retained));
        }

        if (request.Status != DataRequestStatus.AwaitingPrivilegedStep)
        {
            return new ErasureCompletionResult(ErasureCompletionOutcome.NotAwaiting, request.Tenant, status);
        }

        // 2. The redaction, from the row's immutable subject, group, origin and snapshot, on this transaction.
        var report = await handle.RedactAuditAsync(new AuditRedactionPlan(request.Subject()), cancellationToken);
        var retained = Merge(request.Retained, report);

        // 3. The arrow, under the guard's platform arm, exactly once.
        var completed = await handle.QueryAsync(
            Complete,
            [
                new PlatformSqlParameter("id", requestId),
                new PlatformSqlParameter("version", request.Version),
                new PlatformSqlParameter("result", ResultJson(retained)),
            ],
            reader => reader.GetFieldValue<DateTimeOffset>(0),
            cancellationToken);
        if (completed.Count != 1)
        {
            throw new InvalidOperationException(
                "The locked erasure did not complete in exactly one row, so no result row describes it; the act is abandoned.");
        }

        // 4. The MUST row, under the request's real tenant: the tenant reads it, so it has no ticket and no actor.
        await handle.WriteResultAsync(
            new PlatformResult(
                TenantId.From(request.Tenant),
                EntityType,
                requestId.ToString("D", CultureInfo.InvariantCulture),
                StatusState(request.Status),
                AfterState(report)),
            cancellationToken);

        // Past the point where the caller's abort matters: a cancelled commit leaves its outcome unknown.
        sendingCommit();
        await handle.CommitAsync(CancellationToken.None);
        return new ErasureCompletionResult(
            ErasureCompletionOutcome.Completed, request.Tenant, DataRequestCodes.Statuses.ToCode(DataRequestStatus.Completed),
            completed[0], Codes(retained), report);
    }

    /// <summary>
    /// The tenant side's record with the audit log's kinds set from the report. A kind the report leaves out keeps its
    /// recorded count, because a report's list is partial and an absent kind is not zero.
    /// </summary>
    private static SortedDictionary<RetainedReferenceKind, long> Merge(
        IReadOnlyDictionary<RetainedReferenceKind, long> recorded, AuditRedactionReport report)
    {
        var merged = new SortedDictionary<RetainedReferenceKind, long>();
        foreach (var (kind, count) in recorded)
        {
            merged[kind] = count;
        }

        foreach (var entry in report.Retained)
        {
            if (!AuditKinds.Contains(entry.Kind))
            {
                throw new InvalidOperationException(
                    "The redaction reported a retained kind the audit log does not hold; the act is abandoned.");
            }

            merged[entry.Kind] = entry.Count;
        }

        return merged;
    }

    private static string ResultJson(IReadOnlyDictionary<RetainedReferenceKind, long> retained)
    {
        var json = new JsonObject();
        foreach (var (kind, count) in retained)
        {
            json[DataRequestCodes.RetainedKinds.ToCode(kind)] = count;
        }

        return json.ToJsonString();
    }

    private static Dictionary<string, long> Codes(IReadOnlyDictionary<RetainedReferenceKind, long> retained) =>
        retained.ToDictionary(entry => DataRequestCodes.RetainedKinds.ToCode(entry.Key), entry => entry.Value, StringComparer.Ordinal);

    private static string StatusState(DataRequestStatus status) =>
        new JsonObject { ["status"] = DataRequestCodes.Statuses.ToCode(status) }.ToJsonString();

    /// <summary>
    /// The closed report with the new status: counts per column and per kind, never a person identifier.
    /// </summary>
    private static string AfterState(AuditRedactionReport report)
    {
        var after = JsonNode.Parse(report.ToClosedJson())!.AsObject();
        after.Insert(0, "status", DataRequestCodes.Statuses.ToCode(DataRequestStatus.Completed));
        return after.ToJsonString();
    }

    private static LockedRequest ReadRequest(DbDataReader reader)
    {
        var snapshot = new List<Guid>();
        using (var document = JsonDocument.Parse(reader.GetString(7)))
        {
            foreach (var member in document.RootElement.EnumerateArray())
            {
                snapshot.Add(Guid.Parse(member.GetString()!, CultureInfo.InvariantCulture));
            }
        }

        var retained = new Dictionary<RetainedReferenceKind, long>();
        using (var document = JsonDocument.Parse(reader.GetString(8)))
        {
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                retained[DataRequestCodes.RetainedKinds.Parse(entry.Name)] = entry.Value.GetInt64();
            }
        }

        return new LockedRequest(
            reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
            DataRequestCodes.Kinds.Parse(reader.GetString(3)), DataRequestCodes.Origins.Parse(reader.GetString(4)),
            reader.GetGuid(5), DataRequestCodes.Statuses.Parse(reader.GetString(6)), snapshot, retained, reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10));
    }

    private static DataRequestInspectionItem MapItem(DbDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
        DataRequestCodes.Kinds.ToCode(DataRequestCodes.Kinds.Parse(reader.GetString(3))),
        DataRequestCodes.Origins.ToCode(DataRequestCodes.Origins.Parse(reader.GetString(4))),
        DataRequestCodes.Statuses.ToCode(DataRequestCodes.Statuses.Parse(reader.GetString(5))),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : DataRequestCodes.Refusals.ToCode(DataRequestCodes.Refusals.Parse(reader.GetString(9))),
        reader.IsDBNull(10) ? null : DataRequestCodes.FailureReasons.ToCode(DataRequestCodes.FailureReasons.Parse(reader.GetString(10))),
        reader.IsDBNull(11)
            ? null
            : new DataExportArtifactInspection(
                reader.GetGuid(11),
                DataRequestCodes.ArtifactStatuses.ToCode(DataRequestCodes.ArtifactStatuses.Parse(reader.GetString(12))),
                reader.GetFieldValue<DateTimeOffset>(13)));

    private static void RequireOperation(PlatformAdmission admission, PlatformOperation expected)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (!ReferenceEquals(admission.Operation, expected))
        {
            throw new ArgumentException("The admission is not for this data-request operation.", nameof(admission));
        }
    }

    private static bool IsFatal(Exception failure) => failure is OutOfMemoryException or AccessViolationException or StackOverflowException;

    /// <summary>
    /// The position after <paramref name="item"/>: its ordering instant and its own key, bound to the list.
    /// </summary>
    private static string Encode(string scopeKey, DataRequestInspectionItem item, DataRequestInspectionState state)
    {
        var (instant, key) = state == DataRequestInspectionState.ExpiredArtifact
            ? (item.Artifact!.ExpiresAt, item.Artifact.ArtifactId)
            : (item.ExecuteAt ?? item.CreatedAt, item.RequestId);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(
                '~', "v1", scopeKey, instant.UtcTicks.ToString(CultureInfo.InvariantCulture), key.ToString("D", CultureInfo.InvariantCulture))))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecode(string cursor, string scopeKey, out long ticks, out Guid key)
    {
        ticks = 0;
        key = Guid.Empty;
        if (cursor.Length is 0 or > 512)
        {
            return false;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('~');
            return parts is ["v1", var issuedFor, var instant, var position]
                && string.Equals(issuedFor, scopeKey, StringComparison.Ordinal)
                && long.TryParse(instant, NumberStyles.None, CultureInfo.InvariantCulture, out ticks)
                && ticks <= DateTimeOffset.MaxValue.UtcTicks
                && Guid.TryParseExact(position, "D", out key);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The request row the completion locked, read whole through the platform role's column grant.</summary>
    private sealed record LockedRequest(
        Guid Id, Guid Tenant, Guid? Organization, DataRequestKind Kind, DataRequestOrigin Origin, Guid SubjectUser,
        DataRequestStatus Status, IReadOnlyList<Guid> Snapshot, IReadOnlyDictionary<RetainedReferenceKind, long> Retained,
        long Version, DateTimeOffset? CompletedAt)
    {
        /// <summary>The plan's subject, from the row's immutable snapshot and never from the caller.</summary>
        internal UserDataSubject Subject() => new(
            Id, Kind, Origin, UserId.From(SubjectUser),
            new DataRequestScopeGroup(TenantId.From(Tenant), Organization is { } organization ? OrganizationId.From(organization) : null),
            Snapshot);

        /// <summary>Never prints the subject or the snapshot.</summary>
        public override string ToString() => nameof(LockedRequest);
    }
}
