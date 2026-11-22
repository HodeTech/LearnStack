using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AuditRedactionCanaryTests
{
    // Sizes small enough that a few dozen rows cross every kind of batch boundary the executor can draw.
    private const int TinyBatchRows = 3;
    private const long TinyBatchBytes = 6_000;
    private const int TinyUpdateRows = 2;

    [Fact]
    public async Task Small_batches_and_chunks_select_and_redact_exactly_what_one_batch_does()
    {
        // The same rows in a rolled-back transaction each: through the production sizes, which read this tenant in one
        // batch; through batches of three rows or six kilobytes and UPDATEs of two keys; and through each budget alone.
        var tenant = Guid.CreateVersion7();
        var rows = Batched(tenant, Guid.CreateVersion7());
        var single = await BatchRunAsync(tenant, rows, Executor());
        var tiny = await BatchRunAsync(tenant, rows,
            new AuditRedactionExecutor(Registered.Value, TinyBatchRows, TinyBatchBytes, TinyUpdateRows), plan: true, again: true);
        var rowsOnly = await BatchRunAsync(tenant, rows,
            new AuditRedactionExecutor(Registered.Value, TinyBatchRows, long.MaxValue, TinyUpdateRows));
        var bytesOnly = await BatchRunAsync(tenant, rows,
            new AuditRedactionExecutor(Registered.Value, int.MaxValue, TinyBatchBytes, TinyUpdateRows));

        // The data crosses every boundary it claims to, by the executor's documented rule read from the stored sizes.
        var batches = tiny.Planned!;
        var label = rows.ToDictionary(row => row.Id);
        batches.Count.Should().BeGreaterThan(6, "the rows span many small batches");
        batches.SkipLast(1).Should().Contain(batch => label[batch[batch.Count - 1].Id].Selected, "a boundary falls exactly on a selected row");
        batches.Zip(batches.Skip(1)).Should().Contain(pair => pair.First[pair.First.Count - 1].Timestamp == pair.Second[0].Timestamp,
            "a block of identical timestamps straddles a boundary");
        batches.SkipLast(1).Should().Contain(batch => batch.Count < TinyBatchRows && batch.Count > 1,
            "the byte budget ends a batch before the row cap");
        batches.Should().Contain(batch => batch.Count == 1 && batch[0].Bytes > TinyBatchBytes,
            "a single row larger than the budget is a batch of its own");
        batches.Should().Contain(batch => batch.Count(row => label[row.Id].Selected) > TinyUpdateRows,
            "one batch selects more rows than one UPDATE rewrites");

        // And the executor drew exactly them. Each batch starts exactly one boundary statement, so the statements the
        // connection ran are the batches drawn: the one-batch run draws one, and a last batch the rule closes is followed by
        // an empty one. Counting statements, not scans of an index, keeps the proof independent of the planner, whose choice
        // among the tenant, actor and entity indexes moves with table statistics. With both budgets there are more
        // batches than with either alone.
        var iterations = batches.Count + (Closed(batches[^1]) ? 1 : 0);
        single.Boundaries.Should().Be(1, "the production sizes read this tenant in one batch");
        tiny.Boundaries.Should().Be(iterations, "the executor draws the {0} batches the rule draws", iterations);
        tiny.Boundaries.Should().BeGreaterThan(rowsOnly.Boundaries, "the byte budget ends batches")
            .And.BeGreaterThan(bytesOnly.Boundaries, "the row cap ends batches");
        single.Scans.Should().BeGreaterThan(0, "the reads were served by an index, since the planner may not scan sequentially");
        tiny.Scans.Should().BeGreaterThan(single.Scans, "many batches start many more index scans than one");

        // Exactly the selected rows change, and every run leaves every row as the one batch does and reports the same.
        tiny.After.Where(entry => entry.Value != tiny.Before[entry.Key]).Select(entry => label[entry.Key].Label).Should()
            .BeEquivalentTo(rows.Where(row => row.Selected).Select(row => row.Label), "exactly the selected rows change");
        foreach (var run in new[] { tiny, rowsOnly, bytesOnly })
        {
            run.After.Should().Equal(single.After);
            run.Report.RowsSelected.Should().Be(rows.Count(row => row.Selected)).And.Be(single.Report.RowsSelected);
            run.Report.Redacted.Should().Equal(single.Report.Redacted);
            Retained(run.Report).Should().Equal(Retained(single.Report));
        }

        foreach (var row in rows.Where(row => row.Selected))
        {
            var after = tiny.After[row.Id];
            (after.IpAddress, after.UserAgent, after.ActorEmail).Should().Be((null, null, null), row.Label);
            new[] { after.BeforeText, after.AfterText, after.ChangesText }.Should()
                .NotContain(text => text != null && text.Contains(Text(Subject), StringComparison.OrdinalIgnoreCase), row.Label);
        }

        Retained(tiny.Report)[RetainedReferenceKind.AuditMetadataReference].Should()
            .Be(rows.Count(row => row.Tenant == tenant && row.Label.Contains("metadata", StringComparison.Ordinal)));
        tiny.Again!.Redacted.Values.Should().AllSatisfy(count => count.Should().Be(0), "a second run changes no row");
        Retained(tiny.Again).Should().Equal(Retained(tiny.Report));
        tiny.AfterAgain.Should().Equal(tiny.After);
    }

    /// <summary>One executor's run over the rows: what it reported, what the rows read before and after, and how many
    /// scans of the timestamp index and how many boundary statements it started; optionally the planned batches and a
    /// second run's report.</summary>
    private sealed record BatchRun(AuditRedactionReport Report, Dictionary<Guid, Snapshot> Before, Dictionary<Guid, Snapshot> After,
        long Scans, int Boundaries, List<List<BatchRow>>? Planned, AuditRedactionReport? Again, Dictionary<Guid, Snapshot>? AfterAgain);

    private async Task<BatchRun> BatchRunAsync(Guid tenant, List<CanaryRow> rows, AuditRedactionExecutor executor, bool plan = false,
        bool again = false)
    {
        // An executor that never moved its upper bound would loop; the token turns that into a failure.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var statements = new StatementLog();
        await using var source = new NpgsqlDataSourceBuilder(schema.Postgres.PlatformConnectionString)
            .UseLoggerFactory(LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(statements)))
            .Build();
        var connection = await source.OpenConnectionAsync();
        await using var scope = new Scope(connection, await connection.BeginTransactionAsync());

        // The scan count is the proof, so the planner may not pick another access path for a statement: a sequential or
        // bitmap scan on a small or freshly analysed table would otherwise make the count depend on statistics.
        await using (var settings = new NpgsqlCommand("SET LOCAL enable_seqscan = off; SET LOCAL enable_bitmapscan = off", scope.Connection,
            scope.Transaction))
            await settings.ExecuteNonQueryAsync();
        await InsertAsync(scope, rows);
        var before = await ReadAsync(scope, rows);
        var planned = plan ? await PlannedBatchesAsync(scope, tenant) : null;
        var scans = await ScansAsync(scope);
        statements.Clear();
        var report = await executor.RedactAsync(Plan(tenant, DataRequestOrigin.Self, null), Spent(PlatformOperations.ErasureComplete),
            scope.Connection, scope.Transaction, deadline.Token);
        scans = await ScansAsync(scope) - scans;
        var boundaries = statements.Count(BoundaryMarker);
        var after = await ReadAsync(scope, rows);
        var second = again
            ? await executor.RedactAsync(Plan(tenant, DataRequestOrigin.Self, null), Spent(PlatformOperations.ErasureComplete),
                scope.Connection, scope.Transaction, deadline.Token)
            : null;
        var afterAgain = again ? await ReadAsync(scope, rows) : null;
        await scope.Transaction.RollbackAsync();
        return new BatchRun(report, before, after, scans, boundaries, planned, second, afterAgain);
    }

    /// <summary>
    /// Rows of one tenant, newest first one second apart, that name the subject as actor, as entity and in a payload, name
    /// it only in metadata, or name someone else; two of them large enough to end a batch on bytes, one larger than the
    /// whole budget, and a block of six sharing one timestamp. Another tenant's rows sit at the same times.
    /// </summary>
    private static List<CanaryRow> Batched(Guid tenant, Guid foreign)
    {
        var subject = Text(Subject);
        var administrator = Text(OtherPerson);
        var start = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-1), DateTimeKind.Utc);
        start = start.AddTicks(-(start.Ticks % TimeSpan.TicksPerMillisecond));
        var tie = start.AddSeconds(-10);
        var next = 0;
        DateTime At() => start.AddSeconds(-next++);
        static string Noise(int characters) =>
            string.Concat(Enumerable.Range(0, (characters / 32) + 1).Select(_ => Guid.NewGuid().ToString("N")))[..characters];
        JsonObject Course(string creator, int noise = 0) => new()
        {
            ["Id"] = Guid.CreateVersion7().ToString("D"),
            ["CreatedBy"] = creator,
            ["UpdatedBy"] = administrator,
            ["Description"] = noise > 0 ? Noise(noise) : "short",
        };

        CanaryRow Actor(string label, DateTime at, int noise = 0) =>
            new(label, tenant, null, Subject, "Course", Guid.CreateVersion7().ToString("D"), null, Course(administrator, noise), null,
                null, Selected: true, null, null, null)
            { At = at };
        CanaryRow Payload(string label, DateTime at, int noise = 0) =>
            new(label, tenant, null, OtherPerson, "Course", Guid.CreateVersion7().ToString("D"), Course(subject, noise),
                Course(subject, noise), null, null, Selected: true, null, null, null)
            { At = at };
        CanaryRow Entity(string label, DateTime at, Guid membership) =>
            new(label, tenant, null, OtherPerson, "Membership", Text(membership),
                new JsonObject { ["Id"] = Text(membership), ["UserId"] = subject, ["UpdatedBy"] = administrator }, null, null, null,
                Selected: true, null, null, null)
            { At = at };
        CanaryRow Metadata(string label, DateTime at) =>
            new(label, tenant, null, UserId.SystemActor.Value, "Course", Guid.CreateVersion7().ToString("D"), null,
                Course(administrator), null, $$"""{"causal_actor_user_id":"{{subject}}"}""", Selected: false, null, null, null)
            {
                At = at,
                Attributed = false,
            };
        CanaryRow Other(string label, DateTime at, int noise = 0) =>
            new(label, tenant, null, OtherPerson, "Course", Guid.CreateVersion7().ToString("D"), null, Course(administrator, noise), null,
                null, Selected: false, null, null, null)
            { At = at };

        List<CanaryRow> rows =
        [
            Actor("actor 1", At()), Other("other 1", At()), Payload("payload 1", At()),
            Other("large other", At(), noise: 3_500), Payload("large payload", At(), noise: 3_500),
            Actor("oversized actor", At(), noise: 9_000),
            Metadata("metadata 1", At()), Entity("entity 1", At(), Membership1),
            .. Tied(Actor("tie actor 1", tie), Payload("tie payload 1", tie), Entity("tie entity", tie, Membership2),
                Actor("tie actor 2", tie), Metadata("tie metadata", tie), Payload("tie payload 2", tie)),
        ];
        next = 11;
        rows.AddRange(
        [
            Other("other 2", At()), Actor("actor 2", At()), Metadata("metadata 2", At()), Payload("payload 2", At()),
            Other("other 3", At()), Other("other 4", At()), Entity("entity 2", At(), Membership1), Actor("actor 3", At()),
            Payload("payload 3", At()), Other("other 5", At()),
        ]);
        rows.AddRange(rows.Where(row => row.Label.StartsWith("actor", StringComparison.Ordinal)).ToList()
            .Select(row => Actor("foreign " + row.Label, row.At!.Value) with { Tenant = foreign, Selected = false }));
        return rows;
    }

    /// <summary>
    /// Rows sharing one timestamp, given identifiers that order them as listed in the batches' order (newest first, so by
    /// <c>id</c> descending): the boundaries then fall inside the block at known rows, whatever identifiers are drawn.
    /// </summary>
    private static IEnumerable<CanaryRow> Tied(params CanaryRow[] rows)
    {
        var identifiers = rows.Select(_ => Guid.CreateVersion7()).OrderByDescending(id => id.ToString("D"), StringComparer.Ordinal).ToList();
        return rows.Select((row, index) => row with { Id = identifiers[index] });
    }

    /// <summary>Whether the rule closes a planned batch at its last row (a full batch), rather than the rows running out.</summary>
    private static bool Closed(List<BatchRow> batch) => batch.Count == TinyBatchRows || batch.Sum(row => row.Bytes) >= TinyBatchBytes;

    /// <summary>The text only the executor's boundary statement carries.</summary>
    private const string BoundaryMarker = "count(*) OVER batch";

    /// <summary>The commands one connection completed, as the driver logs them.</summary>
    private sealed class StatementLog : ILoggerProvider, ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _commands = new();

        public ILogger CreateLogger(string categoryName) => this;

        public void Clear() => _commands.Clear();

        /// <summary>The commands that ran to completion and carry <paramref name="marker"/>; the start of each is logged too.</summary>
        public int Count(string marker) => _commands.Count(command =>
            command.StartsWith("Command execution completed", StringComparison.Ordinal) && command.Contains(marker, StringComparison.Ordinal));

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _commands.Enqueue(formatter(state, exception));

        public void Dispose()
        {
        }
    }

    /// <summary>One row as the batches order and size it.</summary>
    private sealed record BatchRow(Guid Id, DateTime Timestamp, long Bytes);

    /// <summary>
    /// The batches the executor's documented rule draws over the tenant's rows, newest first by <c>("timestamp", id)</c>:
    /// a batch ends at the row that makes it <see cref="TinyBatchRows"/> rows or brings its stored payload to
    /// <see cref="TinyBatchBytes"/>.
    /// </summary>
    private static async Task<List<List<BatchRow>>> PlannedBatchesAsync(Scope scope, Guid tenant)
    {
        await using var command = new NpgsqlCommand("""
            SELECT a.id, a."timestamp", COALESCE(pg_column_size(a.before_state), 0) + COALESCE(pg_column_size(a.after_state), 0)
                + COALESCE(pg_column_size(a.changes), 0)
            FROM public.audit_log AS a WHERE a.tenant_id = @tenant ORDER BY a."timestamp" DESC, a.id DESC
            """, scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("tenant", tenant);
        var batches = new List<List<BatchRow>> { new() };
        long bytes = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new BatchRow(reader.GetGuid(0), reader.GetDateTime(1), reader.GetInt32(2));
            batches[^1].Add(row);
            bytes += row.Bytes;
            if (batches[^1].Count == TinyBatchRows || bytes >= TinyBatchBytes)
            {
                batches.Add([]);
                bytes = 0;
            }
        }

        return [.. batches.Where(batch => batch.Count > 0)];
    }

    /// <summary>
    /// How many scans of <c>ix_audit_log_tenant_id_timestamp</c> this transaction has started so far: every batch's
    /// boundary statement starts one, so the count bounds the batches the executor drew from below.
    /// </summary>
    private static async Task<long> ScansAsync(Scope scope)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_stat_get_xact_numscans('public.ix_audit_log_tenant_id_timestamp'::regclass)", scope.Connection, scope.Transaction);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
