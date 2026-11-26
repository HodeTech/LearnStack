using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using LearnStack.Tests.Fixtures;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0107 § 7 and ADR-0108 § 8 from end to end through the production composition: a person in three served groups of
/// two tenants and one group of a suspended tenant has a filed closure, a Begin fans it out with the real fan-out act, each
/// group's real erasure job erases the person there, and Finalize (after the provider's three stops, each repaired by a
/// repeat) redacts, deletes the account at the stand-in provider and closes the account. Afterwards the person holds no
/// account and no tenant-side data in any served group, no audit payload names them, every request of theirs is complete,
/// and only the tombstone remains; the database refuses them every new admission.
/// </summary>
/// <remarks>
/// The closure is written as the filing writes it, by the application role under the person's fences; the filing routes
/// themselves, live and retained, are <c>DataRequestHttpTests</c>'. That a Closing or Closed binding answers every session
/// with the uniform 401 is <c>TenantExistingBindingAdmissionTests</c>' proof on the same row state this test reaches.
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class AccountClosureEndToEndTests(SchemaFixture schema)
{
    private const string Ticket = "CHG-107-E2E";
    private static readonly Guid TenantA = SchemaFixture.TenantA;
    private static readonly Guid TenantB = SchemaFixture.TenantB;
    private static readonly Guid TenantC = Guid.Parse("33333333-3333-7333-8333-333333333333");
    private static readonly ClosureParticipantGroup TenantWideA = new(TenantId.From(TenantA), null);
    private static readonly ClosureParticipantGroup BranchA1 = new(TenantId.From(TenantA), OrganizationId.From(SchemaFixture.OrgA1));
    private static readonly ClosureParticipantGroup TenantWideB = new(TenantId.From(TenantB), null);
    private static readonly ClosureParticipantGroup Suspended = new(TenantId.From(TenantC), null);
    private static readonly ClosureParticipantGroup[] Served = [TenantWideA, BranchA1, TenantWideB];

    [Fact]
    public async Task A_closure_from_Begin_through_each_group_s_erasure_job_to_Finalize_leaves_only_the_tombstone()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        var person = Guid.CreateVersion7();
        await InsertUserAsync(database.AppConnectionString, person);
        var memberships = new Dictionary<ClosureParticipantGroup, Guid>
        {
            [TenantWideA] = await MemberAsync(database, TenantWideA, person, RoleId(TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant)),
            [BranchA1] = await MemberAsync(database, BranchA1, person, RoleId(TenantA, BuiltInRoleKey.Learner, PermissionScope.Organization)),
            [TenantWideB] = await MemberAsync(database, TenantWideB, person, RoleId(TenantB, BuiltInRoleKey.Learner, PermissionScope.Tenant)),
            [Suspended] = await SuspendedTenantMemberAsync(database, person),
        };
        var closure = await FileClosureAsync(database, TenantWideA, person, memberships[TenantWideA]);
        await DataErasureTestKit.BackdateAsync(database.MigrationConnectionString, TenantA, null, closure);
        var second = await FileClosureAsync(database, TenantWideB, person, memberships[TenantWideB]);
        // A row of the tenant that is unserved only names the person, so the proof below that Finalize reaches that tenant too
        // is not an empty search before and after: only a redaction of every participant tenant, served or not, removes it.
        await AccountClosureCanary.PlantMembershipRowAsync(database.PlatformConnectionString, TenantC, memberships[Suspended], person);
        (await DataErasureExecutionTests.IdentifiersInPayloadsAsync(database.PlatformConnectionString, TenantC, [person.ToString("D")]))
            .Should().NotBeEmpty("the unserved tenant's row names the person before the closure");
        var subject = new GlobalIdentitySubject(Authority.AuthorityId, Authority.Issuer, "fixture-subject-" + person.ToString("N"));
        var removal = new RecordingAccountRemoval
        {
            Answer = call => call switch
            {
                1 => AccountRemovalOutcome.Refused,
                2 => AccountRemovalOutcome.Unavailable,
                3 => AccountRemovalOutcome.Uncertain,
                _ => AccountRemovalOutcome.Absent,
            },
        };
        await using var host = AccountClosureFlow.Host(database, removal);

        // Begin, with the real fan-out: each served group's erasure created in its own transaction, the suspended tenant's not.
        var begun = await AccountClosureFlow.BeginAsync(host, closure, Ticket);
        begun.Outcome.Should().Be(ClosureBeginOutcome.Begun);
        begun.Groups.Should().Be(new ClosureBeginTally(Served: 3, Started: 3, Adopted: 0, Waiting: 0, Failed: 0, Unserved: 1));

        // Before the jobs ran, no group is ready and Finalize changes nothing.
        var early = await AccountClosureFlow.FinalizeAsync(host, AccountClosureFlow.Admission(PlatformOperations.ClosureFinalize, Ticket), closure);
        (early.Outcome, early.Groups).Should().Be((ClosureFinalizeOutcome.GroupsPending, new ClosureFinalizeTally(3, 0, 3, 1)));
        removal.Subjects.Should().BeEmpty();

        // Each group's erasure job, run as the job host runs it.
        var erasures = new Dictionary<ClosureParticipantGroup, Guid>();
        foreach (var group in Served)
        {
            erasures[group] = await AccountClosureFlow.FanOutRequestAsync(database, group, person);
            await AccountClosureFlow.RunErasureJobAsync(host, database, group, erasures[group]);
        }

        // The provider's three stops: each leaves the person Closing and the closure fulfilling, and a repeat resumes.
        foreach (var stop in new[] { ClosureFinalizeOutcome.ProviderRefused, ClosureFinalizeOutcome.ProviderUnavailable, ClosureFinalizeOutcome.ProviderUncertain })
        {
            var stopped = await AccountClosureFlow.FinalizeAsync(
                host, AccountClosureFlow.Admission(PlatformOperations.ClosureFinalize, Ticket), closure);
            (stopped.Outcome, stopped.Status).Should().Be((stop, "fulfilling"));
            (await UserAsync(database, person)).Should().Be(("Closing", false));
        }

        var admission = AccountClosureFlow.Admission(PlatformOperations.ClosureFinalize, Ticket);
        var finalized = await AccountClosureFlow.FinalizeAsync(host, admission, closure);

        finalized.Outcome.Should().Be(ClosureFinalizeOutcome.Finalized);
        finalized.Groups.Should().Be(new ClosureFinalizeTally(3, 3, 0, 1, ErasuresCompleted: 3, ClosuresCompleted: 2));
        finalized.UnservedGroups.Should().Equal(Suspended);
        removal.Subjects.Should().Equal(Enumerable.Repeat(subject.Subject, 4), "every attempt passes the raw subject, and nothing else");

        // The person: Closed, the raw subject gone, the key kept.
        (await UserAsync(database, person)).Should().Be(("Closed", true));
        (await SubjectKeyAsync(database, person)).Should().Be(subject.SubjectKey.Hex, "the subject key is the permanent tombstone");

        // No tenant-side data in a served group: every Membership of the person is the Revoked tombstone with no profile and no
        // assignment. The suspended tenant's stays, and is named for Phase 11's offboarding.
        foreach (var group in Served)
            (await MembershipAsync(database, group, memberships[group])).Should().Be(("Revoked", (string?)null, 0L), group.ToString());
        (await MembershipAsync(database, Suspended, memberships[Suspended])).Should().Be(("Active", "profile-canary", 1L));

        // Every request of the person is complete: each group's erasure, the closure with its unserved group, the second closure.
        foreach (var group in Served)
            (await RequestAsync(database, group, erasures[group])).Status.Should().Be("completed", group.ToString());
        foreach (var (group, request) in new[] { (TenantWideA, closure), (TenantWideB, second) })
        {
            var completed = await RequestAsync(database, group, request);
            completed.Status.Should().Be("completed");
            JsonNode.Parse(completed.Result)!["unserved_group"]!.GetValue<long>().Should().Be(1);
        }

        // No audit payload in any participant tenant or the sentinel names the person, a Membership of theirs or their profile.
        string[] identifiers = [person.ToString("D"), .. memberships.Where(entry => entry.Key != Suspended).Select(entry => entry.Value.ToString("D")),
            "profile-canary"];
        foreach (var tenant in new[] { TenantA, TenantB, TenantC, TenantId.PlatformSentinel.Value })
        {
            string.Join(Environment.NewLine, await DataErasureExecutionTests.IdentifiersInPayloadsAsync(
                    database.PlatformConnectionString, tenant, identifiers))
                .Should().BeEmpty($"no payload of {tenant} names the person after the closure, except own keys");
        }

        // The evidence: under the sentinel, with the ticket, an intent row per attempt that reached the provider and a result
        // row saying how each ended, and the final act's closed counts.
        var rows = await RowsAsync(database, admission.Operator.CorrelationId!);
        rows.Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation, "platform.closure.finalize_attempt", "platform.closure.finalize");
        rows.Should().OnlyContain(row => row.Tenant == TenantId.PlatformSentinel.Value && row.Metadata.Contains(Ticket));
        var recorded = JsonNode.Parse(rows[^1].After)!;
        (recorded["outcome"]!.GetValue<string>(), recorded["provider"]!.GetValue<string>(), recorded["groups"]!["unserved"]!.GetValue<int>())
            .Should().Be(("finalized", "absent", 1));
        (await CountAsync(database, "SELECT count(*) FROM public.audit_log WHERE operation = 'platform.closure.finalize_attempt' AND entity_id = @id",
            closure)).Should().Be(4, "one intent row per attempt that reached the provider");
        (await CountAsync(database, "SELECT count(*) FROM public.audit_log WHERE operation = 'platform.closure.finalize' AND entity_id = @id",
            closure)).Should().Be(4, "each of those attempts recorded how it ended");

        // The person is refused everywhere: the binding row every session resolves to is Closed, a new Membership and a new
        // request are refused by the database, and a repeat of the act finds it done.
        (await BindingStatusAsync(database, subject)).Should().Be("Closed");
        await using (var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString))
        {
            await using (var transaction = await app.BeginTransactionAsync())
            {
                await ScopeAsync(app, transaction, TenantB, SchemaFixture.OrgB1);
                await LockAsync(app, transaction, TenantB, person, false);
                var admit = () => InsertMemberAsync(app, transaction, TenantB, SchemaFixture.OrgB1, Guid.CreateVersion7(), person,
                    RoleId(TenantB, BuiltInRoleKey.Learner, PermissionScope.Organization));
                (await admit.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0302");
            }

            await using (var transaction = await app.BeginTransactionAsync())
            {
                await FenceAsync(app, transaction, TenantB, null, person);
                var file = () => InsertRequestAsync(app, transaction, Guid.CreateVersion7(), TenantB, null, person,
                    [memberships[TenantWideB]], "erasure");
                (await file.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Be("Data request subject is not admitted.");
            }
        }

        var repeat = await AccountClosureFlow.FinalizeAsync(host, AccountClosureFlow.Admission(PlatformOperations.ClosureFinalize, Ticket), closure);
        repeat.Outcome.Should().Be(ClosureFinalizeOutcome.AlreadyFinalized);
        removal.Subjects.Should().HaveCount(4);
    }

    private static async Task<Guid> MemberAsync(DisposableSchemaDatabase database, ClosureParticipantGroup group, Guid person, Guid role)
    {
        var member = Guid.CreateVersion7();
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        // The tenant key exclusively, as a tenant's first Membership needs it.
        await LockAsync(app, transaction, group.TenantId.Value, person, true);
        await InsertMemberAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value, member, person, role);
        await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await transaction.CommitAsync();
        return member;
    }

    /// <summary>
    /// A third tenant the person joined while it was in use, provisioned as the onboarding writes a tenant, its Roles and a
    /// Membership; it is suspended afterwards, so its group is unserved.
    /// </summary>
    private async Task<Guid> SuspendedTenantMemberAsync(DisposableSchemaDatabase database, Guid person)
    {
        await using (var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString))
        {
            await using (var transaction = await app.BeginTransactionAsync())
            {
                await ScopeAsync(app, transaction, TenantC);
                await ExecuteAsync(app, transaction, """
                    INSERT INTO public.tenants(id,slug,display_name,status,created_at,created_by,row_version)
                    VALUES(@tenant,'closure-unserved','Unserved','Trial',clock_timestamp(),@actor,0)
                    """, ("tenant", TenantC), ("actor", UserId.SystemActor.Value));
                await transaction.CommitAsync();
            }

            var templates = IdentityFoundationManifest.Version1.Templates.ToArray();
            for (var index = 0; index < templates.Length; index++)
            {
                await using var transaction = await app.BeginTransactionAsync();
                await ScopeAsync(app, transaction, TenantC);
                await LockAsync(app, transaction, TenantC, null, false);
                await ExecuteAsync(app, transaction, """
                    INSERT INTO public.roles(id,tenant_id,built_in_key,permission_scope,created_at,created_by)
                    VALUES(@id,@tenant,@key,@scope,clock_timestamp(),@actor)
                    """, ("id", RoleC(index)), ("tenant", TenantC), ("key", templates[index].BuiltInKey.ToString()),
                    ("scope", templates[index].Scope.ToString()), ("actor", UserId.SystemActor.Value));
                foreach (var permission in templates[index].Grants)
                    await ExecuteAsync(app, transaction, """
                        INSERT INTO public.role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
                        VALUES(@tenant,@role,@scope,@key)
                        """, ("tenant", TenantC), ("role", RoleC(index)), ("scope", permission.Scope.ToString()), ("key", permission.Key));
                await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
                await transaction.CommitAsync();
            }

            // A tenant in use has its administrator before anyone else joins.
            var administrator = Guid.CreateVersion7();
            await InsertUserAsync(database.AppConnectionString, administrator);
            await MemberAsync(database, Suspended, administrator,
                RoleC(Array.FindIndex(templates, template => template.BuiltInKey == BuiltInRoleKey.TenantAdmin && template.Scope == PermissionScope.Tenant)));
            var learner = Array.FindIndex(templates, template => template.BuiltInKey == BuiltInRoleKey.Learner && template.Scope == PermissionScope.Tenant);
            var member = await MemberAsync(database, Suspended, person, RoleC(learner));
            await schema.Postgres.ExecuteAsSuperuserAsync(
                $"SET session_replication_role = replica; UPDATE public.tenants SET status = 'Suspended' WHERE id = '{TenantC}'",
                RebindProofSupport.DatabaseOf(database.MigrationConnectionString));
            return member;
        }
    }

    private static Guid RoleC(int index) => Guid.Parse($"01930000-0000-7000-8000-00000000e3{index + 1:00}");

    /// <summary>A closure the person files in the group, as the filing writes it under the person's fences.</summary>
    private static async Task<Guid> FileClosureAsync(DisposableSchemaDatabase database, ClosureParticipantGroup group, Guid person, Guid membership)
    {
        var id = Guid.CreateVersion7();
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await FenceAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value, person);
        await InsertRequestAsync(app, transaction, id, group.TenantId.Value, group.OrganizationId?.Value, person, [membership], "closure");
        await transaction.CommitAsync();
        return id;
    }

    private static async Task<(string Status, bool SubjectRemoved)> UserAsync(DisposableSchemaDatabase database, Guid person)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("SELECT status, provider_subject IS NULL FROM public.users WHERE id = @id", owner);
        command.Parameters.AddWithValue("id", person);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return (row.GetString(0), row.GetBoolean(1));
    }

    private static async Task<string> SubjectKeyAsync(DisposableSchemaDatabase database, Guid person)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        return await ScalarAsync<string>(owner, null, "SELECT encode(subject_key, 'hex') FROM public.users WHERE id = @id", ("id", person));
    }

    /// <summary>The User row the binding store resolves a session's subject to, read as the application role does.</summary>
    private static async Task<string> BindingStatusAsync(DisposableSchemaDatabase database, GlobalIdentitySubject subject)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await GlobalIdentitySchemaTests.AnnounceAsync(app, transaction, subject);
        return await ScalarAsync<string>(app, transaction,
            "SELECT status FROM public.users WHERE authority_id = @authority AND subject_key = @key",
            ("authority", subject.AuthorityId.Value), ("key", subject.SubjectKey.ToByteArray()));
    }

    private static async Task<(string Status, string? DisplayName, long Assignments)> MembershipAsync(
        DisposableSchemaDatabase database, ClosureParticipantGroup group, Guid membership)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        await using var command = new NpgsqlCommand("""
            SELECT m.status, p.display_name,
                (SELECT count(*) FROM public.membership_role_assignments a WHERE a.tenant_id = m.tenant_id AND a.membership_id = m.id)
            FROM public.memberships m JOIN public.membership_profiles p ON p.tenant_id = m.tenant_id AND p.membership_id = m.id
            WHERE m.id = @id
            """, app, transaction);
        command.Parameters.AddWithValue("id", membership);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return (row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1), row.GetInt64(2));
    }

    private sealed record RequestRow(string Status, string Result);

    private static async Task<RequestRow> RequestAsync(DisposableSchemaDatabase database, ClosureParticipantGroup group, Guid request)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        await using var command = new NpgsqlCommand("SELECT status, result::text FROM public.data_subject_requests WHERE id = @id", app, transaction);
        command.Parameters.AddWithValue("id", request);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new RequestRow(row.GetString(0), row.GetString(1));
    }

    private sealed record OperatorRow(Guid Tenant, string Operation, string After, string Metadata);

    private static async Task<List<OperatorRow>> RowsAsync(DisposableSchemaDatabase database, string correlation)
    {
        await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.PlatformConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT tenant_id, operation, COALESCE(after_state::text, ''), metadata::text
            FROM public.audit_log WHERE correlation_id = @c ORDER BY "timestamp", id
            """, platform);
        command.Parameters.AddWithValue("c", correlation);
        var rows = new List<OperatorRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new OperatorRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }

    private static async Task<long> CountAsync(DisposableSchemaDatabase database, string sql, Guid entity)
    {
        await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.PlatformConnectionString);
        return await ScalarAsync<long>(platform, null, sql, ("id", entity.ToString("D")));
    }
}
