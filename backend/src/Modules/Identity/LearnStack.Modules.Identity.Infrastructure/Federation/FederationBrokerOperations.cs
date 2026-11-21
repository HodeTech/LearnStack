using System.Globalization;
using System.Text;
using System.Text.Json;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Identity.Infrastructure.Federation;

/// <summary>
/// The confined owner of the operator's federation report, remove and disable (ADR-0099 Amendment 10 and 11). It holds
/// the platform scope and the operator port and nothing of the tenant runner, so the rule that the tenant's provider
/// port is held only by the runner stands.
/// </summary>
/// <remarks>
/// <para>
/// <b>The realm is read first, the rows second.</b> A tenant writes its connection row before it creates the broker, so
/// a broker the realm listed already had a committed row when the row query runs; reading the rows first would call
/// a broker created in between an orphan. The scope is entered before either read, so the admission is spent fresh and
/// the entry row records the attempt even when the provider is down.
/// </para>
/// <para>
/// <b>An act has two rows.</b> The intent row is written at the scope handle's checkpoint and committed before the
/// provider call; the completion row records how the call ended and commits at the end. A crash between them leaves the
/// intent alone, which the next report resolves by showing the broker present or gone. A no-op (an absent broker on a
/// remove, a disabled broker on a disable) and a refusal before the intent write no tenant row: the entry row records
/// the call, and no tenant is known from nothing.
/// </para>
/// <para>
/// <b>The completion is settled on its own budget.</b> Once a change may have been sent, a caller that leaves or a
/// completion that cannot be written must not turn into "nothing changed": the completion row is written on a bounded
/// token detached from the request, as the tenant runner completes on one, and a completion that still fails after a change
/// answers <see cref="FederationActOutcome.Uncertain"/>. The intent row then stands alone, which the next report resolves.
/// </para>
/// <para>
/// <b>The rows name the marker's tenant</b>, never the platform sentinel, even when no <c>tenants</c> row exists for it.
/// They carry closed tokens only: no display name, issuer, configuration or secret, and no ticket, because a tenant can
/// read them.
/// </para>
/// </remarks>
public sealed class FederationBrokerOperations(
    IPlatformAdminScope scope, IFederationOperator provider, TimeSpan? completionBudget = null) : IFederationBrokerOperations
{
    private const int MaximumLimit = CursorPagination.MaxLimit;
    private const string EntityType = "FederationConnection";
    private const string RowQuery = "SELECT id, tenant_id, state, deleted_at IS NOT NULL FROM federation_connections WHERE id = ANY(@ids)";
    private static readonly TimeSpan ReportDeadline = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The completion's own budget, detached from the request; a test passes a shorter or longer one.</summary>
    private readonly TimeSpan _completionBudget = completionBudget ?? TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public Task<FederationReportResult> ReportAsync(
        PlatformAdmission admission, string? cursor, int limit, CancellationToken cancellationToken = default) =>
        GuardAsync(_ => ReportCoreAsync(admission, cursor, limit, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<FederationActResult> RemoveAsync(
        PlatformAdmission admission, string brokerAlias, CancellationToken cancellationToken = default) =>
        GuardAsync(progress => RemoveCoreAsync(admission, brokerAlias, progress, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<FederationActResult> DisableAsync(
        PlatformAdmission admission, string brokerAlias, CancellationToken cancellationToken = default) =>
        GuardAsync(progress => DisableCoreAsync(admission, brokerAlias, progress, cancellationToken), cancellationToken);

    /// <summary>Whether a provider change may have landed, which decides what a failure further on may claim.</summary>
    private sealed class ActProgress
    {
        internal bool ChangeMayHaveLanded { get; set; }
    }

    /// <summary>
    /// A failure after a change that landed or may have is the unknown outcome, with its cause kept for the route's log and
    /// never "nothing changed"; before any change, a transient database fault is the unavailable owner, and never a raw 500.
    /// What this guard does not catch (a refused credential, a missing grant before any change, a programmer error) is the
    /// defect it is.
    /// </summary>
    private static async Task<T> GuardAsync<T>(Func<ActProgress, Task<T>> operation, CancellationToken cancellationToken)
    {
        var progress = new ActProgress();
        try
        {
            return await operation(progress);
        }
        catch (Exception failure) when (progress.ChangeMayHaveLanded && !IsFatal(failure))
        {
            throw new OperatorRecoveryUnavailableException(commitOutcomeUnknown: true, cause: failure);
        }
        catch (Exception failure) when (!progress.ChangeMayHaveLanded
            && OperatorRecoveryFaults.IsTransientDatabaseFault(failure, cancellationToken))
        {
            throw new OperatorRecoveryUnavailableException(cause: failure);
        }
    }

    private static bool IsFatal(Exception failure) => failure is OutOfMemoryException or AccessViolationException or StackOverflowException;

    private async Task<FederationReportResult> ReportCoreAsync(
        PlatformAdmission admission, string? cursor, int limit, CancellationToken cancellationToken)
    {
        RequireOperation(admission, PlatformOperations.FederationReport);
        var offset = 0;
        if (cursor is not null && !TryDecode(cursor, out offset))
        {
            return FederationReportResult.Of(FederationReportOutcome.InvalidCursor);
        }

        var size = Math.Clamp(limit, 1, MaximumLimit);
        await using var handle = await scope.EnterAsync(admission, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReportDeadline);

        // 1. The realm, before any row.
        FederationBrokerPage page;
        try
        {
            page = await provider.ListAsync(offset, size, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FederationReportResult.Of(FederationReportOutcome.Unavailable);
        }

        if (page.Status != FederationOperatorStatus.Ok)
        {
            return FederationReportResult.Of(FederationReportOutcome.Unavailable);
        }

        // 2. The rows, strictly later, by primary key and through the four granted columns only.
        var rows = await ReadRowsAsync(handle, page.Owned.Select(facts => facts.Owner.ConnectionId).Distinct().ToArray(), cancellationToken);
        var ignored = page.Ignored;
        var candidates = new List<(FederationBrokerFacts Facts, Row? Row)>();
        foreach (var facts in page.Owned)
        {
            rows.TryGetValue(facts.Owner.ConnectionId, out var row);
            if (row is not null && row.TenantId != facts.Owner.TenantId.Value)
            {
                // The marker and the row disagree about whose it is: never reported, never touched.
                ignored++;
                continue;
            }

            candidates.Add((facts, row));
        }

        // 3. The mappers of what looks healthy, bounded, because an extra mapper is drift only a mapper read shows.
        var mappers = await CountMappersAsync(
            candidates.Where(item => IsLive(item.Row) && item.Facts is { Readable: true, Conforms: true })
                .Select(item => item.Facts.Owner).ToList(),
            deadline.Token, cancellationToken);

        var items = new List<FederationBrokerReportItem>();
        var unread = 0;
        foreach (var (facts, row) in candidates)
        {
            mappers.TryGetValue(facts.Owner.Alias, out var count);
            if (count is null && IsLive(row) && facts is { Readable: true, Conforms: true })
            {
                // A mapper is drift only a mapper read shows: this broker is unknown, not clean, and the page says so.
                unread++;
            }

            var classes = ClassesOf(facts, row, count ?? 0);
            if (classes.Count == 0)
            {
                continue;
            }

            items.Add(new FederationBrokerReportItem(
                facts.Owner.Alias, facts.Owner.TenantId.Value, facts.Owner.ConnectionId, classes, RowState(row),
                facts.Protocol is { } protocol ? protocol.ToString().ToLowerInvariant() : null, facts.Enabled, count));
        }

        var next = page.Scanned == size ? Encode(offset + page.Scanned) : null;
        return new FederationReportResult(FederationReportOutcome.Ok, items, next, page.Scanned, ignored, unread);
    }

    private async Task<FederationActResult> RemoveCoreAsync(
        PlatformAdmission admission, string brokerAlias, ActProgress progress, CancellationToken cancellationToken)
    {
        RequireOperation(admission, PlatformOperations.FederationRemove);
        await using var handle = await scope.EnterAsync(admission, cancellationToken);
        var inspection = await provider.InspectAsync(brokerAlias, cancellationToken);
        switch (inspection.Status)
        {
            case FederationInspectionStatus.Unavailable:
                return new FederationActResult(FederationActOutcome.Unavailable);
            case FederationInspectionStatus.Absent:
                // Deleting an absent broker is applied, and no tenant is known from nothing: no tenant row.
                return new FederationActResult(FederationActOutcome.AlreadyApplied);
            case FederationInspectionStatus.NotOwned:
                return new FederationActResult(FederationActOutcome.NotOwned);
        }

        var facts = inspection.Facts!;
        var owner = facts.Owner;
        var row = (await ReadRowsAsync(handle, [owner.ConnectionId], cancellationToken)).GetValueOrDefault(owner.ConnectionId);
        if (row is not null && row.TenantId != owner.TenantId.Value)
        {
            return new FederationActResult(FederationActOutcome.NotOwned);
        }

        if (RefusesRemoval(facts, row))
        {
            // Amendment 10 item 3 and Amendment 12 item 1: a live row refuses a broker that reads or is still enabled.
            return Known(FederationActOutcome.LiveConnection, owner);
        }

        var classes = ClassesOf(facts, row, inspection.MapperCount);
        await handle.WriteIntentAsync(Result(owner, classes, row, after: null), cancellationToken);

        // The row is read again immediately before the delete. A tombstone is terminal and a connection id is fresh, so
        // this protects against a stale reading and not against a race, and is cheap enough to keep.
        var again = (await ReadRowsAsync(handle, [owner.ConnectionId], cancellationToken)).GetValueOrDefault(owner.ConnectionId);
        FederationActOutcome outcome;
        string? cause = null;
        Exception? failure = null;
        if (RefusesRemoval(facts, again))
        {
            outcome = FederationActOutcome.LiveConnection;
            cause = "live_connection";
        }
        else
        {
            // A live row is let through only for a broker that does not read and is disabled, which the adapter proves
            // again at the delete.
            (outcome, failure) = await ChangeAsync(
                token => provider.RemoveAsync(owner, requireDisabledUnreadable: IsLive(again), token), progress, cancellationToken);
        }

        (outcome, failure) = await CompleteAsync(handle, owner, classes, row, outcome, cause, failure, progress);
        return Known(outcome, owner, failure);
    }

    private async Task<FederationActResult> DisableCoreAsync(
        PlatformAdmission admission, string brokerAlias, ActProgress progress, CancellationToken cancellationToken)
    {
        RequireOperation(admission, PlatformOperations.FederationDisable);
        await using var handle = await scope.EnterAsync(admission, cancellationToken);
        var inspection = await provider.InspectAsync(brokerAlias, cancellationToken);
        switch (inspection.Status)
        {
            case FederationInspectionStatus.Unavailable:
                return new FederationActResult(FederationActOutcome.Unavailable);
            case FederationInspectionStatus.Absent:
                return new FederationActResult(FederationActOutcome.NotFound);
            case FederationInspectionStatus.NotOwned:
                return new FederationActResult(FederationActOutcome.NotOwned);
        }

        var facts = inspection.Facts!;
        var owner = facts.Owner;
        var row = (await ReadRowsAsync(handle, [owner.ConnectionId], cancellationToken)).GetValueOrDefault(owner.ConnectionId);
        if (row is not null && row.TenantId != owner.TenantId.Value)
        {
            return new FederationActResult(FederationActOutcome.NotOwned);
        }

        var classes = ClassesOf(facts, row, inspection.MapperCount);
        if (classes.Count == 0)
        {
            // Nothing about a healthy broker with a live row needs an operator.
            return Known(FederationActOutcome.NotApplicable, owner);
        }

        if (!facts.Enabled)
        {
            return Known(FederationActOutcome.AlreadyApplied, owner);
        }

        await handle.WriteIntentAsync(Result(owner, classes, row, after: null), cancellationToken);
        var (outcome, failure) = await ChangeAsync(token => provider.DisableAsync(owner, token), progress, cancellationToken);
        (outcome, failure) = await CompleteAsync(handle, owner, classes, row, outcome, cause: null, failure, progress);
        return Known(outcome, owner, failure);
    }

    /// <summary>
    /// The provider's change. A caller that leaves, and any other failure after the change may have been sent, is an
    /// unknown outcome with its cause kept: the adapter rethrows the caller's cancellation without saying which side of the
    /// send it fell on, and an unexpected failure says nothing about whether the provider took the change. Either is
    /// recorded in the completion row, never skipped.
    /// </summary>
    private static async Task<(FederationActOutcome Outcome, Exception? Failure)> ChangeAsync(
        Func<CancellationToken, Task<FederationOperatorOutcome>> change, ActProgress progress, CancellationToken cancellationToken)
    {
        FederationActOutcome outcome;
        Exception? failure = null;
        try
        {
            outcome = Map(await change(cancellationToken));
        }
        catch (Exception caught) when (!IsFatal(caught))
        {
            outcome = FederationActOutcome.Uncertain;
            failure = caught;
        }

        progress.ChangeMayHaveLanded = outcome is FederationActOutcome.Applied or FederationActOutcome.Uncertain;
        return (outcome, failure);
    }

    /// <summary>
    /// Writes and commits the completion row on a bounded token detached from the request, and returns the outcome the
    /// caller is to answer with the failure behind it. A completion that cannot be written is the audit failure it is when
    /// nothing changed; after a change that landed or may have, the intent row stands alone and the answer is the unknown
    /// outcome with the completion's failure as its cause, never "nothing changed, repeat it" and never a silent swallow.
    /// </summary>
    private async Task<(FederationActOutcome Outcome, Exception? Failure)> CompleteAsync(
        IPlatformAdminScopeHandle handle, FederationBrokerOwner owner, IReadOnlyList<string> classes, Row? row,
        FederationActOutcome outcome, string? cause, Exception? failure, ActProgress progress)
    {
        var token = OutcomeToken(outcome);
        // The account review is owed for any change that may have landed: the provider removes federated links with a
        // broker, and nothing here reads them (ADR-0099 Amendment 10 item 7). A disable owes it too (Amendment 12 item 6).
        var owed = outcome is FederationActOutcome.Applied or FederationActOutcome.Uncertain;
        var after = JsonSerializer.Serialize(new AfterState(token, cause, owed), Json);
        using var budget = new CancellationTokenSource(_completionBudget);
        try
        {
            await handle.WriteResultAsync(Result(owner, classes, row, after), budget.Token);
            await handle.CommitAsync(budget.Token);
            return (outcome, failure);
        }
        catch (Exception lost) when (progress.ChangeMayHaveLanded && !IsFatal(lost))
        {
            return (FederationActOutcome.Uncertain, lost);
        }
    }

    private static PlatformResult Result(FederationBrokerOwner owner, IReadOnlyList<string> classes, Row? row, string? after) => new(
        owner.TenantId, EntityType, owner.ConnectionId.ToString("D", CultureInfo.InvariantCulture),
        JsonSerializer.Serialize(new BeforeState(classes, RowState(row)), Json), after);

    private static FederationActResult Known(FederationActOutcome outcome, FederationBrokerOwner owner, Exception? failure = null) =>
        new(outcome, owner.TenantId.Value, owner.ConnectionId, failure);

    private static FederationActOutcome Map(FederationOperatorOutcome outcome) => outcome switch
    {
        FederationOperatorOutcome.Applied => FederationActOutcome.Applied,
        FederationOperatorOutcome.AlreadyApplied => FederationActOutcome.AlreadyApplied,
        FederationOperatorOutcome.Refused => FederationActOutcome.Refused,
        FederationOperatorOutcome.NotOwned => FederationActOutcome.NotOwned,
        FederationOperatorOutcome.Unavailable => FederationActOutcome.Unavailable,
        _ => FederationActOutcome.Uncertain,
    };

    private static string OutcomeToken(FederationActOutcome outcome) => outcome switch
    {
        FederationActOutcome.Applied => "applied",
        FederationActOutcome.AlreadyApplied => "already_applied",
        FederationActOutcome.NotOwned => "not_owned",
        FederationActOutcome.LiveConnection => "refused",
        FederationActOutcome.Refused => "refused",
        FederationActOutcome.Unavailable => "unavailable",
        _ => "uncertain",
    };

    /// <summary>The classes of one broker: orphan by row, then unreadable, then nonconforming by profile or mapper.</summary>
    private static List<string> ClassesOf(FederationBrokerFacts facts, Row? row, int mappers)
    {
        var classes = new List<string>(3);
        if (!IsLive(row))
        {
            classes.Add("orphan");
        }

        if (!facts.Readable)
        {
            classes.Add("unreadable_owned");
        }
        else if (!facts.Conforms || mappers > 0)
        {
            classes.Add("nonconforming");
        }

        return classes;
    }

    /// <summary>
    /// A live connection row refuses the removal of a broker that reads: the tenant's own Delete retires that pair. A broker
    /// that does not read and is disabled is the exception (ADR-0099 Amendment 12 item 1 and Amendment 13 item 1), because
    /// the tenant's Delete treats it as foreign and fails (Amendment 5 item 5), so nothing else could ever remove it. An
    /// unreadable broker that is still enabled is refused: "unreadable" is a shape check, and the provider may still be
    /// serving it, so the operator disables it first, which is reversible, and removes it after.
    /// </summary>
    private static bool RefusesRemoval(FederationBrokerFacts facts, Row? row) => IsLive(row) && (facts.Readable || facts.Enabled);

    private static bool IsLive(Row? row) => row is { Deleted: false } && !string.Equals(row.State, "Deleted", StringComparison.Ordinal);

    private static string RowState(Row? row) => row is null ? "absent" : IsLive(row) ? row.State : "deleted";

    private static async Task<Dictionary<Guid, Row>> ReadRowsAsync(
        IPlatformAdminScopeHandle handle, Guid[] ids, CancellationToken cancellationToken)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        var rows = await handle.QueryAsync(
            RowQuery, [new PlatformSqlParameter("ids", ids)],
            reader => new Row(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetBoolean(3)), cancellationToken);
        return rows.ToDictionary(row => row.Id);
    }

    /// <summary>
    /// The mapper count of each owner, one after another, until the page's time runs out: the remainder is unknown, which
    /// the report counts as <see cref="FederationReportResult.Unread"/> and never as zero. A module runs one operation at a time (ADR-0040), and the provider's
    /// own bulkhead would refuse a wider fan-out.
    /// </summary>
    private async Task<Dictionary<string, int?>> CountMappersAsync(
        IReadOnlyList<FederationBrokerOwner> owners, CancellationToken deadline, CancellationToken caller)
    {
        var counts = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            try
            {
                counts[owner.Alias] = await provider.CountMappersAsync(owner, deadline);
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            {
                break;
            }
        }

        return counts;
    }

    private static void RequireOperation(PlatformAdmission admission, PlatformOperation expected)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (!ReferenceEquals(admission.Operation, expected))
        {
            throw new ArgumentException("The admission is not for this federation operation.", nameof(admission));
        }
    }

    private static string Encode(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes("v1~federation~" + offset.ToString(CultureInfo.InvariantCulture)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecode(string cursor, out int offset)
    {
        offset = 0;
        if (cursor.Length is 0 or > 64)
        {
            return false;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('~');
            return parts is ["v1", "federation", var number]
                && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out offset)
                && string.Equals(Encode(offset), cursor, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record Row(Guid Id, Guid TenantId, string State, bool Deleted);

    private sealed record BeforeState(IReadOnlyList<string> Classes, string RowState);

    private sealed record AfterState(string Outcome, string? Cause, bool AccountReviewOwed);
}
