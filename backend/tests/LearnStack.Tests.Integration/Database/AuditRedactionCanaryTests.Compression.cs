using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Platform;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AuditRedactionCanaryTests
{
    // Forty payloads of 200,000 repeated characters: each compresses to a few kilobytes, so by stored size the whole
    // tenant is far below this budget, and by decompressed size every second row reaches it.
    private const int SqueezedRows = 40;
    private const int SqueezedCharacters = 200_000;
    private const long SqueezedBudget = 300_000;

    [Fact]
    public async Task A_byte_budget_counts_a_compressed_payload_at_its_decompressed_size()
    {
        var tenant = Guid.CreateVersion7();
        var rows = Squeezed(tenant);
        var single = await SqueezedRunAsync(tenant, rows, Executor());
        var budgeted = await SqueezedRunAsync(tenant, rows,
            new AuditRedactionExecutor(Registered.Value, int.MaxValue, SqueezedBudget, 1_000));

        // The rows are what the case claims: compressed, at under a twentieth of their decompressed size, so a budget read
        // from stored sizes would draw one batch where the decompressed rule draws one per two rows.
        budgeted.Sizes.Should().HaveCount(rows.Count);
        budgeted.Sizes.Should().OnlyContain(size => size.Compressed && size.Stored * 20 < size.Decompressed,
            "a payload of repeated characters is stored compressed");
        Batches(budgeted.Sizes.Select(size => size.Stored)).Should().Be(1, "stored sizes alone never reach the budget");
        var planned = Batches(budgeted.Sizes.Select(size => size.Decompressed));
        planned.Should().Be(SqueezedRows / 2);

        // Every batch's boundary statement reads the log at least once, whatever plan serves it: the executor drew at
        // least the planned batches, where a stored-size budget would have drawn one.
        budgeted.Scans.Should().BeGreaterThanOrEqualTo(planned, "the budget is reached by decompressed bytes");

        budgeted.After.Should().Equal(single.After);
        budgeted.Report.RowsSelected.Should().Be(rows.Count(row => row.Selected)).And.Be(single.Report.RowsSelected);
        budgeted.Report.Redacted.Should().Equal(single.Report.Redacted);
        Retained(budgeted.Report).Should().Equal(Retained(single.Report));
        rows.Where(row => budgeted.After[row.Id] != budgeted.Before[row.Id]).Select(row => row.Label).Should()
            .BeEquivalentTo(rows.Where(row => row.Selected).Select(row => row.Label), "exactly the selected rows change");

        // Outside the reach a payload is never walked, so it counts no bytes: an organization group's request over the
        // same tenant-level rows draws one batch, where counting them would draw the twenty above.
        var branch = await SqueezedRunAsync(tenant, rows,
            new AuditRedactionExecutor(Registered.Value, int.MaxValue, SqueezedBudget, 1_000), Guid.CreateVersion7());
        branch.Report.RowsSelected.Should().Be(0, "every row is at the tenant level, outside the organization group");
        branch.Scans.Should().BeLessThan(planned, "rows outside the reach end no batch");
    }

    /// <summary>
    /// Rows one second apart, newest first, each a Course whose state holds <see cref="SqueezedCharacters"/> repeated
    /// characters; the subject created three of them and acted in one, and everyone else's are left alone.
    /// </summary>
    private static List<CanaryRow> Squeezed(Guid tenant)
    {
        var start = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-2), DateTimeKind.Utc);
        start = start.AddTicks(-(start.Ticks % TimeSpan.TicksPerMillisecond));
        var description = new string('a', SqueezedCharacters);
        return
        [
            .. Enumerable.Range(0, SqueezedRows).Select(index =>
            {
                var creator = index % 13 == 1 ? Subject : OtherPerson;
                var actor = index == 20 ? Subject : ThirdPerson;
                var state = new JsonObject
                {
                    ["Id"] = Guid.CreateVersion7().ToString("D"),
                    ["Description"] = description,
                    ["CreatedBy"] = Text(creator),
                    ["UpdatedBy"] = Text(OtherPerson),
                };
                return new CanaryRow($"squeezed {index}", tenant, null, actor, "Course", Guid.CreateVersion7().ToString("D"), null,
                    state, null, null, Selected: creator == Subject || actor == Subject, null, null, null)
                {
                    At = start.AddSeconds(-index),
                };
            }),
        ];
    }

    /// <summary>How many batches the documented rule draws over sizes taken newest first, by bytes alone.</summary>
    private static int Batches(IEnumerable<long> sizes)
    {
        var (batches, open, bytes) = (0, false, 0L);
        foreach (var size in sizes)
        {
            (open, bytes) = (true, bytes + size);
            if (bytes >= SqueezedBudget)
            {
                (batches, open, bytes) = (batches + 1, false, 0L);
            }
        }

        return batches + (open ? 1 : 0);
    }

    /// <summary>One row's payload as stored: whether it is compressed, its stored size and its decompressed size.</summary>
    private sealed record PayloadSize(bool Compressed, long Stored, long Decompressed);

    /// <summary>One executor's run over the rows in its own rolled-back transaction.</summary>
    private sealed record SqueezedRun(AuditRedactionReport Report, Dictionary<Guid, Snapshot> Before, Dictionary<Guid, Snapshot> After,
        long Scans, List<PayloadSize> Sizes);

    private async Task<SqueezedRun> SqueezedRunAsync(Guid tenant, List<CanaryRow> rows, AuditRedactionExecutor executor,
        Guid? organization = null)
    {
        // An executor that never moved its upper bound would loop; the token turns that into a failure.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var scope = await OpenAsync(schema.Postgres.PlatformConnectionString);
        await InsertAsync(scope, rows);
        var before = await ReadAsync(scope, rows);
        var sizes = new List<PayloadSize>();
        await using (var command = new NpgsqlCommand("""
            SELECT pg_column_compression(a.after_state) IS NOT NULL, pg_column_size(a.after_state), pg_column_size(a.after_state #> '{}')
            FROM public.audit_log AS a WHERE a.tenant_id = @tenant ORDER BY a."timestamp" DESC, a.id DESC
            """, scope.Connection, scope.Transaction))
        {
            command.Parameters.AddWithValue("tenant", tenant);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sizes.Add(new PayloadSize(reader.GetBoolean(0), reader.GetInt32(1), reader.GetInt32(2)));
            }
        }

        var scans = await TableScansAsync(scope);
        var plan = organization is null
            ? Plan(tenant, DataRequestOrigin.Self, null)
            : Plan(tenant, DataRequestOrigin.Administrator, organization);
        var report = await executor.RedactAsync(plan, Spent(PlatformOperations.ErasureComplete), scope.Connection, scope.Transaction,
            deadline.Token);
        scans = await TableScansAsync(scope) - scans;
        var after = await ReadAsync(scope, rows);
        await scope.Transaction.RollbackAsync();
        return new SqueezedRun(report, before, after, scans, sizes);
    }

    /// <summary>How many scans of <c>audit_log</c>, sequential or by any index, this transaction has started so far.</summary>
    private static async Task<long> TableScansAsync(Scope scope)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(seq_scan, 0) + COALESCE(idx_scan, 0) FROM pg_stat_xact_user_tables
            WHERE schemaname = 'public' AND relname = 'audit_log'
            """, scope.Connection, scope.Transaction);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
