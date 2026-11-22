using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// P03-10 Step 1 storage proof for <c>data_subject_requests</c>, <c>data_export_artifacts</c>,
/// <c>data_export_tickets</c> and <c>data_export_chunks</c> (ADR-0106 § 7–8, ADR-0107 § 4 and § 8), connected as
/// <c>learnstack_app</c> against the nonempty seeded schema. Every case rolls back; the controls that must commit
/// or widen a privilege live in <see cref="DataRequestStorageControlTests"/>.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DataRequestStorageTests(SchemaFixture schema)
{
    private static readonly string[] AppRequestUpdate = ["status", "row_version", "progress", "result", "last_refusal",
        "failure_reason", "completed_at", "updated_at", "updated_by"];
    private static readonly string[] AppArtifactUpdate = ["status", "purged_at", "row_version", "updated_at", "updated_by"];
    private static readonly string[] PlatformRequestSelect = ["id", "tenant_id", "organization_id", "kind", "origin",
        "subject_user_id", "status", "membership_snapshot", "execute_at", "progress", "result", "last_refusal", "failure_reason",
        "completed_at", "created_at", "row_version"];
    private static readonly string[] PlatformRequestUpdate = ["status", "row_version", "completed_at", "result"];
    private static readonly string[] AppTableGrants =
    [
        "data_subject_requests:learnstack_app:INSERT", "data_subject_requests:learnstack_app:SELECT",
        "data_export_artifacts:learnstack_app:INSERT", "data_export_artifacts:learnstack_app:SELECT",
        "data_export_tickets:learnstack_app:INSERT", "data_export_tickets:learnstack_app:SELECT",
        "data_export_chunks:learnstack_app:DELETE", "data_export_chunks:learnstack_app:INSERT", "data_export_chunks:learnstack_app:SELECT",
        "data_export_tickets.consumed_at:learnstack_app:UPDATE",
    ];
    private static readonly string[] UnreadActors = ["requester_user_id", "created_by", "updated_by", "deleted_by"];

    public static TheoryData<string> Tables()
    {
        var data = new TheoryData<string>();
        foreach (var table in SchemaTableClasses.IdentityDataRequest) data.Add(table);
        return data;
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task Each_tenant_and_group_reads_only_its_own_rows(string table)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();

        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        (await CountAsync(app, transaction, table)).Should().Be(1, "tenant A's tenant-wide group alone, without an organization");
        (await CountAsync(app, transaction, table, $"tenant_id='{SchemaFixture.TenantB}'")).Should().Be(0,
            "tenant B's seeded rows exist and are invisible");

        await ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        (await CountAsync(app, transaction, table)).Should().Be(2, "the tenant-wide row and organization A1's");
        (await CountAsync(app, transaction, table, $"organization_id='{SchemaFixture.OrgA2}'")).Should().Be(0,
            "organization A2's seeded row is invisible to organization A1");

        await ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA2);
        (await CountAsync(app, transaction, table, $"organization_id='{SchemaFixture.OrgA1}'")).Should().Be(0);
        (await CountAsync(app, transaction, table, $"organization_id='{SchemaFixture.OrgA2}'")).Should().Be(1);

        await ScopeAsync(app, transaction, SchemaFixture.TenantB);
        (await CountAsync(app, transaction, table)).Should().Be(1);
        (await CountAsync(app, transaction, table, $"tenant_id='{SchemaFixture.TenantA}'")).Should().Be(0);

        await ExecuteAsync(app, transaction, "SELECT set_config('app.tenant_id','',true),set_config('app.organization_id','',true)");
        (await CountAsync(app, transaction, table)).Should().Be(0, "no tenant context reads nothing");
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task The_organization_write_guards_refuse_another_organization_and_the_tenant_wide_row(string table)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        await SchemaQueries.SetSettingAsync(app, transaction, "app.scope", "tenant");
        (await CountAsync(app, transaction, table, $"organization_id='{SchemaFixture.OrgA2}'")).Should().Be(1,
            "the tenant-scope read hatch shows organization A2's row, so the zero below is the write guard");
        (await CountAsync(app, transaction, table, "organization_id IS NULL")).Should().Be(1);

        var write = table switch
        {
            "data_subject_requests" => "UPDATE public.data_subject_requests SET row_version=row_version WHERE {0}",
            "data_export_artifacts" => "UPDATE public.data_export_artifacts SET row_version=row_version WHERE {0}",
            "data_export_tickets" => "UPDATE public.data_export_tickets SET consumed_at=consumed_at WHERE {0}",
            _ => "DELETE FROM public.data_export_chunks WHERE {0}",
        };
        (await ExecuteCountAsync(app, transaction, string.Format(System.Globalization.CultureInfo.InvariantCulture, write,
            $"organization_id='{SchemaFixture.OrgA2}'"))).Should().Be(0, "another organization's row is not writable");
        (await ExecuteCountAsync(app, transaction, string.Format(System.Globalization.CultureInfo.InvariantCulture, write,
            "organization_id IS NULL"))).Should().Be(0, "an organization session does not write the tenant-wide row");
    }

    [Fact]
    public async Task A_foreign_tenant_row_is_neither_inserted_nor_updated()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FenceAsync(app, transaction, SchemaFixture.TenantA, null, AdminA);
        var insert = await RefuseAsync(transaction, () => InsertRequestAsync(app, transaction, Guid.CreateVersion7(),
            SchemaFixture.TenantB, null, AdminB, [AdminMembershipB]));
        insert.SqlState.Should().Be("P0301", "the BEFORE guard's tenant fence binds the row to the announced tenant first");
        insert.ConstraintName.Should().Be("identity_tenant_fence");
        (await ExecuteCountAsync(app, transaction, "UPDATE public.data_subject_requests SET row_version=row_version WHERE id=@id",
            ("id", TenantWideB.Request))).Should().Be(0, "USING hides the foreign row");
        (await ExecuteCountAsync(app, transaction, "DELETE FROM public.data_export_chunks WHERE artifact_id=@id",
            ("id", TenantWideB.Artifact!.Value))).Should().Be(0);
        (await ExecuteCountAsync(app, transaction, "UPDATE public.data_export_tickets SET consumed_at=created_at WHERE id=@id",
            ("id", TenantWideB.Ticket!.Value))).Should().Be(0);
    }

    [Fact]
    public async Task One_non_terminal_request_per_kind_subject_and_group_and_a_terminal_one_frees_the_slot()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FenceAsync(app, transaction, SchemaFixture.TenantA, null, AdminA);
        var first = Guid.CreateVersion7();
        await InsertRequestAsync(app, transaction, first, SchemaFixture.TenantA, null, AdminA, [AdminMembershipA]);

        var duplicate = await RefuseAsync(transaction, () => InsertRequestAsync(app, transaction, Guid.CreateVersion7(),
            SchemaFixture.TenantA, null, AdminA, [AdminMembershipA]));
        duplicate.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        duplicate.ConstraintName.Should().Be("ux_data_subject_requests_active",
            "the tenant-wide group's NULL organization is not distinct, so the second export collides");

        await InsertRequestAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, null, AdminA, [AdminMembershipA], "erasure");
        await InsertRequestAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, null, AdminA, [AdminMembershipA], "closure");
        (await RefuseAsync(transaction, () => InsertRequestAsync(app, transaction, Guid.CreateVersion7(),
            SchemaFixture.TenantA, null, AdminA, [AdminMembershipA], "erasure"))).ConstraintName.Should().Be("ux_data_subject_requests_active");

        await StepAsync(app, transaction, first, "status='cancelled'");
        await InsertRequestAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, null, AdminA, [AdminMembershipA]);

        // PersonA's seeded tenant-wide export is completed, so a new one is admitted in that group.
        await LockAsync(app, transaction, SchemaFixture.TenantA, PersonA, false);
        await InsertRequestAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, null, PersonA, [LearnerMembershipA]);
        (await CountAsync(app, transaction, "data_subject_requests",
            $"subject_user_id='{AdminA}' AND status IN ('requested','scheduled','fulfilling','awaiting_privileged_step')"))
            .Should().Be(3, "one active export, erasure and closure");
    }

    public static TheoryData<string, string, string> RefusedInserts() => new()
    {
        { "no-user-fence", "P0301", "identity_user_fence" },
        { "no-tenant-fence", "P0301", "identity_tenant_fence" },
        { "initial-status", "23514", "Data request initial state refused." },
        { "foreign-actor", "23514", "Data request initial state refused." },
        { "nonzero-version", "23514", "Data request initial state refused." },
        { "stale-clock", "23514", "Data request initial state refused." },
        { "tenant-wide-membership", "23514", "Data request snapshot refused." },
        { "other-organization-membership", "23514", "Data request snapshot refused." },
        { "incomplete-snapshot", "23514", "Data request snapshot refused." },
        { "empty-snapshot", "23514", "Data request snapshot refused." },
        { "administrator-closure", "23514", "ck_data_subject_requests_origin" },
        { "self-requester-mismatch", "23514", "ck_data_subject_requests_requester" },
    };

    [Theory]
    [MemberData(nameof(RefusedInserts))]
    public async Task The_request_guard_refuses_an_unfenced_malformed_or_inexact_creation(string variant, string sqlState, string reason)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        if (variant != "no-tenant-fence")
            await LockAsync(app, transaction, SchemaFixture.TenantA, variant == "no-user-fence" ? null : PersonA, false);
        else
            await ExecuteAsync(app, transaction, "SELECT pg_advisory_xact_lock(@key)",
                ("key", IdentityAdvisoryLockKey.ForUser(UserId.From(PersonA))));
        if (variant == "incomplete-snapshot")
        {
            // A Revoked tombstone stays the person's Membership in the group; the snapshot must name it too.
            await ExecuteAsync(app, transaction, """
                UPDATE public.memberships SET status='Revoked',deleted_at=clock_timestamp(),deleted_by=@actor,
                    updated_at=clock_timestamp(),updated_by=@actor,row_version=row_version+1 WHERE id=@member
                """, ("member", BranchMembershipA1), ("actor", AdminA));
            await InsertMemberAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1, Guid.CreateVersion7(), PersonA,
                RoleId(SchemaFixture.TenantA, LearnStack.SharedKernel.Permissions.BuiltInRoleKey.Learner,
                    LearnStack.SharedKernel.Permissions.PermissionScope.Organization));
        }

        var (status, origin, requester, createdBy, version, createdAt, snapshot) = variant switch
        {
            "initial-status" => ("fulfilling", "self", PersonA, PersonA, 0L, "now()", Snapshot(BranchMembershipA1)),
            "foreign-actor" => ("requested", "self", PersonA, AdminA, 0L, "now()", Snapshot(BranchMembershipA1)),
            "nonzero-version" => ("requested", "self", PersonA, PersonA, 1L, "now()", Snapshot(BranchMembershipA1)),
            "stale-clock" => ("requested", "self", PersonA, PersonA, 0L, "now() - interval '1 hour'", Snapshot(BranchMembershipA1)),
            "tenant-wide-membership" => ("requested", "self", PersonA, PersonA, 0L, "now()", Snapshot(LearnerMembershipA)),
            "other-organization-membership" => ("requested", "self", PersonA, PersonA, 0L, "now()", Snapshot(BranchMembershipA2)),
            "empty-snapshot" => ("requested", "self", PersonA, PersonA, 0L, "now()", "[]"),
            "administrator-closure" => ("scheduled", "administrator", AdminA, AdminA, 0L, "now()", Snapshot(BranchMembershipA1)),
            "self-requester-mismatch" => ("requested", "self", AdminA, AdminA, 0L, "now()", Snapshot(BranchMembershipA1)),
            _ => ("requested", "self", PersonA, PersonA, 0L, "now()", Snapshot(BranchMembershipA1)),
        };
        var kind = variant == "administrator-closure" ? "closure" : "export";
        var refusal = await RefuseAsync(transaction, () => ExecuteAsync(app, transaction, $"""
            INSERT INTO public.data_subject_requests(id,tenant_id,organization_id,kind,origin,subject_user_id,requester_user_id,
                status,membership_snapshot,execute_at,created_at,created_by,row_version)
            VALUES(@id,@tenant,@organization,@kind,@origin,@subject,@requester,@status,@snapshot::jsonb,
                CASE WHEN @kind='export' THEN NULL ELSE ({createdAt}) + interval '24 hours' END,{createdAt},@created_by,@version)
            """, ("id", Guid.CreateVersion7()), ("tenant", SchemaFixture.TenantA), ("organization", SchemaFixture.OrgA1),
            ("kind", kind), ("origin", origin), ("subject", PersonA), ("requester", requester), ("status", status),
            ("snapshot", snapshot), ("created_by", createdBy), ("version", version)));
        refusal.SqlState.Should().Be(sqlState);
        if (sqlState == "P0301" || reason.StartsWith("ck_", StringComparison.Ordinal))
            refusal.ConstraintName.Should().Be(reason);
        else
            refusal.MessageText.Should().Be(reason);
    }

    public static TheoryData<string, string, string> RefusedUpdates() => new()
    {
        { "requested-to-completed", "status='completed',completed_at=now()", "Data request transition refused." },
        { "requested-to-awaiting", "status='awaiting_privileged_step'", "Data request transition refused." },
        { "requested-to-failed", "status='failed',failure_reason='execution_failed'", "Data request failure record refused." },
        { "progress-outside-fulfilling", "progress='{\"identity\":\"2026-11-22T09:00:00.0000000Z\"}'", "Data request progress refused." },
        { "result-outside-fulfilling", "result='{\"request_receipt\":1}'", "Data request result refused." },
        { "refusal-on-requested", "last_refusal='blocked_hold'", "Data request refusal record refused." },
        { "completion-stamp-alone", "completed_at=now()", "Data request completion refused." },
    };

    [Theory]
    [MemberData(nameof(RefusedUpdates))]
    public async Task The_request_guard_refuses_every_application_arrow_outside_ADR_0106_section_8(string variant, string set, string reason)
    {
        _ = variant;
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FenceAsync(app, transaction, SchemaFixture.TenantB, SchemaFixture.OrgB1, null);
        var refusal = await RefuseAsync(transaction, () => StepAsync(app, transaction, BranchB.Request, set));
        refusal.SqlState.Should().Be("23514");
        refusal.MessageText.Should().Be(reason);
    }

    [Fact]
    public async Task Version_stamps_terminal_states_and_privileged_arrows_are_refused_and_legal_arrows_admitted()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FenceAsync(app, transaction, SchemaFixture.TenantB, null, null);
        (await RefuseAsync(transaction, () => ExecuteAsync(app, transaction,
            "UPDATE public.data_subject_requests SET status='cancelled',updated_at=now(),updated_by=@system WHERE id=@id",
            ("id", TenantWideB.Request), ("system", SystemActor)))).MessageText.Should().Be("Data request immutable state refused.",
            "every update advances the row version by one");
        (await RefuseAsync(transaction, () => StepAsync(app, transaction, TenantWideB.Request, "status='cancelled'")))
            .MessageText.Should().Be("Data request is terminal.");
        (await RefuseAsync(transaction, () => ExecuteAsync(app, transaction,
            "DELETE FROM public.data_subject_requests WHERE id=@id", ("id", TenantWideB.Request)))).SqlState.Should().Be("42501",
            "no application DELETE exists: requests are retained receipts");

        await LockAsync(app, transaction, SchemaFixture.TenantB, AdminB, false);
        var erasure = Guid.CreateVersion7();
        var closure = Guid.CreateVersion7();
        await InsertRequestAsync(app, transaction, erasure, SchemaFixture.TenantB, null, AdminB, [AdminMembershipB], "erasure");
        await InsertRequestAsync(app, transaction, closure, SchemaFixture.TenantB, null, AdminB, [AdminMembershipB], "closure");
        (await RefuseAsync(transaction, () => StepAsync(app, transaction, erasure, "status='fulfilling'")))
            .MessageText.Should().Be("Data request transition refused.", "an erasure executes only after its 24-hour window");
        (await RefuseAsync(transaction, () => StepAsync(app, transaction, closure, "status='fulfilling'")))
            .MessageText.Should().Be("Data request transition refused.", "a closure's Begin is an operator act, not the application's");
        await StepAsync(app, transaction, closure, "last_refusal='blocked_window'");
        await StepAsync(app, transaction, closure, "status='cancelled'");
        await StepAsync(app, transaction, erasure, "status='cancelled'");
        (await ScalarAsync<string>(app, transaction, "SELECT string_agg(status||':'||row_version,',' ORDER BY kind) FROM public.data_subject_requests WHERE id IN (@a,@b)",
            ("a", erasure), ("b", closure))).Should().Be("cancelled:2,cancelled:1");

        await FenceAsync(app, transaction, SchemaFixture.TenantB, SchemaFixture.OrgB1, null);
        await StepAsync(app, transaction, BranchB.Request, "status='fulfilling'");
        await StepAsync(app, transaction, BranchB.Request, "progress='{\"identity\":\"2026-11-22T09:00:00.0000000Z\"}'");
        (await RefuseAsync(transaction, () => StepAsync(app, transaction, BranchB.Request, "progress='{}'")))
            .MessageText.Should().Be("Data request progress refused.", "progress only grows");
        await StepAsync(app, transaction, BranchB.Request, "result='{\"request_receipt\":1}'");
        (await RefuseAsync(transaction, () => StepAsync(app, transaction, BranchB.Request, "status='failed'")))
            .ConstraintName.Should().Be("ck_data_subject_requests_outcome", "a failure carries its closed reason");
        await StepAsync(app, transaction, BranchB.Request, "status='failed',failure_reason='export_too_large'");
    }

    [Fact]
    public async Task The_JSON_documents_are_bounded_by_their_CHECKs()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FenceAsync(app, transaction, SchemaFixture.TenantB, SchemaFixture.OrgB1, null);
        await StepAsync(app, transaction, BranchB.Request, "status='fulfilling'");
        foreach (var (set, constraint) in new[]
        {
            ("progress='{\"Identity\":\"2026-11-22T09:00:00.0000000Z\"}'", "ck_data_subject_requests_progress"),
            ("progress='{\"identity\":\"2026-11-22\"}'", "ck_data_subject_requests_progress"),
            ("result='{\"anonymous\":1}'", "ck_data_subject_requests_result"),
            ("result='{\"consent_evidence\":-1}'", "ck_data_subject_requests_result"),
            ("result='{\"consent_evidence\":1.5}'", "ck_data_subject_requests_result"),
            ("result='{\"consent_evidence\":\"1\"}'", "ck_data_subject_requests_result"),
        })
            (await RefuseAsync(transaction, () => StepAsync(app, transaction, BranchB.Request, set))).ConstraintName.Should().Be(constraint, set);
        await LockAsync(app, transaction, SchemaFixture.TenantB, PersonB, false);
        var closure = Guid.CreateVersion7();
        await InsertRequestAsync(app, transaction, closure, SchemaFixture.TenantB, SchemaFixture.OrgB1, PersonB, [BranchMembershipB], "closure");
        (await RefuseAsync(transaction, () => StepAsync(app, transaction, closure, "last_refusal='blocked_everything'")))
            .ConstraintName.Should().Be("ck_data_subject_requests_outcome", "a refusal is one of the closed codes");
        (await ScalarAsync<long>(app, transaction, """
            SELECT count(*) FROM public.data_subject_requests WHERE id=@id
                AND jsonb_array_length(membership_snapshot)=1 AND progress='{}'::jsonb AND result='{}'::jsonb
            """, ("id", BranchB.Request))).Should().Be(1);
    }

    [Fact]
    public async Task Chunk_CHECKs_bound_the_nonce_tag_ciphertext_and_index()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        var artifact = await AdminExportArtifactAsync(app, transaction, 2 * DataRequestLimits.ChunkPlaintextBytes + 1);
        foreach (var (index, nonce, ciphertext, tag, constraint) in new (int, int, int, int, string)[]
        {
            (0, 11, 10, 16, "ck_data_export_chunks_nonce"),
            (0, 13, 10, 16, "ck_data_export_chunks_nonce"),
            (0, 12, 10, 15, "ck_data_export_chunks_tag"),
            (0, 12, 10, 17, "ck_data_export_chunks_tag"),
            (0, 12, 0, 16, "ck_data_export_chunks_ciphertext"),
            (0, 12, DataRequestLimits.ChunkPlaintextBytes + 1, 16, "ck_data_export_chunks_ciphertext"),
            (-1, 12, 10, 16, "ck_data_export_chunks_index"),
        })
            (await RefuseAsync(transaction, () => InsertChunkAsync(app, transaction, SchemaFixture.TenantA, null, artifact, index,
                nonce, ciphertext, tag))).ConstraintName.Should().Be(constraint);
        await InsertChunkAsync(app, transaction, SchemaFixture.TenantA, null, artifact, 0, 12, DataRequestLimits.ChunkPlaintextBytes, 16);
        (await RefuseAsync(transaction, () => InsertChunkAsync(app, transaction, SchemaFixture.TenantA, null, artifact, 0)))
            .ConstraintName.Should().Be("pk_data_export_chunks");
        (await RefuseAsync(transaction, () => InsertChunkAsync(app, transaction, SchemaFixture.TenantA, null, artifact, 3)))
            .MessageText.Should().Be("Data export chunk refused.", "an index at or past the artifact's chunk count is refused");
        (await RefuseAsync(transaction, () => ExecuteAsync(app, transaction,
            "UPDATE public.data_export_chunks SET nonce=nonce WHERE artifact_id=@id", ("id", artifact)))).SqlState.Should().Be("42501",
            "chunks are written once and deleted, never updated");
        (await ScalarAsync<string>(app, transaction,
            "SELECT attstorage::text FROM pg_attribute WHERE attrelid='public.data_export_chunks'::regclass AND attname='ciphertext'"))
            .Should().Be("e", "incompressible ciphertext is stored out of line without compression");
    }

    [Fact]
    public async Task An_available_artifact_holds_exactly_its_chunks_and_a_revoked_one_none_at_commit()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        var artifact = await AdminExportArtifactAsync(app, transaction, 2 * DataRequestLimits.ChunkPlaintextBytes + 1);
        await InsertChunkAsync(app, transaction, SchemaFixture.TenantA, null, artifact, 0);
        await InsertChunkAsync(app, transaction, SchemaFixture.TenantA, null, artifact, 1);
        var incomplete = await RefuseAsync(transaction, () => ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE"));
        incomplete.ConstraintName.Should().Be("data_export_artifact_chunks", "two of three chunks is not a stored bundle");
        await InsertChunkAsync(app, transaction, SchemaFixture.TenantA, null, artifact, 2);
        await transaction.SaveAsync("complete");
        await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await transaction.RollbackAsync("complete");

        await RevokeAsync(app, transaction, artifact);
        (await RefuseAsync(transaction, () => ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE")))
            .ConstraintName.Should().Be("data_export_artifact_chunks", "a revocation that leaves ciphertext behind is refused");
        (await ExecuteCountAsync(app, transaction, "DELETE FROM public.data_export_chunks WHERE artifact_id=@id", ("id", artifact)))
            .Should().Be(3);
        await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        (await RefuseAsync(transaction, () => InsertChunkAsync(app, transaction, SchemaFixture.TenantA, null, artifact, 0)))
            .MessageText.Should().Be("Data export chunk refused.", "a revoked artifact accepts no chunk");
    }

    [Fact]
    public async Task The_artifact_guard_holds_its_request_lifecycle_and_one_available_artifact_per_subject_and_group()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        var first = await AdminExportArtifactAsync(app, transaction, 10);
        var firstRequest = await ScalarAsync<Guid>(app, transaction, "SELECT request_id FROM public.data_export_artifacts WHERE id=@id", ("id", first));
        await InsertChunkAsync(app, transaction, SchemaFixture.TenantA, null, first, 0);
        await StepAsync(app, transaction, firstRequest, "status='completed',completed_at=now()");

        var second = Guid.CreateVersion7();
        await InsertRequestAsync(app, transaction, second, SchemaFixture.TenantA, null, AdminA, [AdminMembershipA]);
        (await RefuseAsync(transaction, () => InsertArtifactAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, null,
            second, AdminA, 10))).MessageText.Should().Be("Data export artifact request refused.", "only a fulfilling export has an artifact");
        await StepAsync(app, transaction, second, "status='fulfilling'");
        (await RefuseAsync(transaction, () => InsertArtifactAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, null,
            second, AdminA, 10))).ConstraintName.Should().Be("ux_data_export_artifacts_available",
            "ADR-0107 § 4: a new export revokes the previous artifact first");
        (await RefuseAsync(transaction, () => InsertArtifactAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, null,
            second, PersonA, 10))).MessageText.Should().Be("Data export artifact request refused.", "the subject mirrors the request's");
        (await RefuseAsync(transaction, () => InsertArtifactAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA,
            SchemaFixture.OrgA1, second, AdminA, 10))).MessageText.Should().StartWith("parent scope is unavailable or mismatched",
            "the group mirrors the request's");
        (await RefuseAsync(transaction, () => InsertArtifactAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, null,
            TenantWideA.Request, PersonA, 10))).MessageText.Should().Be("Data export artifact request refused.",
            "a request completed in an earlier transaction gains no artifact");

        foreach (var (set, reason) in new[]
        {
            ("status='expired'", "Data export artifact transition refused."),
            ("status='purged',purged_at=now()", "Data export artifact transition refused."),
            ("status='revoked'", "Data export artifact transition refused."),
        })
            (await RefuseAsync(transaction, () => ExecuteAsync(app, transaction,
                $"UPDATE public.data_export_artifacts SET {set},row_version=row_version+1,updated_at=now(),updated_by=@system WHERE id=@id",
                ("id", first), ("system", SystemActor)))).MessageText.Should().Be(reason, set);
        (await RefuseAsync(transaction, () => ExecuteAsync(app, transaction,
            "UPDATE public.data_export_artifacts SET byte_count=byte_count WHERE id=@id", ("id", first)))).SqlState.Should().Be("42501",
            "the size is not an application-writable column");

        await RevokeAsync(app, transaction, first);
        await ExecuteAsync(app, transaction, "DELETE FROM public.data_export_chunks WHERE artifact_id=@id", ("id", first));
        var replacement = Guid.CreateVersion7();
        await InsertArtifactAsync(app, transaction, replacement, SchemaFixture.TenantA, null, second, AdminA, 10);
        await InsertChunkAsync(app, transaction, SchemaFixture.TenantA, null, replacement, 0);
        await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
    }

    [Fact]
    public async Task Only_the_subject_mints_a_ticket_and_it_is_consumed_once()
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        var artifact = await AdminExportArtifactAsync(app, transaction, 10);
        await InsertChunkAsync(app, transaction, SchemaFixture.TenantA, null, artifact, 0);
        (await RefuseAsync(transaction, () => InsertTicketAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, null,
            artifact, PersonA))).MessageText.Should().Be("Data export ticket artifact refused.", "ADR-0106 § 6: the recipient is the subject");
        (await RefuseAsync(transaction, () => ExecuteAsync(app, transaction, """
            INSERT INTO public.data_export_tickets(id,tenant_id,organization_id,artifact_id,digest,session_binding_digest,
                created_at,created_by,expires_at,consumed_at)
            VALUES(@id,@tenant,NULL,@artifact,sha256('a'::bytea),sha256('b'::bytea),now(),@subject,now()+interval '15 minutes',now())
            """, ("id", Guid.CreateVersion7()), ("tenant", SchemaFixture.TenantA), ("artifact", artifact), ("subject", AdminA))))
            .MessageText.Should().Be("Data export ticket initial state refused.");
        (await RefuseAsync(transaction, () => ExecuteAsync(app, transaction, """
            INSERT INTO public.data_export_tickets(id,tenant_id,organization_id,artifact_id,digest,session_binding_digest,
                created_at,created_by,expires_at)
            VALUES(@id,@tenant,NULL,@artifact,'\x00'::bytea,sha256('b'::bytea),now(),@subject,now()+interval '15 minutes')
            """, ("id", Guid.CreateVersion7()), ("tenant", SchemaFixture.TenantA), ("artifact", artifact), ("subject", AdminA))))
            .ConstraintName.Should().Be("ck_data_export_tickets_digests");

        var ticket = Guid.CreateVersion7();
        await InsertTicketAsync(app, transaction, ticket, SchemaFixture.TenantA, null, artifact, AdminA);
        (await ExecuteCountAsync(app, transaction, "UPDATE public.data_export_tickets SET consumed_at=now() WHERE id=@id", ("id", ticket)))
            .Should().Be(1);
        (await RefuseAsync(transaction, () => ExecuteAsync(app, transaction,
            "UPDATE public.data_export_tickets SET consumed_at=now() WHERE id=@id", ("id", ticket)))).MessageText
            .Should().Be("Data export ticket consumption refused.", "a resumed download mints a new ticket");
        (await RefuseAsync(transaction, () => ExecuteAsync(app, transaction,
            "UPDATE public.data_export_tickets SET digest=digest WHERE id=@id", ("id", ticket)))).SqlState.Should().Be("42501",
            "the consumed marker is the only application-writable column");
        (await RefuseAsync(transaction, () => ExecuteAsync(app, transaction,
            "DELETE FROM public.data_export_tickets WHERE id=@id", ("id", ticket)))).SqlState.Should().Be("42501");

        await RevokeAsync(app, transaction, artifact);
        (await RefuseAsync(transaction, () => InsertTicketAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, null,
            artifact, AdminA))).MessageText.Should().Be("Data export ticket artifact refused.", "a revoked artifact mints no ticket");
    }

    [Fact]
    public async Task The_grant_matrix_is_exactly_ADR_0107_section_8()
    {
        await using var owner = await PostgresFixture.OpenAsync(schema.Postgres.MigrationConnectionString);
        var grants = await SchemaQueries.ReadStringsAsync(owner, """
            WITH tables AS (SELECT oid, relname, relowner, relacl FROM pg_class
                WHERE oid IN ('public.data_subject_requests'::regclass,'public.data_export_artifacts'::regclass,
                    'public.data_export_tickets'::regclass,'public.data_export_chunks'::regclass))
            SELECT t.relname || ':' || COALESCE(r.rolname, 'PUBLIC') || ':' || x.privilege_type
            FROM tables t CROSS JOIN LATERAL aclexplode(t.relacl) x LEFT JOIN pg_roles r ON r.oid = x.grantee
            WHERE x.grantee <> t.relowner
            UNION ALL
            SELECT t.relname || '.' || a.attname || ':' || COALESCE(r.rolname, 'PUBLIC') || ':' || x.privilege_type
            FROM tables t JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum > 0 AND NOT a.attisdropped
            CROSS JOIN LATERAL aclexplode(a.attacl) x LEFT JOIN pg_roles r ON r.oid = x.grantee
            ORDER BY 1
            """);
        var expected = AppTableGrants
            .Concat(AppRequestUpdate.Select(column => $"data_subject_requests.{column}:learnstack_app:UPDATE"))
            .Concat(AppArtifactUpdate.Select(column => $"data_export_artifacts.{column}:learnstack_app:UPDATE"))
            .Concat(PlatformRequestSelect.Select(column => $"data_subject_requests.{column}:learnstack_platform:SELECT"))
            .Concat(PlatformRequestUpdate.Select(column => $"data_subject_requests.{column}:learnstack_platform:UPDATE"));
        grants.Should().BeEquivalentTo(expected, "PUBLIC, learnstack_outbox_admin and the two Identity machine roles hold nothing, "
            + "and learnstack_platform holds only its closed request columns");
        PlatformRequestSelect.Should().NotContain(UnreadActors,
            "the operator read names no actor beyond the subject");
    }

    [Fact]
    public async Task The_platform_role_reads_only_its_closed_columns_and_writes_nothing_its_guard_has_not_admitted()
    {
        await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.PlatformConnectionString);
        (await ScalarAsync<long>(platform, null, "SELECT count(*) FROM (SELECT id,tenant_id,status,membership_snapshot FROM public.data_subject_requests) rows"))
            .Should().Be(Rows.Length, "BYPASSRLS reads every tenant's rows; the grant, not a policy, is the bound");
        foreach (var statement in new[]
        {
            "SELECT requester_user_id FROM public.data_subject_requests",
            "SELECT * FROM public.data_subject_requests",
            "INSERT INTO public.data_subject_requests(id) VALUES(uuidv7())",
            "UPDATE public.data_subject_requests SET last_refusal='blocked_hold'",
            "DELETE FROM public.data_subject_requests",
            "SELECT count(*) FROM public.data_export_artifacts",
            "UPDATE public.data_export_artifacts SET status='revoked'",
            "SELECT count(*) FROM public.data_export_tickets",
            "UPDATE public.data_export_tickets SET consumed_at=now()",
            "SELECT count(*) FROM public.data_export_chunks",
            "DELETE FROM public.data_export_chunks",
        })
        {
            await using var transaction = await platform.BeginTransactionAsync();
            var act = () => ExecuteAsync(platform, transaction, statement);
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501", statement);
        }

        await using (var transaction = await platform.BeginTransactionAsync())
        {
            var act = () => ExecuteAsync(platform, transaction,
                "UPDATE public.data_subject_requests SET status='completed',completed_at=now(),row_version=row_version+1 WHERE id=@id",
                ("id", BranchB.Request));
            var refusal = (await act.Should().ThrowAsync<PostgresException>()).Which;
            refusal.SqlState.Should().Be("23514", "the grant reaches the guard, which admits no platform arm until Steps 2 and 3 add it");
            refusal.MessageText.Should().Be("Data request mutation refused.");
        }
    }

    [Fact]
    public async Task The_aggregates_round_trip_through_EF_as_the_application_role()
    {
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        var tenant = TenantId.From(SchemaFixture.TenantA);
        var subject = UserId.From(AdminA);
        await unit.SetProvisioningTenantContextAsync(tenant);
        await new IdentityMutationLocks(unit).AcquireAsync(new([subject], tenant, IdentityTenantLockMode.Shared));
        await using var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(unit.Connection).Options,
            new StaticTenantContextAccessor(new SeedTenantContext(tenant, null)), EmptyIdentityScope.Instance);
        await db.Database.UseTransactionAsync(unit.Transaction);
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var group = new DataRequestScopeGroup(tenant, null);

        var request = DataSubjectRequest.FileForSelf(DataSubjectRequestId.From(Guid.CreateVersion7()), DataRequestKind.Export, subject,
            group, [MembershipId.From(AdminMembershipA)], now).Value!;
        db.Add(request);
        await db.SaveChangesAsync();
        request.StartFulfilling(now.AddSeconds(1), UserId.SystemActor).IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        request.RecordParticipantCompleted("identity", now.AddSeconds(2), UserId.SystemActor).IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        var artifact = DataExportArtifact.Create(DataExportArtifactId.From(Guid.CreateVersion7()), request,
            2 * DataRequestLimits.ChunkPlaintextBytes + 7, "v1", now.AddSeconds(3), UserId.SystemActor).Value!;
        db.Add(artifact);
        await db.SaveChangesAsync();
        var connection = (NpgsqlConnection)unit.Connection;
        var transaction = (NpgsqlTransaction)unit.Transaction!;
        for (var index = 0; index < artifact.ChunkCount; index++)
            await InsertChunkAsync(connection, transaction, SchemaFixture.TenantA, null, artifact.Id.Value, index);
        request.Complete([new(RetainedReferenceKind.RequestReceipt, 1)], now.AddSeconds(4), UserId.SystemActor).IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        // The completeness check is deferred to commit; force it here, then return to the default.
        await ExecuteAsync(connection, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await ExecuteAsync(connection, transaction, "SET CONSTRAINTS ALL DEFERRED");
        var ticket = DataExportTicket.Mint(DataExportTicketId.From(Guid.CreateVersion7()), artifact, new string('c', 64),
            new string('d', 64), now.AddSeconds(5), subject).Value!;
        db.Add(ticket);
        await db.SaveChangesAsync();
        ticket.Consume(now.AddSeconds(6)).IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var stored = await db.Set<DataSubjectRequest>().SingleAsync(row => row.Id == request.Id);
        stored.Status.Should().Be(DataRequestStatus.Completed);
        stored.Kind.Should().Be(DataRequestKind.Export);
        stored.Origin.Should().Be(DataRequestOrigin.Self);
        stored.RequesterUserId.Should().Be(subject);
        stored.SnapshotMembershipIds.Should().Equal(MembershipId.From(AdminMembershipA));
        stored.Progress.Should().BeEquivalentTo(new Dictionary<string, DateTimeOffset> { ["identity"] = now.AddSeconds(2) });
        stored.RetainedReport.Should().BeEquivalentTo(new Dictionary<RetainedReferenceKind, long> { [RetainedReferenceKind.RequestReceipt] = 1 });
        stored.CompletedAt.Should().Be(now.AddSeconds(4));
        stored.Version.Should().Be(3);
        var storedArtifact = await db.Set<DataExportArtifact>().SingleAsync(row => row.Id == artifact.Id);
        storedArtifact.ChunkCount.Should().Be(3);
        storedArtifact.ExpiresAt.Should().Be(now.AddSeconds(3) + DataRequestLimits.ArtifactLifetime);
        var storedTicket = await db.Set<DataExportTicket>().SingleAsync(row => row.Id == ticket.Id);
        storedTicket.ConsumedAt.Should().Be(now.AddSeconds(6));
        storedTicket.Matches(new string('c', 64), new string('d', 64)).Should().BeTrue("the digests round-trip through bytea");
        (await db.Set<DataSubjectRequest>().CountAsync(row => row.Status == DataRequestStatus.Completed && row.Kind == DataRequestKind.Export))
            .Should().BeGreaterThanOrEqualTo(2, "the closed-code converter translates into the stored spelling");

        storedArtifact.Revoke(now.AddSeconds(7), subject).IsSuccess.Should().BeTrue();
        await db.SaveChangesAsync();
        await ExecuteAsync(connection, transaction, "DELETE FROM public.data_export_chunks WHERE artifact_id=@id", ("id", artifact.Id.Value));
        await ExecuteAsync(connection, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await frame.FailAsync();
    }

    /// <summary>A fresh tenant-wide export of AdminA, fulfilling, with an artifact of the given size and no chunks yet.</summary>
    private static async Task<Guid> AdminExportArtifactAsync(NpgsqlConnection app, NpgsqlTransaction transaction, long bytes)
    {
        await FenceAsync(app, transaction, SchemaFixture.TenantA, null, AdminA);
        var request = Guid.CreateVersion7();
        var artifact = Guid.CreateVersion7();
        await InsertRequestAsync(app, transaction, request, SchemaFixture.TenantA, null, AdminA, [AdminMembershipA]);
        await StepAsync(app, transaction, request, "status='fulfilling'");
        await InsertArtifactAsync(app, transaction, artifact, SchemaFixture.TenantA, null, request, AdminA, bytes);
        return artifact;
    }

    private static Task RevokeAsync(NpgsqlConnection app, NpgsqlTransaction transaction, Guid artifact) =>
        ExecuteAsync(app, transaction, """
            UPDATE public.data_export_artifacts SET status='revoked',purged_at=now(),row_version=row_version+1,
                updated_at=now(),updated_by=@subject WHERE id=@id
            """, ("id", artifact), ("subject", AdminA));

    /// <summary>Runs a statement expected to fail inside a savepoint, so the transaction continues.</summary>
    private static async Task<PostgresException> RefuseAsync(NpgsqlTransaction transaction, Func<Task> act)
    {
        await transaction.SaveAsync("probe");
        var refusal = (await act.Should().ThrowAsync<PostgresException>()).Which;
        await transaction.RollbackAsync("probe");
        return refusal;
    }

    private static Task<long> CountAsync(NpgsqlConnection app, NpgsqlTransaction transaction, string table, string predicate = "true") =>
        ScalarAsync<long>(app, transaction, $"SELECT count(*) FROM public.{table} WHERE {predicate}");

    private static async Task<int> ExecuteCountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenAppAsync()
    {
        var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        return app;
    }
}
