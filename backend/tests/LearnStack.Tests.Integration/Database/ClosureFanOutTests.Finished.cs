using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Platform;
using LearnStack.Tests.Fixtures;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class ClosureFanOutTests
{
    /// <summary>
    /// A repeated Begin's fan-out that reaches a group after the closure's Finalize committed (it read the person Closing
    /// just before) finds the group's fan-out erasure completed: only Finalize completes one, in the transaction that closes
    /// the person. The group is adopted with nothing stored and nothing designated, so its MUST row names neither the closed
    /// person nor a request, and no failure row carries the person's identifiers into the tenant's trail after the closure's
    /// redaction.
    /// </summary>
    [Fact]
    public async Task A_group_whose_fan_out_the_closure_completed_is_adopted_with_a_row_that_names_no_one()
    {
        await ArrangeAsync();
        await CloseAsync(PersonA);
        await using var host = Host(new Grants(true));
        (await FanOutAsync(host, [TenantWideA])).Should().Equal([new ClosureGroupReport(TenantWideA, ClosureGroupOutcome.Started)]);
        var request = (await RequestsAsync(TenantWideA)).Single().Id;
        (await RunJobAsync(host, TenantWideA, request)).Committed();
        await FinalizeAsync(request);

        var repeat = await FanOutAsync(host, [TenantWideA]);

        repeat.Should().Equal([new ClosureGroupReport(TenantWideA, ClosureGroupOutcome.Adopted)],
            "the group's part of the closure is done, so a repeated Begin is owed nothing for it");
        (await RequestsAsync(TenantWideA)).Should().ContainSingle("nothing is filed for a Closed person")
            .Which.Status.Should().Be("completed");
        var rows = await AuditRowsAsync(TenantWideA);
        rows.Should().HaveCount(2);
        rows[^1].Should().Be(new AuditRow(null, "success", "closure_fan_out", true, false, null),
            "the adoption designates nothing, so its row names no request");
        (await LastRowStatesAsync(TenantWideA)).Should().NotContain(PersonA.ToString("D"),
            "no state of the row names the closed person");
    }

    /// <summary>
    /// The closure's Finalize as its second transaction leaves this group: the person Closed with the raw subject removed and
    /// the group's fan-out erasure completed, on the platform role under the User key and every participant tenant's key.
    /// </summary>
    private async Task FinalizeAsync(Guid request)
    {
        await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.PlatformConnectionString);
        await using var transaction = await platform.BeginTransactionAsync();
        await ExecuteAsync(platform, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
            ("key", IdentityAdvisoryLockKey.ForUser(UserId.From(PersonA))));
        foreach (var key in new[] { SchemaFixture.TenantA, SchemaFixture.TenantB }
                     .Select(tenant => IdentityAdvisoryLockKey.ForTenant(TenantId.From(tenant))).Order())
            await ExecuteAsync(platform, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)", ("key", key));
        await ExecuteAsync(platform, transaction, "UPDATE public.users SET status='Closed', provider_subject=NULL WHERE id=@user",
            ("user", PersonA));
        await ExecuteAsync(platform, transaction,
            "UPDATE public.data_subject_requests SET status='completed', row_version=row_version+1 WHERE id=@id", ("id", request));
        await transaction.CommitAsync();
    }

    /// <summary>The newest fan-out row's three states in exactly the group, as text, read under the group's own announcement.</summary>
    private async Task<string> LastRowStatesAsync(ClosureParticipantGroup group)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        return await ScalarAsync<string>(app, transaction, """
            SELECT concat_ws('|', before_state::text, after_state::text, changes::text, entity_id) FROM public.audit_log
            WHERE operation=@operation AND organization_id IS NOT DISTINCT FROM @organization::uuid
            ORDER BY timestamp DESC, id DESC LIMIT 1
            """, ("operation", Operation), ("organization", (object?)group.OrganizationId?.Value ?? DBNull.Value));
    }
}
