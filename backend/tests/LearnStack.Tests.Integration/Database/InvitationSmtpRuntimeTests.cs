using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Email;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class InvitationSmtpRuntimeTests(IdentityMembershipFixture fixture) : IClassFixture<IdentityMembershipFixture>
{
    private const string KeyRing = "{\"active\":\"k1\",\"keys\":{\"k1\":\"AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=\"}}";

    [Theory]
    [InlineData(InvitationSmtpTransportBehavior.Accept, BackgroundJobExternalState.Succeeded, false)]
    [InlineData(InvitationSmtpTransportBehavior.Accept, BackgroundJobExternalState.Succeeded, true)]
    [InlineData(InvitationSmtpTransportBehavior.DropFinalReply, BackgroundJobExternalState.Unknown, false)]
    [InlineData(InvitationSmtpTransportBehavior.PermanentRecipientRejection, BackgroundJobExternalState.Failed, false)]
    public async Task Actual_runtime_receipt_opens_provider_source_and_preserves_unknown_or_finalizes_durable_submission(
        InvitationSmtpTransportBehavior behavior, BackgroundJobExternalState expected, bool organization)
    {
        await using var database = await OpenDatabaseAsync();
        await using var smtp = new InvitationSmtpTransportServer(behavior);
        await using var host = Host(smtp.Port);
        var facts = await SeedAsync(organization ? SchemaFixture.OrgA1 : null);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var start = await executor.ExecuteAsync(await RoutingClaim.ClaimAsync(_database!.OutboxConnectionString, facts.Job), BackgroundJobTestRun.Create());
        start.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        var original = start.Receipt!;
        var prepare = await executor.ExecuteBusinessAsync(original, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        prepare.CommitAcknowledged.Should().BeTrue("the actual Identity prepare root must commit; disposition {0}, code {1}", prepare.Decision.Disposition, prepare.Decision.ErrorCode);
        var read = await executor.ReadExternalStateAsync(original, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        var arm = await executor.RecordExternalArmAsync(original, read.Handle!, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None);
        arm.CommitAcknowledged.Should().BeTrue();
        var result = await executor.ExecuteExternalAsync(read.Handle!, arm, CancellationToken.None);
        result.Status.Should().Be(JobExternalExecutionStatus.Reported);
        result.ProviderIssued.Should().BeTrue();
        result.CleanupFailed.Should().BeFalse();
        result.Observation.Should().NotBeNull();
        result.Observation!.State.Should().Be(expected);
        smtp.Connections.Should().Be(1);
        smtp.Messages.Should().Be(behavior == InvitationSmtpTransportBehavior.PermanentRecipientRejection ? 0 : 1);
        InvitationSmtpExecutionReceipt.Current.Should().BeNull();
        host.Services.GetRequiredService<InvitationSmtpProviderDataSource>().IsHealthy.Should().BeTrue();
        if (expected != BackgroundJobExternalState.Unknown)
        {
            var recorded = await executor.RecordExternalOutcomeAsync(original, read.Handle!,
                original.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome), result.Observation, CancellationToken.None);
            recorded.CommitAcknowledged.Should().BeTrue();
            var finalStart = await executor.ExecuteAsync(await RoutingClaim.ClaimAsync(_database!.OutboxConnectionString, facts.Job), BackgroundJobTestRun.Create());
            finalStart.Outcome.Should().Be(JobStartExecutionOutcome.Started);
            var final = await executor.ExecuteBusinessAsync(finalStart.Receipt!, finalStart.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Finalize));
            final.CommitAcknowledged.Should().BeTrue("actual Identity finalize root must persist the provider receipt; disposition {0}, code {1}", final.Decision.Disposition, final.Decision.ErrorCode);
        }
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SchemaFixture.TenantA, organization ? SchemaFixture.OrgA1 : null);
        await using var state = new NpgsqlCommand("""
            SELECT i.submission_status,i.submission_receipt_id IS NOT NULL,r.released_at IS NOT NULL
            FROM public.invitations i JOIN public.background_job_reservations r ON r.job_id=i.current_delivery_job_id
            WHERE i.id=@id
            """, app, transaction);
        state.Parameters.AddWithValue("id", facts.Invitation);
        await using var row = await state.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetString(0).Should().Be(expected == BackgroundJobExternalState.Unknown ? "Queued" : expected == BackgroundJobExternalState.Succeeded ? "SubmissionAccepted" : "Failed");
        row.GetBoolean(1).Should().Be(expected != BackgroundJobExternalState.Unknown);
        row.GetBoolean(2).Should().Be(expected != BackgroundJobExternalState.Unknown);
    }

    [Fact]
    public async Task Identifiers_and_direct_adapter_call_never_mint_runtime_authority()
    {
        await using var database = await OpenDatabaseAsync();
        await using var smtp = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.Accept);
        await using var host = Host(smtp.Port);
        await using var scope = host.Services.CreateAsyncScope();
        var operation = scope.ServiceProvider.GetRequiredService<IInvitationEmailOperation>();
        var call = () => operation.ExecuteAsync(new(BackgroundJobId.From(Guid.CreateVersion7()), BackgroundJobAttemptId.From(Guid.CreateVersion7()), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1)),
            new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()));
        await call.Should().ThrowAsync<InvalidOperationException>();
        smtp.Connections.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Lost_claim_or_completion_commit_has_one_locked_exact_verification_without_resend(int commitsToPass)
    {
        await using var database = await OpenDatabaseAsync();
        await using var smtp = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.Accept);
        await using var proxy = new CommitResponseDroppingProxy(_database!.AppConnectionString, commitsToPass: commitsToPass);
        await using var host = Host(smtp.Port, proxy.ConnectionString);
        var facts = await SeedAsync();
        var (executor, original, read, arm) = await PrepareAndArmAsync(host.Services, facts.Job);
        var result = await executor.ExecuteExternalAsync(read, arm, CancellationToken.None);
        result.Status.Should().Be(JobExternalExecutionStatus.Reported);
        result.Observation!.State.Should().Be(BackgroundJobExternalState.Succeeded);
        proxy.DroppedCommitResponses.Should().Be(1);
        proxy.AcceptedConnections.Should().Be(2);
        smtp.Messages.Should().Be(1);
        original.SourceOwner.CleanupConfirmed.Should().BeTrue();
    }

    [Theory]
    [InlineData("unclaimed", BackgroundJobExternalState.Cancelled)]
    [InlineData("lost_reply", BackgroundJobExternalState.Unknown)]
    [InlineData("accepted", BackgroundJobExternalState.Succeeded)]
    public async Task Fresh_recovery_fences_unclaimed_or_replays_exact_original_evidence_without_sending(string state, BackgroundJobExternalState expected)
    {
        await using var database = await OpenDatabaseAsync();
        await using var smtp = new InvitationSmtpTransportServer(state == "lost_reply" ? InvitationSmtpTransportBehavior.DropFinalReply : InvitationSmtpTransportBehavior.Accept);
        await using var host = Host(smtp.Port);
        var facts = await SeedAsync();
        var (executor, original, read, arm) = await PrepareAndArmAsync(host.Services, facts.Job);
        JobExternalExecutionResult? sent = null;
        if (state != "unclaimed") sent = await executor.ExecuteExternalAsync(read, arm, CancellationToken.None);
        original.Run.Close();
        await using (var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.OutboxConnectionString))
            await SchemaQueries.ExecuteAsync(dispatcher, null, """
                UPDATE background_jobs SET ownership_until=clock_timestamp() WHERE id=@id;
                UPDATE background_jobs SET phase='reconciling',ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL,probe_after=clock_timestamp()
                  WHERE id=@id AND ownership_until<=clock_timestamp()
                """, ("id", facts.Job.Value));
        var registry = host.Services.GetRequiredService<BackgroundJobDefinitionRegistry>();
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(_database!.OutboxConnectionString));
        var runtime = new BackgroundJobRuntimeGate(registry.Infrastructure);
        var store = new PostgresBackgroundJobControlStore(source, registry, new(), new SystemGuidFactory(), runtime);
        var row = await BackgroundJobRuntimeRows.ReadAsync(_database!.AppConnectionString, facts.Job);
        row.Should().NotBeNull();
        var acquired = await store.AcquireExternalRecoveryAsync(facts.Job, original.Claim.WakeGeneration + 1, "external", CancellationToken.None);
        acquired.ThrowIfFailed();
        acquired.Value.Should().NotBeNull();
        BackgroundJobExternalRecoveryBinding recovery;
        using (JobApplicationSourceOwner.Create().Enter()) recovery = acquired.Value!.Bind(new BackgroundJobRunGate(runtime.RunStop));
        recovery.Receipt.AttemptId.Should().Be(original.AttemptId);
        recovery.Receipt.AdmissionToken.Should().Be(original.Claim.Token);
        recovery.Receipt.RecoveryToken.Should().NotBe(original.Claim.Token);
        recovery.Receipt.Deadline.Should().Be(original.Deadline);
        var recoveredRead = await executor.ReadExternalStateAsync(recovery, CancellationToken.None);
        recoveredRead.Handle.Should().NotBeNull();
        var recovered = await executor.ReconcileExternalAsync(recovery, recoveredRead.Handle!, CancellationToken.None);
        recovered.Status.Should().Be(JobExternalExecutionStatus.Reported);
        recovered.Observation!.State.Should().Be(expected);
        smtp.Connections.Should().Be(state == "unclaimed" ? 0 : 1);
        if (state == "accepted") recovered.Observation.OutcomeJson.Should().Be(sent!.Observation!.OutcomeJson);
        (await executor.ExecuteExternalAsync(read, arm, CancellationToken.None)).Status.Should().Be(JobExternalExecutionStatus.EntryRefused);
    }

    [Fact]
    public async Task Organization_job_cannot_prepare_a_tenant_wide_invitation_even_when_job_id_matches()
    {
        await using var database = await OpenDatabaseAsync();
        await using var smtp = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.Accept);
        await using var host = Host(smtp.Port);
        var facts = await SeedAsync(SchemaFixture.OrgA1, mismatchedInvitationScope: true);
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var start = await executor.ExecuteAsync(await RoutingClaim.ClaimAsync(_database!.OutboxConnectionString, facts.Job), BackgroundJobTestRun.Create());
        start.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        var prepared = await executor.ExecuteBusinessAsync(start.Receipt!, start.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        prepared.CommitAcknowledged.Should().BeFalse();
        smtp.Connections.Should().Be(0);
    }

    [Fact]
    public async Task Countable_finalizer_update_preserves_a_reissued_current_generation_as_a_tracked_no_op()
    {
        await using var database = await OpenDatabaseAsync();
        await using var smtp = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.Accept);
        await using var host = Host(smtp.Port);
        var facts = await SeedAsync();
        var (executor, original, read, arm) = await PrepareAndArmAsync(host.Services, facts.Job);
        var sent = await executor.ExecuteExternalAsync(read, arm, CancellationToken.None);
        sent.Observation!.State.Should().Be(BackgroundJobExternalState.Succeeded);
        (await executor.RecordExternalOutcomeAsync(original, read, original.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome),
            sent.Observation, CancellationToken.None)).CommitAcknowledged.Should().BeTrue();
        var generation = Guid.CreateVersion7(); var newJob = Guid.CreateVersion7();
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, SchemaFixture.TenantA, null);
            await IdentityMembershipFixture.LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                UPDATE invitations SET generation=generation+1,generation_id=@generation,current_delivery_job_id=@job,
                    verifier_digest=@digest,generation_created_at=clock_timestamp(),expires_at=clock_timestamp()+interval '14 days',
                    submission_status='Queued',submission_receipt_id=NULL,row_version=row_version+1,
                    updated_at=clock_timestamp(),updated_by=@actor WHERE id=@id
                """, ("generation", generation), ("job", newJob), ("digest", InvitationSchemaSeed.Digest(generation)),
                ("actor", IdentityMembershipFixture.AdminA), ("id", facts.Invitation));
            await transaction.CommitAsync();
        }
        var finalStart = await executor.ExecuteAsync(await RoutingClaim.ClaimAsync(_database!.OutboxConnectionString, facts.Job), BackgroundJobTestRun.Create());
        finalStart.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        var final = await executor.ExecuteBusinessAsync(finalStart.Receipt!, finalStart.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Finalize));
        final.CommitAcknowledged.Should().BeTrue(); smtp.Messages.Should().Be(1);
        await using var verify = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.AppConnectionString);
        await using var scope = await verify.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(verify, scope, SchemaFixture.TenantA, null);
        await using var query = new NpgsqlCommand("SELECT row_version,generation_id,current_delivery_job_id,submission_status,submission_receipt_id FROM invitations WHERE id=@id", verify, scope);
        query.Parameters.AddWithValue("id", facts.Invitation);
        await using var row = await query.ExecuteReaderAsync(); (await row.ReadAsync()).Should().BeTrue();
        row.GetInt64(0).Should().Be(1); row.GetGuid(1).Should().Be(generation); row.GetGuid(2).Should().Be(newJob);
        row.GetString(3).Should().Be("Queued"); row.IsDBNull(4).Should().BeTrue();
    }

    private async Task<(JobExecutor Executor, ConfirmedStartReceipt Receipt, JobExternalReadHandle Read, JobExternalCheckpointResult Arm)>
        PrepareAndArmAsync(IServiceProvider services, BackgroundJobId job)
    {
        var executor = services.GetRequiredService<JobExecutor>();
        var started = await executor.ExecuteAsync(await RoutingClaim.ClaimAsync(_database!.OutboxConnectionString, job), BackgroundJobTestRun.Create());
        started.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        var receipt = started.Receipt!;
        (await executor.ExecuteBusinessAsync(receipt, receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare))).CommitAcknowledged.Should().BeTrue();
        var read = await executor.ReadExternalStateAsync(receipt, CancellationToken.None);
        read.Handle.Should().NotBeNull();
        var arm = await executor.RecordExternalArmAsync(receipt, read.Handle!, receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None);
        arm.CommitAcknowledged.Should().BeTrue();
        return (executor, receipt, read.Handle!, arm);
    }

    private DisposableSchemaDatabase? _database;
    private async Task<DisposableSchemaDatabase> OpenDatabaseAsync()
    {
        _database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(_database.AppConnectionString);
        await IdentityMembershipFixture.SeedSchemaAsync(_database.MigrationConnectionString, _database.AppConnectionString);
        return _database;
    }

    private WebApplicationFactory<Program> Host(int port, string? providerConnection = null) => new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Default", _database!.AppConnectionString);
        builder.UseSetting("ConnectionStrings:OutboxDispatcher", _database!.OutboxConnectionString);
        builder.UseSetting("InvitationEmail:KeyRing", KeyRing);
        builder.UseSetting("InvitationEmail:Smtp:Host", "127.0.0.1");
        builder.UseSetting("InvitationEmail:Smtp:Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("InvitationEmail:Smtp:Sender", "sender@example.test");
        builder.UseSetting("InvitationEmail:Smtp:Tls", "DevelopmentPlaintext");
        builder.UseSetting("InvitationEmail:Smtp:Username", "");
        builder.UseSetting("InvitationEmail:Smtp:Password", "");
        if (providerConnection is not null)
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<InvitationSmtpProviderDataSource>();
                services.AddSingleton(new InvitationSmtpProviderDataSource(providerConnection));
            });
    });

    private async Task<(BackgroundJobId Job, Guid Invitation)> SeedAsync(Guid? organization = null, bool mismatchedInvitationScope = false)
    {
        var invitation = Guid.CreateVersion7(); var generation = Guid.CreateVersion7(); var spool = Guid.CreateVersion7();
        var parameters = new InvitationEmailParameters(invitation, generation, spool);
        var job = await BackgroundJobRuntimeRows.InsertAsync(_database!.AppConnectionString, organization is null ? "identity.invitation_email_tenant.v1" : "identity.invitation_email_organization.v1",
            organization: organization, parameters: BackgroundJobPayloadContract.Serialize(parameters, typeof(InvitationEmailParameters), BackgroundJobPayloadKind.Parameters),
            capacityGroup: "identity.invitation_email", profile: "external");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["InvitationEmail:KeyRing"] = KeyRing }).Build();
        var encrypted = new InvitationEmailCipher(new ConfigurationSecretProvider(configuration)).Encrypt(
            new(spool, SchemaFixture.TenantA, organization, invitation, generation, job.Value),
            new("recipient@example.test", "Invitation", "Synthetic invitation", "<p>Synthetic invitation</p>", "fixture@example.test"));
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SchemaFixture.TenantA, mismatchedInvitationScope ? null : organization);
        await IdentityMembershipFixture.LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
        await InvitationSchemaSeed.InsertAsync(app, transaction, invitation, SchemaFixture.TenantA, mismatchedInvitationScope ? null : organization, generation: generation, job: job.Value);
        if (mismatchedInvitationScope) await IdentityMembershipFixture.ScopeAsync(app, transaction, SchemaFixture.TenantA, organization);
        await SchemaQueries.ExecuteAsync(app, transaction, """
            INSERT INTO public.invitation_email_envelopes(id,tenant_id,organization_id,invitation_id,generation_id,job_id,format_version,key_version,nonce,ciphertext,tag)
            VALUES(@spool,@tenant,@organization,@invitation,@generation,@job,1,@key,@nonce,@ciphertext,@tag)
            """, ("spool", spool), ("tenant", SchemaFixture.TenantA), ("organization", (object?)organization ?? DBNull.Value), ("invitation", invitation), ("generation", generation), ("job", job.Value),
            ("key", encrypted.KeyVersion), ("nonce", encrypted.Nonce), ("ciphertext", encrypted.Ciphertext), ("tag", encrypted.Tag));
        await transaction.CommitAsync();
        return (job, invitation);
    }

    private sealed class RoutingClaim : BackgroundJobRoutingClaim.MintingSource
    {
        internal static async Task<BackgroundJobRoutingClaim> ClaimAsync(string connectionString, BackgroundJobId job)
        {
            await using var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
            await using var command = new NpgsqlCommand("""
                UPDATE background_jobs SET ownership_token=@token,ownership_kind='routing',
                    ownership_until=clock_timestamp()+interval '4 minutes' WHERE id=@job
                RETURNING id,tenant_id,organization_id,definition_key,queue,wake_generation,ownership_token,
                    ownership_until,original_due_at,profile,phase,clock_timestamp()
                """, dispatcher);
            command.Parameters.AddWithValue("job", job.Value);
            command.Parameters.AddWithValue("token", Guid.NewGuid());
            await using var row = await command.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            return MintClaim(row);
        }
    }
}
