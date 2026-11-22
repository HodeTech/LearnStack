using FluentAssertions;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AuditRedactionCanaryTests
{
    [Fact]
    public async Task A_closure_tenant_pass_redacts_exactly_what_the_whole_tenant_erasure_does_across_every_group_and_nothing_else()
    {
        // ADR-0108 § 4: a closure's tenant pass is the whole-tenant selection with every Membership the person holds in
        // the tenant, so one pass covers all of the person's groups there. The same rows, in a rolled-back transaction
        // each, go through a person's own erasure over the tenant-wide group and through the closure's pass: every declared
        // path's canary, the reach cases in two organizations and at the tenant level (a Membership of each group as
        // entity), another tenant's rows naming the same person, and a platform sentinel row whose actor is the person.
        var tenant = Guid.CreateVersion7();
        var foreign = Guid.CreateVersion7();
        var rows = Reach(tenant, foreign, Guid.CreateVersion7(), Guid.CreateVersion7())
            .Concat(Registered.Value.SelectMany(locator => locator.Redactable).SelectMany(declaration => Canary(declaration, tenant)))
            .Append(new CanaryRow("sentinel/actor", TenantId.PlatformSentinel.Value, null, Subject, "Course",
                Guid.CreateVersion7().ToString("D"), null, null, null, null, Selected: false, null, null, null))
            .ToList();
        var selected = rows.Where(row => row.Selected && row.Tenant == tenant).Select(row => row.Label).ToList();

        var erasure = await TenantPassAsync(rows, Plan(tenant, DataRequestOrigin.Self, organization: null), PlatformOperations.ErasureComplete);
        var closure = await TenantPassAsync(rows, ClosureTenantPlan(tenant), PlatformOperations.ClosureFinalize);

        closure.Changed.Should().BeEquivalentTo(selected, "the pass changes exactly the rows naming the person in the tenant");
        closure.Changed.Should().BeEquivalentTo(erasure.Changed);
        closure.After.Should().Equal(erasure.After, "each row's six columns read as the whole-tenant erasure leaves them");
        closure.Report.RowsSelected.Should().Be(erasure.Report.RowsSelected).And.Be(selected.Count);
        closure.Report.Redacted.Should().Equal(erasure.Report.Redacted);
        Retained(closure.Report).Should().Equal(Retained(erasure.Report)).And.ContainKeys(
            RetainedReferenceKind.AuditActorReference, RetainedReferenceKind.AuditEntityReference, RetainedReferenceKind.AuditMetadataReference)
            .And.NotContainKey(RetainedReferenceKind.UnscopedAuditRows, "a closure's pass reaches its whole tenant");
        rows.Where(row => row.Tenant != tenant).Select(row => row.Label).Should().NotIntersectWith(closure.Changed,
            "another tenant's rows and the platform sentinel's are outside the pass");
        closure.Again.Redacted.Values.Should().AllSatisfy(count => count.Should().Be(0), "a repeated pass rewrites nothing");
        closure.AfterAgain.Should().Equal(closure.After);
    }

    /// <summary>One redaction's run over the rows: the labels it changed, every row's six columns after it, and a re-run's.</summary>
    private sealed record TenantPass(AuditRedactionReport Report, List<string> Changed, Dictionary<Guid, string> After,
        AuditRedactionReport Again, Dictionary<Guid, string> AfterAgain);

    private async Task<TenantPass> TenantPassAsync(List<CanaryRow> rows, AuditRedactionPlan plan, PlatformOperation operation)
    {
        await using var scope = await OpenAsync(schema.Postgres.PlatformConnectionString);
        await InsertAsync(scope, rows);
        var before = await ReadAsync(scope, rows);
        var report = await Executor().RedactAsync(plan, Spent(operation), scope.Connection, scope.Transaction);
        var after = await ReadAsync(scope, rows);
        var again = await Executor().RedactAsync(plan, Spent(operation), scope.Connection, scope.Transaction);
        var afterAgain = await ReadAsync(scope, rows);
        await scope.Transaction.RollbackAsync();

        // The six columns only: the rest of a row carries the insert's own clock, which differs between the two runs.
        static Dictionary<Guid, string> Columns(Dictionary<Guid, Snapshot> read) => read.ToDictionary(entry => entry.Key,
            entry => string.Join('|', entry.Value.IpAddress, entry.Value.UserAgent, entry.Value.ActorEmail, entry.Value.BeforeText,
                entry.Value.AfterText, entry.Value.ChangesText));

        return new TenantPass(report, [.. rows.Where(row => after[row.Id] != before[row.Id]).Select(row => row.Label)], Columns(after),
            again, Columns(afterAgain));
    }
}
