using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.DataProtection;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LearnStack.Application.Messaging;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0107 § 2–§ 4 through the actual job runtime and the actual composition root: a seeded export request runs
/// as its registered transactional definition, as <c>learnstack_app</c> under the job's own tenant transaction, to a
/// completed request and an encrypted artifact that decrypts to the expected bundle — and every way it must not.
/// Each case owns a disposable copy of the seeded schema.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class DataExportExecutionTests(SchemaFixture fixture)
{
    private static readonly string[] Participants = ["audit", "customization", "education", "identity", "tenancy"];
    private DisposableSchemaDatabase? _database;

    [Theory]
    [InlineData("tenant")]
    [InlineData("organization")]
    public async Task An_export_runs_to_completed_with_one_decryptable_bundle_and_withdraws_the_earlier_artifact(string scope)
    {
        await using var database = await OpenDatabaseAsync();
        var (organization, membership, earlier) = scope == "tenant"
            ? ((Guid?)null, LearnerMembershipA, TenantWideA.Artifact!.Value)
            : (SchemaFixture.OrgA1, BranchMembershipA1, BranchA1.Artifact!.Value);
        await using var host = Host();
        var request = await FileExportAsync(SchemaFixture.TenantA, organization, PersonA, membership);
        var job = await SubmitAsync(SchemaFixture.TenantA, organization, request);

        var result = await RunAsync(host.Services, job);

        result.CommitAcknowledged.Should().BeTrue("disposition {0}, code {1}", result.Decision.Disposition, result.Decision.ErrorCode);
        var state = await RequestAsync(SchemaFixture.TenantA, organization, request);
        state.Status.Should().Be("completed");
        state.Failure.Should().BeNull();
        JsonDocument.Parse(state.Progress).RootElement.EnumerateObject().Select(entry => entry.Name).Should().BeEquivalentTo(Participants,
            "every registered participant's completion is recorded");
        JsonDocument.Parse(state.Result).RootElement.EnumerateObject().Select(entry => entry.Name).Should()
            .Contain(["request_receipt", "invitation_record", "attribution_reference", "consent_evidence",
                "audit_actor_reference", "audit_entity_reference", "audit_metadata_reference"]);
        // The retained references are counted before the artifact exists; the artifact this run stored is its own
        // request receipt, so the stored count is every receipt row of the subject in the group after the commit.
        JsonDocument.Parse(state.Result).RootElement.GetProperty("request_receipt").GetInt64()
            .Should().Be(await ReceiptRowsAsync(SchemaFixture.TenantA, organization, PersonA));
        var artifacts = await ArtifactsAsync(SchemaFixture.TenantA, organization, PersonA);
        var stored = artifacts.Single(artifact => artifact.Request == request);
        stored.Status.Should().Be("available");
        stored.Chunks.Should().Be(stored.ChunkCount);
        stored.Truncation.Should().Be("{}");
        var withdrawn = artifacts.Single(artifact => artifact.Id == earlier);
        withdrawn.Status.Should().Be("revoked", "a subject holds one available artifact per group");
        withdrawn.Chunks.Should().Be(0);
        (await PurgeJobsAsync(SchemaFixture.TenantA, organization, stored.Id)).Should().ContainSingle()
            .Which.Should().Be(stored.ExpiresAt, "the purge is scheduled at the artifact's expiry");

        using var bundle = await DecryptAsync(host.Services, SchemaFixture.TenantA, organization, stored.Id);
        var text = bundle.RootElement.GetRawText();
        var root = bundle.RootElement;
        root.GetProperty("request").GetProperty("id").GetString().Should().Be(request.ToString("D"));
        root.GetProperty("request").GetProperty("scope").GetString().Should().Be(scope);
        root.GetProperty("sections").EnumerateObject().Select(section => section.Name).Should().Equal(Participants);
        var memberships = root.GetProperty("sections").GetProperty("identity").GetProperty("memberships");
        memberships.GetArrayLength().Should().Be(1, "only the request's group, never another organization of the same person");
        memberships[0].GetProperty("scope").GetString().Should().Be(scope);
        memberships[0].GetProperty("status").GetString().Should().Be("active");
        memberships[0].GetProperty("roles").EnumerateArray().Select(role => role.GetString()).Should().Equal("learner");
        memberships[0].GetProperty("profile").GetProperty("display_name").GetString().Should().Be("profile-canary");
        root.GetProperty("truncated").EnumerateObject().Should().BeEmpty();
        Identifier().Matches(text).Select(match => match.Value).Should().OnlyContain(value => value == request.ToString("D"),
            "the bundle names no internal identifier but its own request");
        text.Should().NotContain("fixture-subject").And.NotContain("verifier").And.NotContain("subject_key");

        (await AuditAsync(SchemaFixture.TenantA, organization, "identity.data_request.fulfill_export", request.ToString("D")))
            .Should().Be(1, "the fulfilment is one MUST row about the request");
        (await AuditAsync(SchemaFixture.TenantA, organization, "identity.data_export.store", stored.Id.ToString("D")))
            .Should().Be(1, "and one about the stored artifact");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cancelled")]
    [InlineData("completed")]
    public async Task A_missing_cancelled_or_completed_request_completes_its_job_as_a_recorded_no_op(string state)
    {
        await using var database = await OpenDatabaseAsync();
        await using var host = Host();
        var request = Guid.CreateVersion7();
        if (state != "missing")
        {
            request = await FileExportAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, PersonA, BranchMembershipA2);
        }

        if (state == "cancelled")
        {
            await using var app = await OpenAppAsync();
            await using var transaction = await app.BeginTransactionAsync();
            await FenceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA2, null);
            await StepAsync(app, transaction, request, "status='cancelled'");
            await transaction.CommitAsync();
        }
        else if (state == "completed")
        {
            (await RunAsync(host.Services, await SubmitAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, request))).CommitAcknowledged.Should().BeTrue();
        }

        var before = await ArtifactsAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, PersonA);

        var result = await RunAsync(host.Services, await SubmitAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, request));

        result.CommitAcknowledged.Should().BeTrue("disposition {0}, code {1}", result.Decision.Disposition, result.Decision.ErrorCode);
        (await ArtifactsAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, PersonA)).Should().BeEquivalentTo(before, "a no-op stores nothing");
        if (state != "missing")
        {
            (await RequestAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, request)).Status.Should().Be(state);
        }

        (await AuditAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, "identity.data_request.fulfill_export", null))
            .Should().Be(state == "completed" ? 2 : 1, "every run, a no-op included, writes its MUST row");
    }

    [Fact]
    public async Task A_failed_attempt_rolls_every_write_back_and_the_retry_stores_exactly_once()
    {
        await using var database = await OpenDatabaseAsync();
        var probe = new FailingParticipant();
        await using var host = Host(services => services.AddSingleton<IUserDataParticipant>(probe));
        var request = await FileExportAsync(SchemaFixture.TenantA, null, PersonA, LearnerMembershipA);

        var failed = await RunAsync(host.Services, await SubmitAsync(SchemaFixture.TenantA, null, request));

        failed.CommitAcknowledged.Should().BeFalse();
        failed.KnownRollback.Should().BeTrue();
        failed.Decision.ErrorCode.Should().Be("unexpected_fault", "a participant's own refusal is not a dependency outage");
        (await RequestAsync(SchemaFixture.TenantA, null, request)).Status.Should().Be("requested", "the attempt's transitions rolled back");
        var untouched = await ArtifactsAsync(SchemaFixture.TenantA, null, PersonA);
        untouched.Should().NotContain(artifact => artifact.Request == request);
        untouched.Single(artifact => artifact.Id == TenantWideA.Artifact!.Value).Status.Should().Be("available",
            "the earlier artifact is withdrawn only together with its successor");

        var retried = await RunAsync(host.Services, await SubmitAsync(SchemaFixture.TenantA, null, request));
        var again = await RunAsync(host.Services, await SubmitAsync(SchemaFixture.TenantA, null, request));

        retried.CommitAcknowledged.Should().BeTrue();
        again.CommitAcknowledged.Should().BeTrue();
        (await RequestAsync(SchemaFixture.TenantA, null, request)).Status.Should().Be("completed");
        var artifacts = await ArtifactsAsync(SchemaFixture.TenantA, null, PersonA);
        artifacts.Count(artifact => artifact.Request == request).Should().Be(1, "a retried or repeated run never stores twice");
        (await PurgeJobsAsync(SchemaFixture.TenantA, null, artifacts.Single(artifact => artifact.Request == request).Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task The_bundle_ceiling_fails_the_request_with_export_too_large_and_stores_nothing()
    {
        await using var database = await OpenDatabaseAsync();
        await using var host = Host(services => services.AddSingleton<IUserDataParticipant>(new BulkParticipant()),
            maxBytes: DataRequestLimits.MinimumBundleBytes);
        var request = await FileExportAsync(SchemaFixture.TenantA, null, PersonA, LearnerMembershipA);

        var result = await RunAsync(host.Services, await SubmitAsync(SchemaFixture.TenantA, null, request));

        result.CommitAcknowledged.Should().BeTrue();
        var state = await RequestAsync(SchemaFixture.TenantA, null, request);
        state.Status.Should().Be("failed");
        state.Failure.Should().Be("export_too_large");
        var artifacts = await ArtifactsAsync(SchemaFixture.TenantA, null, PersonA);
        artifacts.Should().NotContain(artifact => artifact.Request == request);
        artifacts.Single(artifact => artifact.Id == TenantWideA.Artifact!.Value).Status.Should().Be("available");
    }

    [Fact]
    public async Task A_capped_section_is_stated_in_the_bundle_and_kept_on_the_artifact_receipt()
    {
        await using var database = await OpenDatabaseAsync();
        await using var host = Host(services => services.AddSingleton<IUserDataParticipant>(new CappedParticipant()));
        var request = await FileExportAsync(SchemaFixture.TenantB, SchemaFixture.OrgB1, PersonB, BranchMembershipB, existing: BranchB.Request);

        var result = await RunAsync(host.Services, await SubmitAsync(SchemaFixture.TenantB, SchemaFixture.OrgB1, request));

        result.CommitAcknowledged.Should().BeTrue();
        var stored = (await ArtifactsAsync(SchemaFixture.TenantB, SchemaFixture.OrgB1, PersonB)).Single(artifact => artifact.Request == request);
        var receipt = JsonDocument.Parse(stored.Truncation).RootElement.GetProperty("capped.entries");
        var included = receipt.GetProperty("included").GetInt64();
        included.Should().BeGreaterThan(0).And.BeLessThan(CappedParticipant.Total);
        receipt.GetProperty("total").GetInt64().Should().Be(CappedParticipant.Total);
        using var bundle = await DecryptAsync(host.Services, SchemaFixture.TenantB, SchemaFixture.OrgB1, stored.Id);
        bundle.RootElement.GetProperty("sections").GetProperty("capped").GetProperty("entries").GetArrayLength().Should().Be((int)included);
        bundle.RootElement.GetProperty("truncated").GetProperty("capped.entries").GetProperty("included").GetInt64().Should().Be(included);
    }

    [Fact]
    public async Task An_expired_artifact_is_unreadable_before_its_purge_and_the_purge_job_then_removes_every_chunk()
    {
        await using var database = await OpenDatabaseAsync();
        await using var host = Host();
        var request = await FileExportAsync(SchemaFixture.TenantA, null, PersonA, LearnerMembershipA);
        (await RunAsync(host.Services, await SubmitAsync(SchemaFixture.TenantA, null, request))).CommitAcknowledged.Should().BeTrue();
        var stored = (await ArtifactsAsync(SchemaFixture.TenantA, null, PersonA)).Single(artifact => artifact.Request == request);
        await BackdateAsync(SchemaFixture.TenantA, stored.Id);

        await using (var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, null))
        {
            using var cipher = DataExportTestKit.Cipher();
            var store = DataExportArtifactStoreTests.Store(scope, cipher);
            var locator = new DataExportArtifactLocator(stored.Id, new DataRequestScopeGroup(TenantId.From(SchemaFixture.TenantA), null));
            var metadata = (await store.ReadMetadataAsync(locator, CancellationToken.None))!;
            metadata.Status.Should().Be(DataExportArtifactStatus.Available, "the purge has not run");
            metadata.IsReadable.Should().BeFalse("expiry is a time check against the database clock");
            (await store.ReadChunkAsync(locator, 0, CancellationToken.None)).Should().BeNull();
            await scope.Frame.FailAsync();
        }

        var purge = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString, "identity.data_export_purge_tenant.v1",
            tenant: SchemaFixture.TenantA, parameters: BackgroundJobPayloadContract.Serialize(new DataExportPurgeParameters(stored.Id),
                typeof(DataExportPurgeParameters), BackgroundJobPayloadKind.Parameters));
        (await RunAsync(host.Services, purge)).CommitAcknowledged.Should().BeTrue();
        (await RunAsync(host.Services, await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            "identity.data_export_purge_tenant.v1", tenant: SchemaFixture.TenantA, parameters: BackgroundJobPayloadContract.Serialize(
                new DataExportPurgeParameters(stored.Id), typeof(DataExportPurgeParameters), BackgroundJobPayloadKind.Parameters))))
            .CommitAcknowledged.Should().BeTrue("a purge of a purged artifact is a no-op");

        var purged = (await ArtifactsAsync(SchemaFixture.TenantA, null, PersonA)).Single(artifact => artifact.Id == stored.Id);
        purged.Status.Should().Be("purged");
        purged.Chunks.Should().Be(0);
        (await AuditAsync(SchemaFixture.TenantA, null, "identity.data_export.purge", stored.Id.ToString("D"))).Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task A_dead_export_job_reads_as_failed_and_the_reconciler_stores_it_and_frees_the_slot()
    {
        await using var database = await OpenDatabaseAsync();
        var request = await FileExportAsync(SchemaFixture.TenantA, null, PersonA, LearnerMembershipA);
        var job = await SubmitAsync(SchemaFixture.TenantA, null, request);

        await using (var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, null))
        {
            var (reconciler, reader, store) = Reconciler(scope);
            var loaded = (await store.FindLockedAsync(DataSubjectRequestId.From(request), CancellationToken.None))!;
            (await reader.ReadAsync(loaded.Id, loaded.Kind, CancellationToken.None)).Should().Be(DataRequestJobPhase.Pending);
            DataRequestFailureReconciler.EffectiveStatus(loaded, DataRequestJobPhase.Pending).Should().Be(DataRequestStatus.Requested);
            (await reconciler.ReconcileAsync(loaded, CancellationToken.None)).Should().BeFalse("a live job may still finish");
            await scope.Frame.FailAsync();
        }

        await using (var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(dispatcher, null,
                "UPDATE background_jobs SET phase='quarantined', quarantine_reason='unknown_outcome' WHERE id=@id", ("id", job.Value));

        await using (var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, null))
        {
            var (reconciler, reader, store) = Reconciler(scope);
            var loaded = (await store.FindLockedAsync(DataSubjectRequestId.From(request), CancellationToken.None))!;
            var phase = await reader.ReadAsync(loaded.Id, loaded.Kind, CancellationToken.None);
            phase.Should().Be(DataRequestJobPhase.Dead);
            DataRequestFailureReconciler.EffectiveStatus(loaded, phase).Should().Be(DataRequestStatus.Failed,
                "the receipt shows failed without writing");
            (await reconciler.ReconcileAsync(loaded, CancellationToken.None)).Should().BeTrue();
            loaded.Status.Should().Be(DataRequestStatus.Failed);
            loaded.FailureReason.Should().Be(DataRequestFailureReason.ExecutionFailed);
            (await reconciler.ReconcileAsync(loaded, CancellationToken.None)).Should().BeFalse("a terminal request is left alone");
            await scope.Frame.CompleteAsync();
        }

        var state = await RequestAsync(SchemaFixture.TenantA, null, request);
        state.Status.Should().Be("failed");
        state.Failure.Should().Be("execution_failed");
        var next = await FileExportAsync(SchemaFixture.TenantA, null, PersonA, LearnerMembershipA);
        (await RequestAsync(SchemaFixture.TenantA, null, next)).Status.Should().Be("requested", "the stored failure freed the slot");
    }

    private static (DataRequestFailureReconciler Reconciler, DataRequestJobPhaseReader Reader, DataRequestStore Store) Reconciler(
        DataExportTestKit.GroupScope scope)
    {
        var store = new DataRequestStore(scope.Db, scope.Unit, new ProducerExecution(scope.Unit), scope.Context);
        var reader = new DataRequestJobPhaseReader(scope.Unit, scope.Context);
        return (new DataRequestFailureReconciler(store, reader, new LearnStack.Infrastructure.Persistence.IdentityMutationLocks(scope.Unit),
            scope.Context, new SystemClock()), reader, store);
    }

    private async Task<DisposableSchemaDatabase> OpenDatabaseAsync() =>
        _database = await DataExportTestKit.CreateSeededDatabaseAsync(fixture.Postgres);

    private WebApplicationFactory<Program> Host(Action<IServiceCollection>? configure = null, long? maxBytes = null) =>
        new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Default", _database!.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", _database!.OutboxConnectionString);
            if (maxBytes is { } ceiling)
            {
                builder.UseSetting("DataExport:MaxBytes", ceiling.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (configure is not null)
            {
                builder.ConfigureTestServices(configure);
            }
        });

    /// <summary>A self-filed export in its initial state, under the canonical fences; or the seeded one.</summary>
    private async Task<Guid> FileExportAsync(Guid tenant, Guid? organization, Guid subject, Guid membership, Guid? existing = null)
    {
        if (existing is { } seeded)
        {
            return seeded;
        }

        var id = Guid.CreateVersion7();
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await FenceAsync(app, transaction, tenant, organization, subject);
        await InsertRequestAsync(app, transaction, id, tenant, organization, subject, [membership]);
        await transaction.CommitAsync();
        return id;
    }

    private Task<BackgroundJobId> SubmitAsync(Guid tenant, Guid? organization, Guid request) =>
        BackgroundJobRuntimeRows.InsertAsync(_database!.AppConnectionString,
            organization is null ? "identity.data_export_tenant.v1" : "identity.data_export_organization.v1",
            organization: organization, tenant: tenant, parameters: BackgroundJobPayloadContract.Serialize(new DataExportJobParameters(request),
                typeof(DataExportJobParameters), BackgroundJobPayloadKind.Parameters));

    private async Task<JobBusinessExecutionResult> RunAsync(IServiceProvider services, BackgroundJobId job)
    {
        var executor = services.GetRequiredService<JobExecutor>();
        var start = await executor.ExecuteAsync(await DataExportTestKit.RoutingClaim.ClaimAsync(_database!.OutboxConnectionString, job), BackgroundJobTestRun.Create());
        start.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        return await executor.ExecuteBusinessAsync(start.Receipt!, start.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional));
    }

    private sealed record RequestState(string Status, string Progress, string Result, string? Failure);

    private sealed record ArtifactState(Guid Id, Guid Request, string Status, int ChunkCount, long Chunks, string Truncation,
        DateTimeOffset ExpiresAt);

    private async Task<RequestState> RequestAsync(Guid tenant, Guid? organization, Guid request)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        await using var command = new NpgsqlCommand(
            "SELECT status, progress::text, result::text, failure_reason FROM public.data_subject_requests WHERE id=@id", app, transaction);
        command.Parameters.AddWithValue("id", request);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new RequestState(row.GetString(0), row.GetString(1), row.GetString(2), row.IsDBNull(3) ? null : row.GetString(3));
    }

    private async Task<List<ArtifactState>> ArtifactsAsync(Guid tenant, Guid? organization, Guid subject)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        await using var command = new NpgsqlCommand("""
            SELECT a.id, a.request_id, a.status, a.chunk_count,
                (SELECT count(*) FROM public.data_export_chunks AS c WHERE c.tenant_id=a.tenant_id AND c.artifact_id=a.id),
                a.truncation::text, a.expires_at
            FROM public.data_export_artifacts AS a
            WHERE a.subject_user_id=@subject AND a.organization_id IS NOT DISTINCT FROM @organization
            ORDER BY a.id
            """, app, transaction);
        command.Parameters.AddWithValue("subject", subject);
        command.Parameters.AddWithValue("organization", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)organization ?? DBNull.Value);
        var artifacts = new List<ArtifactState>();
        await using var row = await command.ExecuteReaderAsync();
        while (await row.ReadAsync())
        {
            artifacts.Add(new ArtifactState(row.GetGuid(0), row.GetGuid(1), row.GetString(2), row.GetInt32(3), row.GetInt64(4),
                row.GetString(5), new DateTimeOffset(row.GetDateTime(6), TimeSpan.Zero)));
        }

        return artifacts;
    }

    /// <summary>The subject's request, artifact and ticket rows in exactly the group.</summary>
    private async Task<long> ReceiptRowsAsync(Guid tenant, Guid? organization, Guid subject)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        return await ScalarAsync<long>(app, transaction, """
            SELECT (SELECT count(*) FROM public.data_subject_requests
                    WHERE subject_user_id=@subject AND organization_id IS NOT DISTINCT FROM @organization::uuid)
                + (SELECT count(*) FROM public.data_export_artifacts
                    WHERE subject_user_id=@subject AND organization_id IS NOT DISTINCT FROM @organization::uuid)
                + (SELECT count(*) FROM public.data_export_tickets
                    WHERE created_by=@subject AND organization_id IS NOT DISTINCT FROM @organization::uuid)
            """, ("subject", subject), ("organization", (object?)organization ?? DBNull.Value));
    }

    private async Task<List<DateTimeOffset>> PurgeJobsAsync(Guid tenant, Guid? organization, Guid artifact)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        await using var command = new NpgsqlCommand("""
            SELECT original_due_at FROM public.background_jobs
            WHERE definition_key=@key AND profile='transactional' AND parameters=@parameters
              AND organization_id IS NOT DISTINCT FROM @organization
            """, app, transaction);
        command.Parameters.AddWithValue("key", organization is null ? "identity.data_export_purge_tenant.v1" : "identity.data_export_purge_organization.v1");
        command.Parameters.AddWithValue("parameters", BackgroundJobPayloadContract.Serialize(new DataExportPurgeParameters(artifact),
            typeof(DataExportPurgeParameters), BackgroundJobPayloadKind.Parameters));
        command.Parameters.AddWithValue("organization", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)organization ?? DBNull.Value);
        var due = new List<DateTimeOffset>();
        await using var row = await command.ExecuteReaderAsync();
        while (await row.ReadAsync())
        {
            due.Add(new DateTimeOffset(row.GetDateTime(0), TimeSpan.Zero));
        }

        return due;
    }

    private async Task<long> AuditAsync(Guid tenant, Guid? organization, string operation, string? entity)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        return await ScalarAsync<long>(app, transaction, """
            SELECT count(*) FROM public.audit_log
            WHERE operation=@operation AND organization_id IS NOT DISTINCT FROM @organization::uuid
              AND (@entity::text IS NULL OR entity_id=@entity::text)
            """, ("operation", operation), ("organization", (object?)organization ?? DBNull.Value), ("entity", (object?)entity ?? DBNull.Value));
    }

    /// <summary>Reads every chunk as the application role and opens it with the host's own key ring and binding.</summary>
    private async Task<JsonDocument> DecryptAsync(IServiceProvider services, Guid tenant, Guid? organization, Guid artifact)
    {
        var cipher = services.GetRequiredService<DataExportCipher>();
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        await using var command = new NpgsqlCommand("""
            SELECT a.request_id, a.chunk_count, a.key_version, c.chunk_index, c.nonce, c.ciphertext, c.tag
            FROM public.data_export_artifacts AS a JOIN public.data_export_chunks AS c ON c.tenant_id=a.tenant_id AND c.artifact_id=a.id
            WHERE a.id=@id ORDER BY c.chunk_index
            """, app, transaction);
        command.Parameters.AddWithValue("id", artifact);
        var plaintext = new List<byte>();
        await using var row = await command.ExecuteReaderAsync();
        while (await row.ReadAsync())
        {
            plaintext.AddRange(cipher.Open(new DataExportChunkBinding(tenant, row.GetGuid(0), artifact, row.GetInt32(3), row.GetInt32(1)),
                new DataExportSealedChunk(row.GetString(2), (byte[])row[4], (byte[])row[5], (byte[])row[6])));
        }

        return JsonDocument.Parse(plaintext.ToArray());
    }

    /// <summary>Moves an artifact eight days into the past as the owner, with its guard paused and restored.</summary>
    private async Task BackdateAsync(Guid tenant, Guid artifact)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_export_artifacts DISABLE TRIGGER tg_data_export_artifacts_mutation_guard");
        await ScopeAsync(owner, transaction, tenant);
        await ExecuteAsync(owner, transaction, """
            UPDATE public.data_export_artifacts SET created_at=created_at - interval '8 days', expires_at=expires_at - interval '8 days'
            WHERE id=@id
            """, ("id", artifact));
        await ExecuteAsync(owner, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_export_artifacts ENABLE TRIGGER tg_data_export_artifacts_mutation_guard");
        await transaction.CommitAsync();
    }

    private Task<NpgsqlConnection> OpenAppAsync() => DataExportTestKit.OpenAppAsync(_database!.AppConnectionString);

    [GeneratedRegex("[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}")]
    private static partial Regex Identifier();

    /// <summary>Throws on its first export and succeeds afterwards: a transient failure inside the business transaction.</summary>
    private sealed class FailingParticipant : IUserDataParticipant
    {
        private int _calls;

        public string Key => "probe";

        public Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new InvalidOperationException("Synthetic transient export failure.")
                : Task.CompletedTask;

        public Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<RetainedReferenceCount>>([]);
    }

    /// <summary>Writes past any ceiling until refused.</summary>
    private sealed class BulkParticipant : IUserDataParticipant
    {
        public string Key => "bulk";

        public async Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken)
        {
            var record = Encoding.UTF8.GetBytes("\"" + new string('b', 100_000) + "\"");
            while (await writer.AppendAsync("items", record, cancellationToken) == UserDataExportAppend.Appended)
            {
            }
        }

        public Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<RetainedReferenceCount>>([]);
    }

    /// <summary>A small capped section that declares its truncation, as the audit section does at 8 MiB.</summary>
    private sealed class CappedParticipant : IUserDataParticipant
    {
        internal const long Total = 500;

        public string Key => "capped";

        public long? ExportSectionCapBytes => 4096;

        public async Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken)
        {
            long included = 0;
            for (var index = 0; index < Total; index++)
            {
                var record = Encoding.UTF8.GetBytes("{\"n\":" + index + ",\"pad\":\"" + new string('p', 40) + "\"}");
                if (await writer.AppendAsync("entries", record, cancellationToken) != UserDataExportAppend.Appended)
                {
                    writer.DeclareTruncated("entries", included, Total);
                    return;
                }

                included++;
            }
        }

        public Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<RetainedReferenceCount>>([]);
    }
}
