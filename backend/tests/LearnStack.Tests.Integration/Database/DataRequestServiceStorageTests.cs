using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Messaging;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The request API's storage seams as <c>learnstack_app</c> on the seeded data-request rows: the explicit group
/// predicate that the canonical row policy alone does not supply, the one-active index mapped to a refusal rather
/// than an exception, and the exact Membership snapshot the creation guard demands.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DataRequestServiceStorageTests(SchemaFixture fixture)
{
    [Fact]
    public async Task Reads_name_exactly_the_announced_group_although_the_row_policy_shows_an_organization_the_tenant_wide_rows()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(fixture.Postgres);
        await using (var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, SchemaFixture.OrgA1))
        {
            // The policy alone admits the tenant-wide group's row into an organization context.
            (await ScalarAsync<long>(scope.Connection, scope.Transaction, "SELECT count(*) FROM public.data_subject_requests WHERE organization_id IS NULL"))
                .Should().Be(1, "the canonical row policy shows an organization context the tenant-wide rows");
            var reader = new DataRequestReader(scope.Db, scope.Unit, scope.Context);
            (await reader.ListAsync(null, null, 101, CancellationToken.None)).Select(request => request.Id.Value)
                .Should().Equal(BranchA1.Request);
            (await reader.ListAsync(UserId.From(PersonA), null, 101, CancellationToken.None)).Select(request => request.Id.Value)
                .Should().Equal(BranchA1.Request);
            (await reader.FindAsync(DataSubjectRequestId.From(TenantWideA.Request), CancellationToken.None)).Should().BeNull();
            (await reader.ReadArtifactsAsync([DataSubjectRequestId.From(TenantWideA.Request)], CancellationToken.None)).Should().BeEmpty();
            (await reader.FindArtifactLockedAsync(DataSubjectRequestId.From(TenantWideA.Request), CancellationToken.None)).Should().BeNull();
            (await Store(scope).ReadSubjectMembershipsAsync(UserId.From(PersonA), CancellationToken.None)).Select(member => member.Value)
                .Should().Equal(BranchMembershipA1);
            (await reader.FindMembershipSubjectAsync(MembershipId.From(LearnerMembershipA), CancellationToken.None)).Should().BeNull(
                "another group's Membership is absent");
            await scope.Frame.FailAsync();
        }

        await using (var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, null))
        {
            var reader = new DataRequestReader(scope.Db, scope.Unit, scope.Context);
            (await reader.ListAsync(null, null, 101, CancellationToken.None)).Select(request => request.Id.Value)
                .Should().Equal(TenantWideA.Request);
            var artifacts = await reader.ReadArtifactsAsync([DataSubjectRequestId.From(TenantWideA.Request)], CancellationToken.None);
            artifacts.Should().ContainSingle().Which.Value.Readable.Should().BeTrue();
            (await reader.FindMembershipSubjectAsync(MembershipId.From(LearnerMembershipA), CancellationToken.None))
                .Should().Be(UserId.From(PersonA));
            await scope.Frame.FailAsync();
        }
    }

    [Fact]
    public async Task The_one_active_index_refuses_a_second_active_export_as_a_mapped_refusal_that_leaves_the_transaction_rollback_only()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(fixture.Postgres);
        await using var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantB, SchemaFixture.OrgB1,
            [PersonB]);
        var store = Store(scope);
        var active = await store.FindActiveLockedAsync(UserId.From(PersonB), DataRequestKind.Export, CancellationToken.None);
        active!.Id.Value.Should().Be(BranchB.Request, "the seeded requested export holds the slot");
        // Bypass the eligibility judgment on purpose: the index is the backstop a race meets.
        var second = DataSubjectRequest.FileForSelf(DataSubjectRequestId.From(Guid.CreateVersion7()), DataRequestKind.Export, UserId.From(PersonB),
            new DataRequestScopeGroup(scope.Context.TenantId, scope.Context.OrganizationId), [MembershipId.From(BranchMembershipB)],
            DateTimeOffset.UtcNow).Value!;
        (await store.TryAddAsync(second, CancellationToken.None)).Should().BeFalse();
        scope.Unit.IsRollbackOnly.Should().BeTrue();
        scope.Db.ChangeTracker.Entries<DataSubjectRequest>().Should().NotContain(entry => entry.Entity == second);
    }

    /// <summary>
    /// A storage wait past the five-second statement budget is the retryable storage failure (503), never a 500, and it
    /// leaves the owner rollback-only: a cancellation behind an export job's request row lock, and a chunk read behind a
    /// lock on the chunk table.
    /// </summary>
    [Theory]
    [InlineData("request_row_lock")]
    [InlineData("chunk_read")]
    public async Task A_wait_past_the_statement_budget_is_the_retryable_storage_failure_and_leaves_the_owner_rollback_only(string wait)
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(fixture.Postgres);
        await using var holder = (NpgsqlConnection)await PostgresFixture.OpenAsync(wait == "chunk_read"
            ? database.MigrationConnectionString : database.AppConnectionString);
        await using var held = await holder.BeginTransactionAsync();
        if (wait == "chunk_read")
            await ExecuteAsync(holder, held, "LOCK TABLE public.data_export_chunks IN ACCESS EXCLUSIVE MODE");
        else
        {
            await ScopeAsync(holder, held, SchemaFixture.TenantA);
            await ExecuteAsync(holder, held, "SELECT id FROM public.data_subject_requests WHERE id=@id FOR UPDATE", ("id", TenantWideA.Request));
        }

        await using var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, null);
        using var cipher = DataExportTestKit.Cipher();
        var started = System.Diagnostics.Stopwatch.StartNew();
        Func<Task> act = wait == "chunk_read"
            ? () => DataExportArtifactStoreTests.Store(scope, cipher).ReadChunkAsync(new DataExportArtifactLocator(TenantWideA.Artifact!.Value,
                new DataRequestScopeGroup(scope.Context.TenantId, null)), 0, CancellationToken.None)
            : () => Store(scope).FindLockedAsync(DataSubjectRequestId.From(TenantWideA.Request), CancellationToken.None);

        (await act.Should().ThrowAsync<InfrastructureException>()).Which.Error.Code.Should().Be("dependency_unavailable");
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "the budget bounds the wait");
        scope.Unit.IsRollbackOnly.Should().BeTrue("a timed-out statement leaves the owner unusable for anything but rollback");
        await held.RollbackAsync();
    }

    /// <summary>
    /// ADR-0106 § 8: a receipt reads its export job's phase by the job's exact route. Among thousands of the tenant's
    /// jobs of hundreds of other definitions, the generic plan of the reader's statement (the shape a parameterized
    /// command is planned with) seeks <c>ix_background_jobs_definition</c> on the tenant and the definition key and
    /// filters only the group's export jobs; with the index removed the same statement filters the tenant's history. Both
    /// statements the reader runs, the single read and the page read, are explained verbatim: the test reads them from
    /// the reader's own constants.
    /// </summary>
    [Fact]
    public async Task The_job_phase_read_seeks_the_definition_index_instead_of_the_tenants_job_history()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(fixture.Postgres);
        var request = Guid.CreateVersion7();
        var parameters = BackgroundJobPayloadContract.Serialize(new DataExportJobParameters(request), typeof(DataExportJobParameters),
            BackgroundJobPayloadKind.Parameters);
        var definition = DataExportJobKeys.ExportFor(new DataRequestScopeGroup(TenantId.From(SchemaFixture.TenantA), null)).Value;
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO background_jobs (id, tenant_id, definition_key, profile, queue, parameters, original_due_at, traceparent)
                SELECT uuidv7(), @tenant, 'fixture.backlog_' || (n % 400) || '.v1', 'transactional', 'fixture', '{}',
                    clock_timestamp() + interval '1 day', @trace FROM generate_series(1, 8000) n;
                INSERT INTO background_jobs (id, tenant_id, definition_key, profile, queue, parameters, original_due_at, traceparent)
                SELECT uuidv7(), @tenant, @definition, 'transactional', 'fixture',
                    CASE WHEN n = 1 THEN @parameters ELSE '{"requestId":"' || uuidv7() || '"}' END,
                    clock_timestamp() + interval '1 day', @trace FROM generate_series(1, 12) n;
                """, ("tenant", SchemaFixture.TenantA), ("trace", BackgroundJobSchemaSeed.Traceparent), ("definition", definition),
                ("parameters", parameters));
            await transaction.CommitAsync();
        }

        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaQueries.ExecuteAsync(owner, null, "ANALYZE background_jobs");
        (await SchemaQueries.ReadStringsAsync(owner, """
            SELECT indexname || ':' || indexdef FROM pg_indexes
            WHERE schemaname = 'public' AND indexname IN ('ix_background_jobs_definition', 'ix_data_subject_requests_group_page')
            ORDER BY indexname
            """)).Should().Equal(
            "ix_background_jobs_definition:CREATE INDEX ix_background_jobs_definition ON public.background_jobs USING btree (tenant_id, definition_key, organization_id)",
            "ix_data_subject_requests_group_page:CREATE INDEX ix_data_subject_requests_group_page ON public.data_subject_requests USING btree (tenant_id, organization_id, id)");

        foreach (var page in new[] { false, true })
        {
            var clean = await PhasePlanAsync(database, parameters, definition, page);
            PlanIndexes(clean).Should().Contain("ix_background_jobs_definition", page ? "the page read" : "the single read");
            MaximumRowsRemoved(clean).Should().BeLessThan(100, "the seek reads the group's export jobs, not the tenant's history");
        }

        await using (var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, null))
        {
            var reader = new DataRequestJobPhaseReader(scope.Unit, scope.Context);
            (await reader.ReadAsync(DataSubjectRequestId.From(request), DataRequestKind.Export, CancellationToken.None))
                .Should().Be(DataRequestJobPhase.Pending, "the reader finds its job");
            var unknown = DataSubjectRequestId.From(Guid.CreateVersion7());
            (await reader.ReadManyAsync([DataSubjectRequestId.From(request), unknown], CancellationToken.None)).Should().BeEquivalentTo(
                new Dictionary<DataSubjectRequestId, DataRequestJobPhase>
                {
                    [DataSubjectRequestId.From(request)] = DataRequestJobPhase.Pending,
                    [unknown] = DataRequestJobPhase.Unavailable,
                }, "the page read finds its job among the group's other export jobs and none for a request without one");
            await scope.Frame.FailAsync();
        }

        await SchemaQueries.ExecuteAsync(owner, null, "DROP INDEX public.ix_background_jobs_definition");
        foreach (var page in new[] { false, true })
        {
            var dirty = await PhasePlanAsync(database, parameters, definition, page);
            PlanIndexes(dirty).Should().NotContain("ix_background_jobs_definition");
            MaximumRowsRemoved(dirty).Should().BeGreaterThanOrEqualTo(1000, "without it the same statement filters the tenant's history");
        }
    }

    /// <summary>
    /// ADR-0107 § 4's ticket bounds read by the database clock in one statement: the open tickets of one session binding
    /// (a consumed one and another binding's do not count) and every ticket ever minted over the artifact.
    /// </summary>
    [Fact]
    public async Task The_ticket_counts_read_one_bindings_open_tickets_and_every_ticket_ever_minted()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(fixture.Postgres);
        var artifact = TenantWideA.Artifact!.Value;
        // The seeded ticket's binding is sha256(uuid_send(artifact)); uuid_send writes the identifier big-endian.
        var seeded = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(artifact.ToByteArray(bigEndian: true)));
        var other = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("other"u8));
        await using var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, null);
        await InsertTicketAsync(scope.Connection, scope.Transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, null, artifact, PersonA);
        var consumed = Guid.CreateVersion7();
        await ExecuteAsync(scope.Connection, scope.Transaction, """
            INSERT INTO public.data_export_tickets(id,tenant_id,organization_id,artifact_id,digest,session_binding_digest,
                created_at,created_by,expires_at)
            VALUES(@id,@tenant,NULL,@artifact,sha256(uuid_send(@id)),sha256('other'::bytea),now(),@subject,now()+interval '15 minutes')
            """, ("id", consumed), ("tenant", SchemaFixture.TenantA), ("artifact", artifact), ("subject", PersonA));
        await ExecuteAsync(scope.Connection, scope.Transaction,
            "UPDATE public.data_export_tickets SET consumed_at=now() WHERE id=@id", ("id", consumed));
        var store = new DataExportTicketStore(scope.Db, scope.Unit, scope.Context);
        var id = DataExportArtifactId.From(artifact);

        (await store.CountAsync(id, seeded, CancellationToken.None)).Should().Be(new DataExportTicketCounts(2, 3),
            "the seeded ticket and the new one share the seeded binding; the consumed one is another binding's");
        (await store.CountAsync(id, other, CancellationToken.None)).Should().Be(new DataExportTicketCounts(0, 3),
            "a consumed ticket is no longer open, but it was minted");
        (await store.CountAsync(id, new string('0', 64), CancellationToken.None)).Should().Be(new DataExportTicketCounts(0, 3));
        await FluentActions.Awaiting(() => store.CountAsync(id, "not-a-digest", CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        await scope.Frame.FailAsync();
    }

    /// <summary>
    /// One of the phase reader's two statements, read from its constant, explained as the generic plan a parameterized
    /// command gets: the named parameters become positional ones of the types the reader binds.
    /// </summary>
    private static async Task<string> PhasePlanAsync(DisposableSchemaDatabase database, string parameters, string definition, bool page)
    {
        var statement = (string)typeof(DataRequestJobPhaseReader)
            .GetField(page ? "PageStatement" : "SingleStatement", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        var positional = statement.Replace("@tenant", "$1", StringComparison.Ordinal)
            .Replace("@organization", "$2", StringComparison.Ordinal)
            .Replace("@definition", "$3", StringComparison.Ordinal)
            .Replace("@parameters", "$4", StringComparison.Ordinal);
        positional.Should().NotContain("@", "every named parameter of the reader's statement is bound here");
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await SchemaQueries.ExecuteAsync(app, transaction, "DEALLOCATE ALL; SET LOCAL plan_cache_mode = force_generic_plan;\n"
            + "PREPARE phase_read(uuid, uuid, text, " + (page ? "text[]" : "text") + ") AS\n" + positional + ";");
        // A generic plan is fixed before its arguments are seen, so these test-owned literals do not shape it.
        var argument = "'" + parameters.Replace("'", "''", StringComparison.Ordinal) + "'";
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, FORMAT JSON) EXECUTE phase_read('"
            + SchemaFixture.TenantA.ToString("D") + "'::uuid, NULL, '" + definition.Replace("'", "''", StringComparison.Ordinal) + "', "
            + (page ? "ARRAY[" + argument + "]::text[]" : argument) + ")", app, transaction);
        var plan = (string)(await command.ExecuteScalarAsync())!;
        await SchemaQueries.ExecuteAsync(app, transaction, "DEALLOCATE phase_read");
        await transaction.RollbackAsync();
        return plan;
    }

    private static string[] PlanIndexes(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Walk(document.RootElement[0].GetProperty("Plan")).Where(node => node.TryGetProperty("Index Name", out _))
            .Select(node => node.GetProperty("Index Name").GetString()!).ToArray();
    }

    private static long MaximumRowsRemoved(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Walk(document.RootElement[0].GetProperty("Plan")).Where(node => node.TryGetProperty("Rows Removed by Filter", out _))
            .Select(node => node.GetProperty("Rows Removed by Filter").GetInt64()).DefaultIfEmpty().Max();
    }

    private static IEnumerable<JsonElement> Walk(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in Walk(child))
                    yield return descendant;
    }

    private static DataRequestStore Store(DataExportTestKit.GroupScope scope) =>
        new(scope.Db, scope.Unit, new ProducerExecution(scope.Unit), scope.Context);
}
