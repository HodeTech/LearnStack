using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// A closure Finalize's audit redaction against PostgreSQL (ADR-0107 § 7, ADR-0108 §§ 4, 8). The pass over the platform
/// sentinel tenant selects exactly the person's global creation rows, the failed and undetermined attempts that only the
/// subject key finds, and the rows naming the person as actor or User, and nothing else of that tenant or of any other;
/// it is idempotent and batches as the tenant pass does. The real platform scope, entered for the closure's Finalize,
/// runs every pass on its first transaction, which commits with the intent row carrying their summed counts, and refuses
/// an erasure's plan, as the erasure's completion refuses a closure's passes.
/// </summary>
/// <remarks>
/// Each case has a database of its own: the global creation rows are written as <c>learnstack_app</c> under the creation
/// guard and committed, the platform role's planted rows beside them, and the shared schema's censuses count rows. Every
/// redaction under test runs as <c>learnstack_platform</c>.
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class ClosureAuditRedactionTests(SchemaFixture schema)
{
    private static readonly string SystemActorText = UserId.SystemActor.Value.ToString("D");

    /// <summary>The sentinel rows the pass selects, of the world <see cref="SeedAsync"/> writes.</summary>
    private static readonly string[] SentinelSelected = ["creation", "failed attempt", "undetermined attempt", "actor", "user entity"];

    [Fact]
    public async Task The_sentinel_pass_selects_exactly_the_person_s_global_rows_redacts_them_and_reports_what_survives()
    {
        await using var world = await SeedAsync();
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(world.Database.PlatformConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        var before = await ReadAsync(connection, transaction, world);

        var report = await Executor().RedactAsync(world.SentinelPass, Spent(PlatformOperations.ClosureFinalize), connection, transaction);

        var after = await ReadAsync(connection, transaction, world);
        Changed(world, before, after).Should().BeEquivalentTo(["undetermined attempt", "actor"],
            "a selected row changes where a granted column holds something of the person's, and no other row changes");
        world.Rows.Keys.Should().OnlyContain(label => after[label].Retained == before[label].Retained,
            "the key, the actor, the entity and every column outside the grant survive (ADR-0108 § 6)");
        (after["actor"].IpAddress, after["actor"].UserAgent, after["actor"].ActorEmail).Should().Be((null, null, null));
        var attempt = JsonNode.Parse(after["undetermined attempt"].After!)!;
        attempt["UpdatedBy"]!.GetValue<string>().Should().Be(SensitiveTokenCatalog.RedactedValue, "a declared path naming the person");
        attempt["CreatedBy"]!.GetValue<string>().Should().Be(SystemActorText, "the system attribution is no one's");
        after["failed attempt"].Should().Be(before["failed attempt"], "a creation row holds nothing of the person's in a granted column");

        report.RowsSelected.Should().Be(SentinelSelected.Length);
        Columns(report).Should().Equal(1, 1, 1, 0, 1, 0);
        Kinds(report).Should().Equal(new Dictionary<RetainedReferenceKind, long>
        {
            [RetainedReferenceKind.AuditActorReference] = 1,
            [RetainedReferenceKind.AuditEntityReference] = 2,
            [RetainedReferenceKind.AuditMetadataReference] = 3,
        }, "the actor row, the creation and the User entity row, and the three rows carrying the subject key");

        // Idempotent: the re-run selects the same rows, by key, actor and entity, and rewrites none of them.
        var again = await Executor().RedactAsync(world.SentinelPass, Spent(PlatformOperations.ClosureFinalize), connection, transaction);
        again.RowsSelected.Should().Be(SentinelSelected.Length);
        again.Redacted.Values.Should().AllSatisfy(count => count.Should().Be(0));
        Kinds(again).Should().Equal(Kinds(report));
        (await ReadAsync(connection, transaction, world)).Should().Equal(after);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Small_batches_draw_several_batches_over_the_sentinel_and_select_and_redact_exactly_what_one_batch_does()
    {
        await using var world = await SeedAsync();
        var single = await BatchRunAsync(world, Executor());
        var tiny = await BatchRunAsync(world, new AuditRedactionExecutor(Locators(), batchRows: 2, batchBytes: 300, updateRows: 1));

        single.Boundaries.Should().Be(1, "the production sizes read the sentinel tenant in one batch");
        tiny.Boundaries.Should().BeGreaterThan(3, "two rows a batch over the sentinel tenant's rows draws several");
        tiny.After.Should().Equal(single.After);
        tiny.Report.RowsSelected.Should().Be(single.Report.RowsSelected).And.Be(SentinelSelected.Length);
        tiny.Report.Redacted.Should().Equal(single.Report.Redacted);
        Kinds(tiny.Report).Should().Equal(Kinds(single.Report));
    }

    /// <summary>
    /// The sentinel selection walks no payload, so the sentinel pass's batch boundary sizes none: with a byte budget every
    /// payload exceeds and a row cap above the tenant's rows, it reads the whole sentinel tenant in one batch, where a pass
    /// that sized payloads would end a batch at each sentinel row that carries one.
    /// </summary>
    [Fact]
    public async Task The_sentinel_pass_sizes_no_payload_so_only_the_row_cap_ends_its_batches()
    {
        await using var world = await SeedAsync();
        await using (var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(world.Database.PlatformConnectionString))
        {
            (await IdentityMembershipFixture.ScalarAsync<long>(platform, null, """
                SELECT count(*) FROM public.audit_log
                WHERE tenant_id = @sentinel AND (before_state IS NOT NULL OR after_state IS NOT NULL OR changes IS NOT NULL)
                """, ("sentinel", TenantId.PlatformSentinel.Value)))
                .Should().BeGreaterThan(1, "the premise: several sentinel rows carry a payload a byte budget of one would cut at");
        }

        var run = await BatchRunAsync(world, new AuditRedactionExecutor(Locators(), batchRows: 1_000, batchBytes: 1, updateRows: 1));

        run.Boundaries.Should().Be(1, "the row cap alone ends a sentinel batch, and no payload is decompressed to be sized");
        run.Report.RowsSelected.Should().Be(SentinelSelected.Length);
    }

    [Fact]
    public async Task Through_the_real_scope_Finalize_redacts_every_participant_and_the_sentinel_and_commits_it_with_its_intent_row()
    {
        await using var world = await SeedAsync();
        await using var platform = PersistenceCompositionExtensions.BuildPlatformDataSource(world.Database.PlatformConnectionString);
        var scope = Scope(platform);
        var request = Guid.CreateVersion7();
        var original = await SnapshotAsync(world);

        // An act abandoned before its checkpoint redacts nothing: the passes ran on the first transaction, which the
        // disposal rolls back, so no state exists where the redaction is done and its evidence is not (ADR-0108 § 8).
        var abandoned = Admission("CHG-311");
        await using (var handle = await scope.EnterAsync(abandoned))
        {
            await handle.RedactAuditAsync(world.TenantPass(world.ParticipantA));
            await handle.RedactAuditAsync(world.SentinelPass);
        }

        (await SnapshotAsync(world)).Should().Equal(original, "the abandoned act's redaction rolled back");
        (await RowsAsync(world, abandoned.Operator.CorrelationId!)).Select(row => row.Operation)
            .Should().Equal(PlatformAdminScope.EnterOperation);

        // The act: every pass on the first transaction, the intent row carrying their sum at the checkpoint, then the
        // result row on the second.
        var admission = Admission("CHG-312");
        AuditRedactionAggregate aggregate;
        await using (var handle = await scope.EnterAsync(admission))
        {
            var first = await handle.RedactAuditAsync(world.TenantPass(world.ParticipantA));
            var second = await handle.RedactAuditAsync(world.TenantPass(world.ParticipantB));
            var sentinel = await handle.RedactAuditAsync(world.SentinelPass);
            var erasure = async () => await handle.RedactAuditAsync(new AuditRedactionPlan(new UserDataSubject(request,
                DataRequestKind.Erasure, DataRequestOrigin.ClosureFanout, UserId.From(world.Person),
                new DataRequestScopeGroup(TenantId.From(world.ParticipantA), null), [world.MembershipA])));
            await erasure.Should().ThrowAsync<InvalidOperationException>().WithMessage("*may not redact*",
                "the closure's Finalize redacts its own passes, never an erasure's plan");
            (await handle.QueryAsync("SELECT 1", null, reader => reader.GetInt32(0))).Should().Equal([1],
                "a refusal before anything ran leaves the act usable");

            aggregate = new AuditRedactionAggregate(
                new Dictionary<TenantId, AuditRedactionReport>
                {
                    [TenantId.From(world.ParticipantA)] = first,
                    [TenantId.From(world.ParticipantB)] = second,
                },
                sentinel);
            await handle.WriteIntentAsync(new PlatformResult(
                TenantId.PlatformSentinel, "DataSubjectRequest", request.ToString("D"), null, aggregate.ToClosedJson()));

            // The checkpoint committed the redaction with its evidence, before any effect the act goes on to make.
            var committed = await SnapshotAsync(world);
            ChangedLabels(original, committed).Should().BeEquivalentTo(
                ["undetermined attempt", "actor", "a actor", "a membership", "b payload", "b membership"]);
            var intent = (await RowsAsync(world, admission.Operator.CorrelationId!)).Should().HaveCount(2).And.Subject.Last();
            (intent.Tenant, intent.Operation, intent.Outcome, intent.Reason, intent.Actor).Should().Be(
                (TenantId.PlatformSentinel.Value, "platform.closure.finalize_attempt", "indeterminate", "closure_finalize", (Guid?)null));
            JsonNode.DeepEquals(JsonNode.Parse(intent.After!), JsonNode.Parse(aggregate.ToClosedJson())).Should().BeTrue(intent.After);
            intent.Metadata.Should().Contain("CHG-312", "a sentinel row carries the operator's ticket");

            await handle.WriteResultAsync(new PlatformResult(
                TenantId.PlatformSentinel, "DataSubjectRequest", request.ToString("D"), null, """{"status":"completed"}"""));
            await handle.CommitAsync();
        }

        var rows = await RowsAsync(world, admission.Operator.CorrelationId!);
        rows.Select(row => (row.Operation, row.Outcome)).Should().Equal(
            (PlatformAdminScope.EnterOperation, "success"), ("platform.closure.finalize_attempt", "indeterminate"),
            ("platform.closure.finalize", "success"));
        rows.Should().OnlyContain(row => row.Tenant == TenantId.PlatformSentinel.Value && row.Actor == null);

        // The document is counts and tenant identifiers, never the person.
        var document = aggregate.ToClosedJson();
        document.Should().Contain(world.ParticipantA.ToString("D")).And.Contain(world.ParticipantB.ToString("D"))
            .And.NotContain(world.Person.ToString("D")).And.NotContain(world.Key.Hex);
        aggregate.Total.RowsSelected.Should().Be(9, "two rows in each participant and five in the sentinel");
        Columns(aggregate.Total).Should().Equal(5, 5, 5, 0, 2, 0);
        Kinds(aggregate.Total).Should().Equal(new Dictionary<RetainedReferenceKind, long>
        {
            [RetainedReferenceKind.AuditActorReference] = 2,
            [RetainedReferenceKind.AuditEntityReference] = 4,
            [RetainedReferenceKind.AuditMetadataReference] = 3,
        });

        // A repeat redacts nothing more, and the erasure's completion is refused a closure's pass, leaving its act usable.
        await using (var repeat = await scope.EnterAsync(Admission(null)))
        {
            foreach (var pass in new[] { world.TenantPass(world.ParticipantA), world.TenantPass(world.ParticipantB), world.SentinelPass })
            {
                (await repeat.RedactAuditAsync(pass)).Redacted.Values.Should().AllSatisfy(count => count.Should().Be(0));
            }
        }

        await using (var completion = await scope.EnterAsync(TestPlatformAdmissions.Admission(PlatformOperations.ErasureComplete)))
        {
            foreach (var pass in new[] { world.TenantPass(world.ParticipantA), world.SentinelPass })
            {
                var redact = async () => await completion.RedactAuditAsync(pass);
                await redact.Should().ThrowAsync<InvalidOperationException>().WithMessage("*may not redact*");
            }

            (await completion.QueryAsync("SELECT 1", null, reader => reader.GetInt32(0))).Should().Equal([1]);
        }

        ChangedLabels(original, await SnapshotAsync(world)).Should().BeEquivalentTo(
            ["undetermined attempt", "actor", "a actor", "a membership", "b payload", "b membership"],
            "the outsider tenant, the other person and the operator rows were never touched");
    }

    /// <summary>
    /// The world: the person and another, each with a real global creation row written under the creation guard; the
    /// person's failed attempt (a captured User the person never became) and undetermined attempt (whose state names the
    /// person at a declared path, which no creation writes today), and the other's failed attempt, each found by its key
    /// alone; platform-role rows of the sentinel tenant naming the person as actor, as User entity, as a Membership entity
    /// (not a User), and an operator's and the other person's rows; and two participant tenants and an outsider, whose
    /// rows name the person as actor, as Membership entity or in a payload.
    /// </summary>
    private async Task<World> SeedAsync()
    {
        var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            await IdentityAuthorityDeploymentInstaller.InstallAsync(owner, IdentityMembershipFixture.Authority);
        }

        var (person, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await IdentityMembershipFixture.InsertUserAsync(database.AppConnectionString, person);
        await IdentityMembershipFixture.InsertUserAsync(database.AppConnectionString, other);
        var world = new World(database, person, Subject(person).SubjectKey, Guid.CreateVersion7(), Guid.CreateVersion7(),
            Guid.CreateVersion7(), Guid.CreateVersion7());
        var sentinel = TenantId.PlatformSentinel.Value;
        string Text(Guid value) => value.ToString("D");
        string Course(Guid? creator, Guid? updater) =>
            new JsonObject { ["Id"] = Text(Guid.CreateVersion7()), ["CreatedBy"] = Text(creator ?? other), ["UpdatedBy"] = Text(updater ?? other) }
                .ToJsonString();
        string Captured(string updater) =>
            new JsonObject { ["Id"] = Text(Guid.CreateVersion7()), ["Status"] = "Active", ["CreatedBy"] = SystemActorText, ["UpdatedBy"] = updater }
                .ToJsonString();

        world.Rows["creation"] = await CreationAsync(database, person);
        world.Rows["other creation"] = await CreationAsync(database, other);
        world.Rows["failed attempt"] = await AttemptAsync(database, person, "failed", Captured(SystemActorText));
        world.Rows["undetermined attempt"] = await AttemptAsync(database, person, "indeterminate", Captured(Text(person)));
        world.Rows["other failed attempt"] = await AttemptAsync(database, other, "failed", Captured(Text(other)));
        world.Rows["actor"] = await PlantAsync(database, sentinel, person, "Course", Text(Guid.CreateVersion7()), null);
        world.Rows["user entity"] = await PlantAsync(database, sentinel, null, "User", Text(person), null, attributed: false);
        world.Rows["membership entity"] = await PlantAsync(database, sentinel, other, "Membership", Text(person), null);
        world.Rows["operator"] = await PlantAsync(database, sentinel, null, "DataSubjectRequest", Text(Guid.CreateVersion7()), null);
        world.Rows["other actor"] = await PlantAsync(database, sentinel, other, "Course", Text(Guid.CreateVersion7()), null);
        world.Rows["a actor"] = await PlantAsync(database, world.ParticipantA, person, "Course", Text(Guid.CreateVersion7()), null);
        world.Rows["a membership"] = await PlantAsync(database, world.ParticipantA, other, "Membership", Text(world.MembershipA), null);
        world.Rows["a other"] = await PlantAsync(database, world.ParticipantA, other, "Course", Text(Guid.CreateVersion7()), Course(null, null));
        world.Rows["b payload"] = await PlantAsync(database, world.ParticipantB, other, "Course", Text(Guid.CreateVersion7()), Course(null, person));
        world.Rows["b membership"] = await PlantAsync(database, world.ParticipantB, other, "Membership", Text(world.MembershipB), null);
        world.Rows["outsider actor"] = await PlantAsync(database, Guid.CreateVersion7(), person, "Course", Text(Guid.CreateVersion7()), Course(person, person));
        return world;
    }

    private static GlobalIdentitySubject Subject(Guid user) => new(IdentityMembershipFixture.Authority.AuthorityId,
        IdentityMembershipFixture.Authority.Issuer, "fixture-subject-" + user.ToString("N"));

    /// <summary>The identifier of a user's global creation row, which the fixture wrote under the creation guard.</summary>
    private static async Task<Guid> CreationAsync(DisposableSchemaDatabase database, Guid user)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT id FROM public.audit_log WHERE operation = 'identity.user.create' AND outcome = 'success' AND entity_id = @user",
            connection);
        command.Parameters.AddWithValue("user", user.ToString("D"));
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// A creation attempt that never became a User, as the application role writes it under the person's identity
    /// announcement and the creation guard: its entity is a fresh identifier, so only the subject key finds it.
    /// </summary>
    private static async Task<Guid> AttemptAsync(DisposableSchemaDatabase database, Guid user, string outcome, string after)
    {
        var subject = Subject(user);
        var id = Guid.CreateVersion7();
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await GlobalIdentitySchemaTests.AnnounceAsync(app, transaction, subject);
        await using (var command = new NpgsqlCommand("""
            INSERT INTO public.audit_log(id, "timestamp", tenant_id, organization_id, actor_user_id, module, operation, operation_type,
                operation_class, entity_type, entity_id, outcome, after_state, metadata)
            VALUES(@id, clock_timestamp(), @sentinel, NULL, NULL, 'identity', 'identity.user.create', 'SecurityEvent', 'Must', 'User',
                @target, @outcome, @after::jsonb, @metadata::jsonb)
            """, app, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("sentinel", TenantId.PlatformSentinel.Value);
            command.Parameters.AddWithValue("target", Guid.CreateVersion7().ToString("D"));
            command.Parameters.AddWithValue("outcome", outcome);
            command.Parameters.AddWithValue("after", after);
            command.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(new
            {
                identity_authority_id = subject.AuthorityId.Value.ToString("D"),
                identity_subject_key = subject.SubjectKey.Hex,
                identity_source = "identity-bootstrap",
            }));
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return id;
    }

    /// <summary>A row the platform role writes, with an address, an agent and an e-mail unless it is unattributed.</summary>
    private static async Task<Guid> PlantAsync(DisposableSchemaDatabase database, Guid tenant, Guid? actor, string entityType,
        string entityId, string? after, bool attributed = true)
    {
        var id = Guid.CreateVersion7();
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.PlatformConnectionString);
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.audit_log (id, "timestamp", tenant_id, organization_id, actor_user_id, actor_email, module, operation,
                operation_type, operation_class, entity_type, entity_id, outcome, after_state, correlation_id, ip_address, user_agent)
            VALUES (@id, clock_timestamp(), @tenant, NULL, @actor, @email, 'canary', 'canary.closure.row', 'Update', 'Must',
                @entity_type, @entity_id, 'success', @after, @correlation, @address, @agent)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, (object?)actor ?? DBNull.Value);
        command.Parameters.AddWithValue("email", NpgsqlDbType.Varchar, attributed ? "actor@example.test" : DBNull.Value);
        command.Parameters.AddWithValue("entity_type", entityType);
        command.Parameters.AddWithValue("entity_id", entityId);
        command.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, (object?)after ?? DBNull.Value);
        command.Parameters.AddWithValue("correlation", "00-" + id.ToString("N") + "-01");
        command.Parameters.AddWithValue("address", NpgsqlDbType.Inet, attributed ? System.Net.IPAddress.Parse("198.51.100.7") : DBNull.Value);
        command.Parameters.AddWithValue("agent", NpgsqlDbType.Varchar, attributed ? "Canary/1.0" : DBNull.Value);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>Every labelled row's six granted columns, and every other column as one canonical document.</summary>
    private static async Task<Dictionary<string, Snapshot>> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, World world)
    {
        await using var command = new NpgsqlCommand("""
            SELECT a.id, host(a.ip_address), a.user_agent, a.actor_email, a.before_state::text, a.after_state::text, a.changes::text,
                (to_jsonb(a) - 'ip_address' - 'user_agent' - 'actor_email' - 'before_state' - 'after_state' - 'changes')::text
            FROM public.audit_log AS a WHERE a.id = ANY(@ids)
            """, connection, transaction);
        command.Parameters.AddWithValue("ids", world.Rows.Values.ToArray());
        var labels = world.Rows.ToDictionary(entry => entry.Value, entry => entry.Key);
        var read = new Dictionary<string, Snapshot>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            read[labels[reader.GetGuid(0)]] = new Snapshot(Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5),
                Text(reader, 6), reader.GetString(7));
        }

        read.Keys.Should().BeEquivalentTo(world.Rows.Keys, "every labelled row is read back");
        return read;
    }

    private static async Task<Dictionary<string, Snapshot>> SnapshotAsync(World world)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(world.Database.PlatformConnectionString);
        return await ReadAsync(connection, null, world);
    }

    private static List<string> Changed(World world, Dictionary<string, Snapshot> before, Dictionary<string, Snapshot> after) =>
        [.. world.Rows.Keys.Where(label => after[label] != before[label])];

    private static List<string> ChangedLabels(Dictionary<string, Snapshot> before, Dictionary<string, Snapshot> after) =>
        [.. before.Keys.Where(label => after[label] != before[label])];

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>The operator rows one act wrote, in the order it wrote them.</summary>
    private static async Task<List<OperatorRow>> RowsAsync(World world, string correlation)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(world.Database.PlatformConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT tenant_id, operation, outcome, reason, actor_user_id, after_state::text, metadata::text
            FROM public.audit_log WHERE correlation_id = @c ORDER BY "timestamp", id
            """, connection);
        command.Parameters.AddWithValue("c", correlation);
        var rows = new List<OperatorRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OperatorRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), Text(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), Text(reader, 5), reader.GetString(6)));
        }

        return rows;
    }

    private static async Task<BatchRun> BatchRunAsync(World world, AuditRedactionExecutor executor)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var statements = new StatementLog();
        await using var source = new NpgsqlDataSourceBuilder(world.Database.PlatformConnectionString)
            .UseLoggerFactory(LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(statements)))
            .Build();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        statements.Clear();
        var report = await executor.RedactAsync(world.SentinelPass, Spent(PlatformOperations.ClosureFinalize), connection, transaction,
            deadline.Token);
        var boundaries = statements.Completed("count(*) OVER batch");
        var after = await ReadAsync(connection, transaction, world);
        await transaction.RollbackAsync();
        return new BatchRun(report, after, boundaries);
    }

    private static PlatformAdminScope Scope(NpgsqlDataSource platform) => new(
        new PrivilegedDataSource(() => platform), new AuditCatalog([new TenancyAuditCatalogSource()]), AuditServices(Executor()),
        new SystemClock(), new SystemGuidFactory(), NullLogger<PlatformAdminScope>.Instance);

    /// <summary>The audit store and the real executor, as the scope resolves them per call.</summary>
    private static IServiceScopeFactory AuditServices(IAuditRedactor redactor) =>
        new ServiceCollection()
            .AddSingleton(redactor)
            .AddMetrics()
            .AddLogging()
            .AddScoped<IAuditStateCapture, AuditStateCapture>()
            .AddSingleton<IAuditHealth, AuditHealth>()
            .AddSingleton<IAuditCatalog>(new AuditCatalog([new TenancyAuditCatalogSource()]))
            .AddSingleton<ITenantContextAccessor>(StaticTenantContextAccessor.Unresolved)
            .AddScoped<IGlobalIdentityEnrollment, GlobalIdentityEnrollment>()
            .AddSingleton(new Lazy<NpgsqlDataSource>(
                () => throw new InvalidOperationException("The scope's rows are written on its own connection.")))
            .AddScoped<IAuditStore, PostgresAuditStore>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    private static PlatformAdmission Admission(string? ticket) => TestPlatformAdmissions.Admission(
        PlatformOperations.ClosureFinalize, ticket, correlationId: "00-closure-" + Guid.NewGuid().ToString("N"));

    private static AuditRedactionExecutor Executor() => new(Locators());

    private static IReadOnlyList<IUserReferenceLocator> Locators()
    {
        var services = new ServiceCollection();
        services.AddIdentityContextualFoundation();
        services.AddDataExportCustody(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        return [.. provider.GetServices<IUserReferenceLocator>()];
    }

    private static PlatformAdmission Spent(PlatformOperation operation)
    {
        var admission = TestPlatformAdmissions.Admission(operation);
        admission.TryConsume().Should().BeTrue("the scope spends the admission on entry");
        return admission;
    }

    private static long[] Columns(AuditRedactionReport report) =>
        [.. Enum.GetValues<AuditRedactedColumn>().Select(column => report.Redacted[column])];

    private static Dictionary<RetainedReferenceKind, long> Kinds(AuditRedactionReport report) =>
        report.Retained.ToDictionary(entry => entry.Kind, entry => entry.Count);

    /// <summary>One disposable database and what was written into it, by label.</summary>
    private sealed record World(DisposableSchemaDatabase Database, Guid Person, IdentitySubjectKey Key, Guid ParticipantA,
        Guid ParticipantB, Guid MembershipA, Guid MembershipB) : IAsyncDisposable
    {
        public Dictionary<string, Guid> Rows { get; } = [];

        public AuditRedactionPlan SentinelPass => AuditRedactionPlan.ForClosureSentinel(UserId.From(Person), Key);

        public AuditRedactionPlan TenantPass(Guid tenant) => AuditRedactionPlan.ForClosureTenant(TenantId.From(tenant), UserId.From(Person),
            [tenant == ParticipantA ? MembershipA : MembershipB]);

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed record Snapshot(string? IpAddress, string? UserAgent, string? ActorEmail, string? Before, string? After, string? Changes,
        string Retained);

    private sealed record OperatorRow(Guid Tenant, string Operation, string Outcome, string? Reason, Guid? Actor, string? After, string Metadata);

    private sealed record BatchRun(AuditRedactionReport Report, Dictionary<string, Snapshot> After, int Boundaries);

    /// <summary>The commands one data source's connections completed, as the driver logs them.</summary>
    private sealed class StatementLog : ILoggerProvider, ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _commands = new();

        public ILogger CreateLogger(string categoryName) => this;

        public void Clear() => _commands.Clear();

        public int Completed(string marker) => _commands.Count(command =>
            command.StartsWith("Command execution completed", StringComparison.Ordinal) && command.Contains(marker, StringComparison.Ordinal));

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _commands.Enqueue(formatter(state, exception));

        public void Dispose()
        {
        }
    }
}
