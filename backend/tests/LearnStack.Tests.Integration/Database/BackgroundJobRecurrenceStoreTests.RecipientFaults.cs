using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Theory]
    [InlineData("tenant")]
    [InlineData("organization")]
    [InlineData("token")]
    [InlineData("hash")]
    [InlineData("target")]
    [InlineData("occurrence")]
    [InlineData("due")]
    public async Task Corrupted_recipient_routing_cannot_reach_child_identity_creation(string mutation)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        var claim = await ClaimRecipientAsync(source, manifest);
        if (mutation == "tenant")
        {
            await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
            await SeedLifecycleTenantAsync(app, SchemaFixture.TenantB, SchemaFixture.OrgB1, "Active", false, true);
        }
        var (property, value) = mutation switch
        {
            "tenant" => (nameof(claim.TenantId), (object)TenantId.From(SchemaFixture.TenantB)),
            "organization" => (nameof(claim.OrganizationId), OrganizationId.From(SchemaFixture.OrgA2)),
            "token" => (nameof(claim.Token), Guid.NewGuid()),
            "hash" => (nameof(claim.ManifestHash), new string('0', 64)),
            "target" => (nameof(claim.DefinitionKey), "jobsrecurrencefixture.run.v2"),
            "occurrence" => (nameof(claim.OccurrenceId), BackgroundJobOccurrenceId.From(Guid.CreateVersion7())),
            _ => (nameof(claim.DueAt), claim.DueAt.AddTicks(-TimeSpan.TicksPerMicrosecond)),
        };
        // Deliberately bypass the opaque witness's private constructor only in
        // the proof: the live app/database fences must still reject bad facts.
        var field = typeof(BackgroundJobRecipientClaim).GetField("<" + property + ">k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.Should().NotBeNull();
        field.SetValue(claim, value);
        await using var evidence = await RecipientEvidenceSourceAsync(database);
        var ids = new RecipientIdentityCounter();
        await using var host = RecipientHost(database, new(), configure: services =>
        {
            services.RemoveAll<IGuidFactory>();
            services.AddSingleton<IGuidFactory>(ids);
            services.Replace(ServiceDescriptor.Singleton<IAuthorizationEvidenceSessionFactory>(evidence));
        });
        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
            BackgroundJobLifecycleProjectionDeclaration.Create(), RecipientGate(), CancellationToken.None);
        result.Status.Should().Be(mutation == "organization" ? JobRecipientSubmissionStatus.Failed : JobRecipientSubmissionStatus.LostClaim);
        result.Command!.AcceptedChild.Should().BeNull();
        result.Command.ProvisionalChild.Should().BeNull();
        result.CleanupFailed.Should().BeFalse();
        result.SourceOwner.CleanupConfirmed.Should().BeTrue();
        if (mutation == "organization")
            ids.Issued.Should().BeEmpty("the invalid announced organization fails before an attributable refusal");
        else
        {
            var denialId = ids.Issued.Should().ContainSingle("the sole minted identity must belong to the durable central denial, never a child").Subject;
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var readScope = await app.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(app, readScope);
            await EducationSchemaSeed.AnnounceAsync(app, readScope, claim.TenantId.Value, claim.OrganizationId?.Value);
            await using var denial = new NpgsqlCommand("""
                SELECT id,operation,operation_class,operation_type,outcome,actor_user_id,
                    metadata->'authorization'->>'source',entity_id
                FROM audit_log WHERE id=@id
                """, app, readScope);
            denial.Parameters.AddWithValue("id", denialId);
            await using var rows = await denial.ExecuteReaderAsync();
            (await rows.ReadAsync()).Should().BeTrue("a separate app-role connection must see the committed refusal");
            rows.GetGuid(0).Should().Be(denialId);
            rows.GetString(1).Should().Be("identity.authorization.deny");
            rows.GetString(2).Should().Be("Must");
            rows.GetString(3).Should().Be("SecurityEvent");
            rows.GetString(4).Should().Be("denied");
            rows.GetGuid(5).Should().Be(UserId.SystemActor.Value);
            rows.GetString(6).Should().Be("job");
            rows.IsDBNull(7).Should().BeTrue();
            (await rows.ReadAsync()).Should().BeFalse();
        }
        ids.Count.Should().Be(ids.Issued.Count, "all identifier allocations are accounted for");
        host.Services.GetRequiredService<IAuditHealth>().IsHealthy.Should().BeTrue();
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(0);
        (await ReadAsync<bool>(dispatcher, "SELECT accepted_job_id IS NULL AND skipped_at IS NULL FROM background_job_recipients"))
            .Should().BeTrue();
    }

    private static async Task<AuthorizationEvidenceSource> RecipientEvidenceSourceAsync(DisposableSchemaDatabase database)
    {
        var authority = IdentityMembershipFixture.Authority;
        await using (var migration = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        await using (var install = new NpgsqlCommand("INSERT INTO identity_authority(id,authority_id,issuer) VALUES(1,@id,@issuer)", migration))
        {
            install.Parameters.AddWithValue("id", authority.AuthorityId.Value);
            install.Parameters.AddWithValue("issuer", authority.Issuer);
            await install.ExecuteNonQueryAsync();
        }
        var evidence = new AuthorizationEvidenceSource(database.AppConnectionString);
        try
        {
            await using var business = ApplicationDataSource.Build(database.AppConnectionString);
            await using var proof = await DatabaseQualificationProof.OpenAsync(business);
            await evidence.QualifyAsync(authority.AuthorityId, authority.Issuer, proof, proof.DeadlineToken);
            return evidence;
        }
        catch { await evidence.DisposeAsync(); throw; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recipient_identity_factory_callback_is_revalidated_before_the_actual_INSERT(bool corrupt)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        var claim = await ClaimRecipientAsync(source, manifest);
        var ids = new RecipientIdentityCounter();
        await using var migration = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaQueries.ExecuteAsync(migration, null, """
            CREATE FUNCTION public.fn_recipient_insert_notice() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE NOTICE 'recipient_insert_proof'; RETURN NEW; END;
            $$;
            CREATE TRIGGER zz_recipient_insert_notice AFTER INSERT ON background_jobs
                FOR EACH ROW EXECUTE FUNCTION public.fn_recipient_insert_notice();
            """);
        try
        {
            var observation = new RecipientInsertNotices();
            await using var host = RecipientHost(database, new(), configure: services =>
            {
                services.RemoveAll<IGuidFactory>();
                services.AddSingleton<IGuidFactory>(ids);
                services.AddSingleton(observation);
                services.AddScoped<SubmitBackgroundJobRecipientCommandHandler>();
                services.RemoveAll<IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>>();
                services.AddScoped<IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>, InsertObservingHandler>();
            });
            var accessor = host.Services.GetRequiredService<ITenantContextAccessor>();
            var previous = accessor.Current;
            if (corrupt) ids.BeforeMint = () => accessor.Current = new BackgroundJobFixtureContext();
            var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
                BackgroundJobLifecycleProjectionDeclaration.Create(), RecipientGate(), CancellationToken.None);
            ids.Count.Should().Be(1, "the callback boundary follows actual successful route validation");
            observation.Notices.Should().Be(corrupt ? 0 : 1,
                "the actual INSERT must be refused before SQL; a later rollback cannot erase an already received server NOTICE");
            result.Status.Should().Be(corrupt ? JobRecipientSubmissionStatus.Failed : JobRecipientSubmissionStatus.Accepted);
            result.Command!.Gate.KnownRollback.Should().Be(corrupt);
            result.Command.ProvisionalChild.HasValue.Should().Be(!corrupt);
            result.SourceOwner.CleanupConfirmed.Should().BeTrue();
            accessor.Current.Should().BeSameAs(previous);
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(corrupt ? 0 : 1);
        }
        finally
        {
            await SchemaQueries.ExecuteAsync(migration, null, """
                DROP TRIGGER zz_recipient_insert_notice ON background_jobs;
                DROP FUNCTION public.fn_recipient_insert_notice();
                """);
        }
    }

    [Fact]
    public async Task Recipient_admission_samples_database_time_after_the_actual_row_lock_wait()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        var claim = await ClaimRecipientAsync(source, manifest);
        await using var locker = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var transaction = await locker.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(locker, transaction, "SELECT id FROM background_job_recipients FOR UPDATE");
        await using var host = RecipientHost(database, new());
        var running = host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
            BackgroundJobLifecycleProjectionDeclaration.Create(), RecipientGate(), CancellationToken.None);
        var released = false;
        try
        {
            // The observed waiting query precedes the expiry write. No guessed
            // sleep or elapsed-time assertion establishes this interleaving.
            var blockedAt = await WaitForRecipientLockAsync(database);
            await using var expire = new NpgsqlCommand(
                "UPDATE background_job_recipients SET ownership_until=clock_timestamp() RETURNING ownership_until",
                (NpgsqlConnection)locker, (NpgsqlTransaction)transaction);
            var expiredAt = new DateTimeOffset((DateTime)(await expire.ExecuteScalarAsync())!);
            expiredAt.Should().BeAfter(blockedAt);
            await transaction.CommitAsync();
            released = true;
            var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
            result.Status.Should().Be(JobRecipientSubmissionStatus.LostClaim);
            result.Command!.ProvisionalChild.Should().BeNull();
            result.Command.Gate.KnownRollback.Should().BeTrue();
            result.CleanupFailed.Should().BeFalse();
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(0);
        }
        finally
        {
            if (!released) await transaction.RollbackAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Stop_after_the_actual_recipient_receipt_UPDATE_rolls_back_the_child_and_receipt_before_COMMIT()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        var claim = await ClaimRecipientAsync(source, manifest);
        await using var migration = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaQueries.ExecuteAsync(migration, null, """
            CREATE FUNCTION public.fn_recipient_receipt_notice() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE NOTICE 'recipient_receipt_proof'; RETURN NEW; END;
            $$;
            CREATE TRIGGER zz_recipient_receipt_notice AFTER UPDATE OF accepted_job_id ON background_job_recipients
                FOR EACH ROW WHEN (OLD.accepted_job_id IS NULL AND NEW.accepted_job_id IS NOT NULL)
                EXECUTE FUNCTION public.fn_recipient_receipt_notice();
            """);
        try
        {
            var observation = new RecipientReceiptStop();
            await using var host = RecipientHost(database, new(), configure: services =>
            {
                services.AddSingleton(observation);
                services.AddScoped<SubmitBackgroundJobRecipientCommandHandler>();
                services.RemoveAll<IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>>();
                services.AddScoped<IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>, ReceiptStoppingHandler>();
            });
            var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
                BackgroundJobLifecycleProjectionDeclaration.Create(), RecipientGate(), CancellationToken.None);
            observation.Notices.Should().Be(1, "the real server-side receipt UPDATE must reach the client before stop closes its gate");
            result.Status.Should().Be(JobRecipientSubmissionStatus.Canceled);
            result.Command!.ProvisionalChild.Should().NotBeNull();
            result.Command.Gate.CommitStarted.Should().BeFalse();
            result.Command.Gate.CommitAcknowledged.Should().BeFalse();
            result.Command.Gate.KnownRollback.Should().BeTrue();
            result.CleanupFailed.Should().BeFalse();
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(0);
            (await ReadAsync<bool>(dispatcher, "SELECT accepted_job_id IS NULL AND accepted_at IS NULL FROM background_job_recipients"))
                .Should().BeTrue();
        }
        finally
        {
            await SchemaQueries.ExecuteAsync(migration, null, """
                DROP TRIGGER zz_recipient_receipt_notice ON background_job_recipients;
                DROP FUNCTION public.fn_recipient_receipt_notice();
                """);
        }
    }

    private static async Task<DateTimeOffset> WaitForRecipientLockAsync(DisposableSchemaDatabase database)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var observer = await PostgresFixture.OpenAsync(database.AppConnectionString);
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT query_start FROM pg_stat_activity WHERE datname=current_database()
                    AND usename=current_user AND wait_event_type='Lock'
                    AND query LIKE 'SELECT id FROM background_job_recipients%'
                """, (NpgsqlConnection)observer) { CommandTimeout = 2 };
            if (await command.ExecuteScalarAsync(deadline.Token) is DateTime started) return new(started);
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class RecipientReceiptStop { internal int Notices; }
    private sealed class RecipientInsertNotices { internal int Notices; }
    private sealed class InsertObservingHandler(SubmitBackgroundJobRecipientCommandHandler actual,
        IUnitOfWork unit, RecipientInsertNotices observation) : IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>
    {
        public Task<Result<BackgroundJobId>> Handle(SubmitBackgroundJobRecipientCommand request, CancellationToken cancellationToken)
        {
            ((NpgsqlConnection)unit.Connection).Notice += (_, args) =>
            {
                if (args.Notice.MessageText == "recipient_insert_proof") Interlocked.Increment(ref observation.Notices);
            };
            return actual.Handle(request, cancellationToken);
        }
    }

    private sealed class ReceiptStoppingHandler(SubmitBackgroundJobRecipientCommandHandler actual,
        IUnitOfWork unit, RecipientReceiptStop observation) : IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>
    {
        public Task<Result<BackgroundJobId>> Handle(SubmitBackgroundJobRecipientCommand request, CancellationToken cancellationToken)
        {
            ((NpgsqlConnection)unit.Connection).Notice += (_, args) =>
            {
                if (args.Notice.MessageText != "recipient_receipt_proof") return;
                Interlocked.Increment(ref observation.Notices);
                request.Gate.Close();
            };
            return actual.Handle(request, cancellationToken);
        }
    }
}
