using FluentAssertions;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// <c>20261123130000_harden_closure_request_guard</c> on real PostgreSQL as the application role: a closure the platform
/// made <c>fulfilling</c> takes no application write at all, so its Begin moment, its result and its version are written
/// only by the platform's arrows, while a scheduled closure and every other kind keep their application arms; and the
/// INSERT arm answers a refused snapshot before it asks the subject's status, so the two refusals reveal no status.
/// </summary>
public sealed partial class AccountClosureStorageTests
{
    /// <summary>
    /// Every status-preserving write of a begun closure (its result, its update stamp alone, a refusal code) is refused with
    /// the transition refusal, under the fences an application writer holds; a scheduled closure's stamp, its cancellation
    /// and a fulfilling erasure's result stay admitted.
    /// </summary>
    [Fact]
    public async Task A_begun_closure_takes_no_application_write_and_a_scheduled_one_keeps_its_arms()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        var arranged = await ArrangeAsync(database);
        await MoveAsync(database, PersonA, Close, (SchemaFixture.TenantA, Mode.Exclusive), (SchemaFixture.TenantB, Mode.Exclusive));
        await PlatformCommitAsync(database, FullFencesOfPersonA, Format(Begin), arranged.DueClosureA);
        var begun = await RequestStateAsync(database, SchemaFixture.TenantA, null, arranged.DueClosureA);
        begun.Status.Should().Be("fulfilling", "the platform's Begin arrow made it fulfilling");
        var fulfilling = Guid.CreateVersion7();
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA2, PersonA);
            await InsertFanOutAsync(app, transaction, fulfilling, SchemaFixture.TenantA, SchemaFixture.OrgA2, PersonA, BranchMembershipA2);
            await transaction.CommitAsync();
        }

        foreach (var (set, because) in new[]
        {
            ("result='{\"consent_evidence\":7}'", "a result no participant produced"),
            ("status=status", "a later update stamp, which is the Begin moment Finalize reads"),
            ("last_refusal='blocked_hold'", "a refusal code"),
        })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await FenceAsync(app, transaction, SchemaFixture.TenantA, null, PersonA);
            var refusal = await DataExportTestKit.RefuseAsync(transaction, () => StepAsync(app, transaction, arranged.DueClosureA, set));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", Transition), because);
        }

        (await RequestStateAsync(database, SchemaFixture.TenantA, null, arranged.DueClosureA)).Should().Be(begun,
            "nothing the application role sent reached the begun closure");

        // The application's arms that stay: a scheduled closure's own write and its cancellation in its window, and a
        // fulfilling erasure's result, which is the job's.
        foreach (var (tenant, organization, id, set) in new (Guid, Guid?, Guid, string)[]
        {
            (SchemaFixture.TenantA, SchemaFixture.OrgA1, arranged.PendingClosureA1, "status=status"),
            (SchemaFixture.TenantA, SchemaFixture.OrgA1, arranged.PendingClosureA1, "status='cancelled'"),
            (SchemaFixture.TenantA, SchemaFixture.OrgA2, fulfilling, "result='{\"consent_evidence\":7}'"),
        })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await FenceAsync(app, transaction, tenant, organization, PersonA);
            await StepAsync(app, transaction, id, set);
            await transaction.RollbackAsync();
        }
    }

    /// <summary>
    /// A filing whose snapshot is wrong is refused for its snapshot whatever the subject's status, so the refusal says
    /// nothing about whether the person's account is closing; with the exact snapshot, a Closing subject is refused as not
    /// admitted and an Active one is admitted.
    /// </summary>
    [Fact]
    public async Task A_refused_snapshot_is_answered_before_the_subject_s_status_is_asked()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        await ArrangeAsync(database);
        await MoveAsync(database, PersonA, Close, (SchemaFixture.TenantA, Mode.Exclusive), (SchemaFixture.TenantB, Mode.Exclusive));
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);

        foreach (var (subject, tenant, organization, membership, reason, because) in new (Guid, Guid, Guid?, Guid, string, string)[]
        {
            (PersonA, SchemaFixture.TenantA, null, Guid.CreateVersion7(), "Data request snapshot refused.", "a Closing subject, a wrong snapshot"),
            (PersonB, SchemaFixture.TenantB, SchemaFixture.OrgB1, Guid.CreateVersion7(), "Data request snapshot refused.", "an Active subject, a wrong snapshot"),
            (PersonA, SchemaFixture.TenantA, null, LearnerMembershipA, NotAdmitted, "a Closing subject, the exact snapshot"),
        })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await FenceAsync(app, transaction, tenant, organization, subject);
            var refusal = await DataExportTestKit.RefuseAsync(transaction, () =>
                InsertRequestAsync(app, transaction, Guid.CreateVersion7(), tenant, organization, subject, [membership], "erasure"));
            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", reason), because);
        }

        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, SchemaFixture.TenantB, SchemaFixture.OrgB1, PersonB);
            await InsertRequestAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantB, SchemaFixture.OrgB1, PersonB,
                [BranchMembershipB], "erasure");
            await transaction.RollbackAsync();
        }
    }
}
