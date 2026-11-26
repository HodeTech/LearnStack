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
    private const string ContainmentMarker = "'lax $.**{1 to 3}') ?|";
    private const string SetMarker = "jsonb_array_elements_text(";

    /// <summary>
    /// The cheap payload filter's two forms answer the same question. A closure's pass at the tenant bound (the subject's two
    /// Memberships and 254 that no row names, so 514 spellings, which PostgreSQL hashes) selects and redacts exactly the
    /// same rows, with the same report, through the containment test and through the set lookup, over rows that name the
    /// subject as actor, as entity, in a payload and only in metadata. By default that pass takes the set lookup, and an
    /// erasure's few identifiers keep the containment test.
    /// </summary>
    [Fact]
    public async Task The_set_lookup_selects_and_redacts_exactly_what_the_containment_test_does()
    {
        var tenant = Guid.CreateVersion7();
        var rows = Batched(tenant, Guid.CreateVersion7());
        var plan = AuditRedactionPlan.ForClosureTenant(TenantId.From(tenant), UserId.From(Subject),
            [Membership1, Membership2, .. Enumerable.Range(0, ClosureLimits.MaximumTenantMemberships - 2).Select(_ => Guid.CreateVersion7())]);

        var containment = await LookupRunAsync(rows, plan, PlatformOperations.ClosureFinalize, setLookupFrom: int.MaxValue);
        var set = await LookupRunAsync(rows, plan, PlatformOperations.ClosureFinalize, setLookupFrom: 1);
        var byDefault = await LookupRunAsync(rows, plan, PlatformOperations.ClosureFinalize, AuditRedactionExecutor.SetLookupFrom);
        var erasure = await LookupRunAsync(rows, Plan(tenant, DataRequestOrigin.Self, null), PlatformOperations.ErasureComplete,
            AuditRedactionExecutor.SetLookupFrom);

        (containment.Containment, containment.Set).Should().Match<(int Containment, int Set)>(count => count.Containment > 0 && count.Set == 0,
            "the containment run used that form alone");
        (set.Containment, set.Set).Should().Match<(int Containment, int Set)>(count => count.Containment == 0 && count.Set > 0,
            "the set run used that form alone");
        byDefault.Set.Should().BeGreaterThan(0, "a closure pass of the tenant bound takes the set lookup by default");
        erasure.Containment.Should().BeGreaterThan(0, "an erasure's two Memberships keep the containment test by default");
        erasure.Set.Should().Be(0);

        set.After.Should().Equal(containment.After);
        byDefault.After.Should().Equal(containment.After);
        var label = rows.ToDictionary(row => row.Id);
        set.After.Where(entry => entry.Value != set.Before[entry.Key]).Select(entry => label[entry.Key].Label).Should()
            .BeEquivalentTo(rows.Where(row => row.Selected).Select(row => row.Label), "exactly the selected rows change");
        set.Report.RowsSelected.Should().Be(containment.Report.RowsSelected).And.Be(rows.Count(row => row.Selected));
        set.Report.Redacted.Should().Equal(containment.Report.Redacted);
        Retained(set.Report).Should().Equal(Retained(containment.Report));
    }

    /// <summary>One run's report, the rows before and after it, and how many selections each form of the filter ran.</summary>
    private sealed record LookupRun(AuditRedactionReport Report, Dictionary<Guid, Snapshot> Before, Dictionary<Guid, Snapshot> After,
        int Containment, int Set);

    /// <summary>The rows inserted and redacted by an executor with the given set-lookup bound, in a rolled-back transaction.</summary>
    private async Task<LookupRun> LookupRunAsync(List<CanaryRow> rows, AuditRedactionPlan plan, PlatformOperation operation,
        int setLookupFrom)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var statements = new StatementLog();
        await using var source = new NpgsqlDataSourceBuilder(schema.Postgres.PlatformConnectionString)
            .UseLoggerFactory(LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(statements)))
            .Build();
        var connection = await source.OpenConnectionAsync();
        await using var scope = new Scope(connection, await connection.BeginTransactionAsync());
        await InsertAsync(scope, rows);
        var before = await ReadAsync(scope, rows);
        statements.Clear();
        var executor = new AuditRedactionExecutor(Registered.Value, AuditRedactionExecutor.BatchRows, AuditRedactionExecutor.BatchBytes,
            AuditRedactionExecutor.UpdateRows, setLookupFrom);
        var report = await executor.RedactAsync(plan, Spent(operation), scope.Connection, scope.Transaction, deadline.Token);
        var run = new LookupRun(report, before, await ReadAsync(scope, rows), statements.Count(ContainmentMarker),
            statements.Count(SetMarker));
        await scope.Transaction.RollbackAsync();
        return run;
    }
}
