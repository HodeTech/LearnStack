using System.Runtime.CompilerServices;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The real platform scope with taps, for the closure's coverage cases that need a fault or an observation at one exact
/// statement of the act: the backend the act runs on, a hook before every statement (which may change the world the
/// statement is about to meet), a result row that cannot be written, and a commit whose answer is lost after it landed.
/// Every statement still runs on the real scope, in the real transaction, as <c>learnstack_platform</c>.
/// </summary>
internal sealed class TappedScope(IPlatformAdminScope inner) : IPlatformAdminScope
{
    /// <summary>Whether the act's own backend is read, once, as the act enters its scope.</summary>
    public bool RecordBackend { get; init; }

    /// <summary>The server process of the connection the act ran on, when <see cref="RecordBackend"/> is set.</summary>
    public int? Backend { get; private set; }

    /// <summary>Run before each statement or query of the act, with its text.</summary>
    public Func<string, Task>? BeforeStatement { get; set; }

    /// <summary>Replaces the write of a result row: it throws, or never completes before its token does.</summary>
    public Func<PlatformResult, CancellationToken, Task>? ReplaceWriteResult { get; set; }

    /// <summary>Run before a commit is sent, to hold the act where it holds every fence and has written everything.</summary>
    public Func<Task>? BeforeCommit { get; set; }

    /// <summary>Run after a commit landed; it throws to lose the answer.</summary>
    public Func<Task>? AfterCommit { get; set; }

    public async Task<IPlatformAdminScopeHandle> EnterAsync(
        PlatformAdmission admission, CancellationToken cancellationToken = default,
        [CallerMemberName] string? callerMember = null,
        [CallerFilePath] string? callerFile = null,
        [CallerLineNumber] int callerLine = 0)
    {
        var real = await inner.EnterAsync(admission, cancellationToken, callerMember, callerFile, callerLine);
        if (RecordBackend)
        {
            Backend = (await real.QueryAsync(
                "SELECT pg_catalog.pg_backend_pid()", null, reader => reader.GetInt32(0), cancellationToken)).Single();
        }

        return new Handle(real, this);
    }

    private sealed class Handle(IPlatformAdminScopeHandle real, TappedScope tap) : IPlatformAdminScopeHandle
    {
        public async Task<int> ExecuteAsync(
            string sql, IReadOnlyList<PlatformSqlParameter>? parameters = null, CancellationToken cancellationToken = default)
        {
            if (tap.BeforeStatement is { } before)
            {
                await before(sql);
            }

            return await real.ExecuteAsync(sql, parameters, cancellationToken);
        }

        public async Task<IReadOnlyList<T>> QueryAsync<T>(
            string sql, IReadOnlyList<PlatformSqlParameter>? parameters, Func<System.Data.Common.DbDataReader, T> map,
            CancellationToken cancellationToken = default)
        {
            if (tap.BeforeStatement is { } before)
            {
                await before(sql);
            }

            return await real.QueryAsync(sql, parameters, map, cancellationToken);
        }

        public Task WriteIntentAsync(PlatformResult intent, CancellationToken cancellationToken = default) =>
            real.WriteIntentAsync(intent, cancellationToken);

        public Task<AuditRedactionReport> RedactAuditAsync(AuditRedactionPlan plan, CancellationToken cancellationToken = default) =>
            real.RedactAuditAsync(plan, cancellationToken);

        public Task WriteResultAsync(PlatformResult result, CancellationToken cancellationToken = default) =>
            tap.ReplaceWriteResult is { } replaced ? replaced(result, cancellationToken) : real.WriteResultAsync(result, cancellationToken);

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (tap.BeforeCommit is { } before)
            {
                await before();
            }

            await real.CommitAsync(cancellationToken);
            if (tap.AfterCommit is { } after)
            {
                await after();
            }
        }

        public ValueTask DisposeAsync() => real.DisposeAsync();
    }
}

/// <summary>
/// The fan-out's stand-in that answers whatever a test scripts, including answers a well-behaved fan-out never gives: a
/// null list, a null element, a group nobody handed over, an outcome outside the closed set, the same group twice, a
/// cancellation. It records the groups it was handed.
/// </summary>
internal sealed class ScriptedFanOut(
    Func<ClosureFanOutRequest, CancellationToken, Task<IReadOnlyList<ClosureGroupReport>?>> script) : IClosureFanOut
{
    public List<ClosureFanOutRequest> Calls { get; } = [];

    public async Task<IReadOnlyList<ClosureGroupReport>> FanOutAsync(
        PlatformOperatorWitness operatorWitness, string? ticket, ClosureFanOutRequest request,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(request);

        // The port's contract is a list; a null answer is the defect the owner must still survive.
        return (await script(request, cancellationToken))!;
    }
}

/// <summary>The audit rows a coverage case plants to make a redaction assertion non-vacuous.</summary>
internal static class AccountClosureCanary
{
    /// <summary>
    /// A row of the tenant whose entity is the Membership and whose after-state names the person, as the Membership's own
    /// audit rows do: selected by the Membership's identifier, and its reference to the person redactable. Written as the
    /// platform role, which holds the audit log's INSERT.
    /// </summary>
    internal static async Task<Guid> PlantMembershipRowAsync(string platformConnectionString, Guid tenant, Guid membership, Guid person)
    {
        var id = Guid.CreateVersion7();
        await using var platform = await AccountClosureBeginKit.OpenAsync(platformConnectionString);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO public.audit_log (id, "timestamp", tenant_id, organization_id, actor_user_id, module, operation, operation_type,
                operation_class, entity_type, entity_id, outcome, correlation_id, after_state)
            VALUES (@id, clock_timestamp(), @tenant, NULL, NULL, 'canary', 'canary.membership.row', 'Update', 'Must', 'Membership',
                @entity, 'success', @correlation, jsonb_build_object('UserId', @person::text))
            """, platform);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("tenant", tenant);
        insert.Parameters.AddWithValue("entity", membership.ToString("D"));
        insert.Parameters.AddWithValue("correlation", "00-" + id.ToString("N") + "-01");
        insert.Parameters.AddWithValue("person", person);
        await insert.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>
    /// A row of the tenant that names only the Membership: an invitation whose result is that Membership, with no actor and no
    /// reference to the person. Only a redaction that compares the Membership's identifier selects it.
    /// </summary>
    internal static async Task<Guid> PlantMembershipReferenceRowAsync(string platformConnectionString, Guid tenant, Guid membership)
    {
        var id = Guid.CreateVersion7();
        await using var platform = await AccountClosureBeginKit.OpenAsync(platformConnectionString);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO public.audit_log (id, "timestamp", tenant_id, organization_id, actor_user_id, module, operation, operation_type,
                operation_class, entity_type, entity_id, outcome, correlation_id, after_state)
            VALUES (@id, clock_timestamp(), @tenant, NULL, NULL, 'canary', 'canary.invitation.row', 'Update', 'Must', 'Invitation',
                @entity, 'success', @correlation, jsonb_build_object('ResultMembershipId', @membership::text))
            """, platform);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("tenant", tenant);
        insert.Parameters.AddWithValue("entity", Guid.CreateVersion7().ToString("D"));
        insert.Parameters.AddWithValue("correlation", "00-" + id.ToString("N") + "-01");
        insert.Parameters.AddWithValue("membership", membership);
        await insert.ExecuteNonQueryAsync();
        return id;
    }
}
