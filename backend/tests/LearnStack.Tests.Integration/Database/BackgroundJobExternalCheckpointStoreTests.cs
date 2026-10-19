using System.Data.Common;
using System.Text;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
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

/// <summary>
/// Actual app-role admission and Prepare/Arm/Outcome SQL and pipeline proof. The
/// explicit routing claim below is not a native dispatch or provider execution.
/// The original construction canary's registry and throwing adapter stay intact.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class BackgroundJobExternalCheckpointStoreTests(SchemaFixture schema)
{
    private static BackgroundJobRuntimeFixture Fixture => BackgroundJobRuntimeFixture.Shared;

    [Theory]
    [InlineData(BackgroundJobExternalState.Succeeded, null)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.Permanent)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientRepeatable)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientNotRepeatable)]
    [InlineData(BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.TransientRepeatable)]
    [InlineData(BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.TransientNotRepeatable)]
    public async Task Actual_app_checkpoints_preserve_canonical_bytes_and_release_only_the_recorded_completed_attempt(
        BackgroundJobExternalState state, BackgroundJobExternalFailurePolicy? policy)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            BackgroundJobRuntimeFixture.ExternalKey, capacityGroup: BackgroundJobRuntimeFixture.CapacityGroup, profile: "external");
        var artifact = Guid.NewGuid();
        var diagnostics = new CheckpointDiagnostics();
        await using var host = Fixture.Host(database, new BackgroundJobFixtureProbe(), configure: services =>
        {
            services.AddSingleton(diagnostics);
            services.Insert(0, ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(FixtureDiagnosticsBehavior<,>)));
            var definition = Fixture.Registry.Resolve(BackgroundJobRuntimeFixture.ExternalKey).Definition!;
            services.AddSingleton(new PreparedArtifact(artifact));
            services.AddTransient(typeof(IRequestHandler<,>).MakeGenericType(definition.ProfileRegistration.RootRequestType,
                typeof(Result<>).MakeGenericType(definition.ProfileRegistration.PlanType!)),
                typeof(FixturePrepareHandler<,>).MakeGenericType(definition.ProfileRegistration.RootRequestType,
                    definition.ProfileRegistration.PlanType!));
        });
        var claim = await FixtureClaim.ClaimAsync(database, job);
        var receipt = await AdmitFixtureAsync(host.Services, database, claim);
        var prepared = await host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(receipt,
            receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare));
        prepared.CommitAcknowledged.Should().BeTrue("Prepare should reach its real fence: decision={0}/{1}, failure={2}, sqlstate={3}",
            prepared.Decision.Disposition, prepared.Decision.ErrorCode, diagnostics.FailureType, diagnostics.SqlState);
        prepared.CleanupFailed.Should().BeFalse();
        receipt.SourceOwner.CleanupConfirmed.Should().BeTrue();
        var planJson = "{\"ArtifactId\":\"" + artifact.ToString("D") + "\"}";
        var planType = Fixture.Registry.Resolve(BackgroundJobRuntimeFixture.ExternalKey).Definition!.ProfileRegistration.PlanType!;
        var plan = BackgroundJobPreparedPlan.Capture((IResultBase)typeof(Result<>).MakeGenericType(planType)
            .GetMethod(nameof(Result<None>.Ok))!.Invoke(null, [Activator.CreateInstance(planType, artifact), null])!, planType, job);
        plan.Json.Should().Be(planJson);
        await using (var read = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await read.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(read, transaction);
            await EducationSchemaSeed.AnnounceAsync(read, transaction, SchemaFixture.TenantA, null);
            await using var rowCommand = new NpgsqlCommand("""
                SELECT j.plan,octet_length(j.plan),j.plan_fingerprint,j.operation_id,
                    a.plan_fingerprint,a.operation_id,a.execute_intent_at
                FROM background_jobs j JOIN background_job_attempts a ON a.id=j.current_attempt_id
                WHERE j.id=@job
                """, (NpgsqlConnection)read, (NpgsqlTransaction)transaction);
            rowCommand.Parameters.AddWithValue("job", job.Value);
            await using var row = await rowCommand.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            row.GetString(0).Should().Be(planJson, "the actual Prepare parameter must preserve its exact compact canonical text");
            row.GetInt32(1).Should().Be(Encoding.UTF8.GetByteCount(planJson));
            row.GetString(2).Should().Be(plan.Fingerprint);
            row.GetGuid(3).Should().Be(plan.OperationId);
            row.GetString(4).Should().Be(plan.Fingerprint, "the original Prepare attempt is stamped in the same owner transaction");
            row.GetGuid(5).Should().Be(plan.OperationId);
            row.IsDBNull(6).Should().BeTrue("Prepare never invents ExecuteIntent");
        }
        var armGate = receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm);
        var arm = new RecordBackgroundJobArmCommand(receipt, armGate, plan.Fingerprint, plan.OperationId);
        (await SendCheckpointAsync(host.Services, receipt, arm, armGate)).IsSuccess.Should().BeTrue();
        armGate.CommitAcknowledged.Should().BeTrue();
        var outcomeJson = "{\"ArtifactId\":\"" + Guid.NewGuid().ToString("D") + "\"}";
        var outcomeGate = receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome);
        var outcome = new RecordBackgroundJobOutcomeCommand(receipt, outcomeGate, plan.Fingerprint, plan.OperationId,
            outcomeJson, state, policy, Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced);
        (await SendCheckpointAsync(host.Services, receipt, outcome, outcomeGate)).IsSuccess.Should().BeTrue();
        outcomeGate.CommitAcknowledged.Should().BeTrue();
        await using var outside = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var outsideTransaction = await outside.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(outside, outsideTransaction);
        await EducationSchemaSeed.AnnounceAsync(outside, outsideTransaction, SchemaFixture.TenantA, null);
        await using var persisted = new NpgsqlCommand("""
            SELECT a.outcome,octet_length(a.outcome),a.outcome_kind,a.failure_policy,
                a.quiescence_id,a.quiescent_at IS NOT NULL,r.released_at IS NOT NULL,r.release_reason
            FROM background_job_attempts a JOIN background_job_reservations r ON r.attempt_id=a.id
            WHERE a.id=@attempt
            """, (NpgsqlConnection)outside, (NpgsqlTransaction)outsideTransaction);
        persisted.Parameters.AddWithValue("attempt", receipt.AttemptId.Value);
        await using var stored = await persisted.ExecuteReaderAsync();
        (await stored.ReadAsync()).Should().BeTrue();
        stored.GetString(0).Should().Be(outcomeJson, "the actual production Outcome handler must preserve canonical bytes");
        stored.GetInt32(1).Should().Be(Encoding.UTF8.GetByteCount(outcomeJson));
        stored.GetString(2).Should().Be(state.ToString().ToLowerInvariant());
        if (policy is null) stored.IsDBNull(3).Should().BeTrue();
        else stored.GetString(3).Should().Be(policy switch
        {
            BackgroundJobExternalFailurePolicy.Permanent => "permanent",
            BackgroundJobExternalFailurePolicy.TransientRepeatable => "transient_repeatable",
            BackgroundJobExternalFailurePolicy.TransientNotRepeatable => "transient_not_repeatable",
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        });
        stored.GetGuid(4).Should().Be(outcome.QuiescenceId);
        stored.GetBoolean(5).Should().BeTrue();
        stored.GetBoolean(6).Should().BeTrue();
        stored.GetString(7).Should().Be("quiescent");
    }

    private static async Task<Result<None>> SendCheckpointAsync<TRequest>(IServiceProvider services,
        ConfirmedStartReceipt receipt, TRequest command, BackgroundJobCommitGate gate) where TRequest : IRequest<Result<None>>
    {
        using var binding = receipt.SourceOwner.Enter();
        receipt.SourceOwner.RequireFreshScopeReady();
        var accessor = services.GetRequiredService<ITenantContextAccessor>();
        var previous = accessor.Current;
        accessor.Current = Context(receipt.Envelope);
        try
        {
            await using var scope = services.CreateAsyncScope();
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var admission = services.GetRequiredService<BackgroundJobExecutionAdmission>();
            using var authority = command switch
            {
                RecordBackgroundJobArmCommand arm => admission.EnterExternalCheckpoint(arm, unit),
                RecordBackgroundJobOutcomeCommand outcome => admission.EnterExternalCheckpoint(outcome, unit),
                _ => throw new InvalidOperationException("Unexpected checkpoint fixture request."),
            };
            gate.TryIssue().Should().BeTrue();
            return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command);
        }
        finally { accessor.Current = previous; }
    }

    private static async Task<ConfirmedStartReceipt> AdmitFixtureAsync(IServiceProvider services,
        DisposableSchemaDatabase database, BackgroundJobRoutingClaim claim)
    {
        BackgroundJobEnvelope envelope;
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await using var command = new NpgsqlCommand("SELECT " + BackgroundJobEnvelopeSql.Projection
                + " FROM background_jobs WHERE id=@job", (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
            command.Parameters.AddWithValue("job", claim.JobId.Value);
            await using var row = await command.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            envelope = BackgroundJobEnvelopeSql.Read(row);
        }
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        using var framework = services.GetRequiredService<BackgroundJobExecutionAdmission>().EnterFramework(claim);
        BackgroundJobExecutionAdmission.InstallValidatedEnvelope(claim, envelope);
        var accessor = services.GetRequiredService<ITenantContextAccessor>();
        var previous = accessor.Current;
        accessor.Current = Context(envelope);
        var run = BackgroundJobTestRun.Create();
        var gate = run.OpenBoundary(BackgroundJobCheckpointKind.Admission);
        var request = new AdmitBackgroundJobAttemptCommand(claim, envelope, gate);
        try
        {
            await using var scope = services.CreateAsyncScope();
            gate.TryIssue().Should().BeTrue();
            (await scope.ServiceProvider.GetRequiredService<ISender>().Send(request)).IsSuccess.Should().BeTrue();
        }
        finally { accessor.Current = previous; }
        var receipt = request.Receipt ?? throw new InvalidOperationException("The actual fixture admission did not acknowledge COMMIT.");
        receipt.BindRun(run);
        return receipt;
    }

    private static JobTenantContext Context(BackgroundJobEnvelope envelope) => JobTenantContext.FromValidatedJob(
        envelope.TenantId, envelope.OrganizationId, envelope.JobId, null, null, null, envelope.Traceparent,
        envelope.DefinitionKey[..envelope.DefinitionKey.IndexOf('.', StringComparison.Ordinal)]);

    private sealed record PreparedArtifact(Guid Value);

    private sealed class CheckpointDiagnostics
    {
        internal string? FailureType { get; set; }
        internal string? SqlState { get; set; }
        internal string? ConstraintName { get; set; }
    }

    private sealed class FixtureDiagnosticsBehavior<TRequest, TResponse>(CheckpointDiagnostics diagnostics)
        : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
    {
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            try { return await next(); }
            catch (Exception failure)
            {
                diagnostics.FailureType = failure.GetType().Name;
                diagnostics.SqlState = failure is PostgresException postgres ? postgres.SqlState : null;
                diagnostics.ConstraintName = (failure as PostgresException)?.ConstraintName;
                throw;
            }
        }
    }

    private sealed class FixturePrepareHandler<TRequest, TPlan>(PreparedArtifact artifact) : IRequestHandler<TRequest, Result<TPlan>>
        where TRequest : notnull, IRequest<Result<TPlan>> where TPlan : IBackgroundJobPlan
    {
        public Task<Result<TPlan>> Handle(TRequest request, CancellationToken cancellationToken)
        {
            if (typeof(TPlan).GetConstructor([typeof(Guid)]) is { } constructor)
                return Task.FromResult(Result<TPlan>.Ok((TPlan)constructor.Invoke([artifact.Value])));
            var plan = Activator.CreateInstance<TPlan>();
            foreach (var property in typeof(TPlan).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                property.SetValue(plan, artifact.Value);
            return Task.FromResult(Result<TPlan>.Ok(plan));
        }
    }

    // Exercise the real admission handler, including its envelope, lease,
    // generation and tenant-wide capacity fences. Only its configured deadline
    // varies to make original-attempt expiry observable within a bounded test.
    private static void ConfigureAdmissionWindow(IServiceCollection services, TimeSpan duration)
    {
        services.RemoveAll<BackgroundJobRuntimeOptions>();
        services.AddSingleton(new BackgroundJobRuntimeOptions { ExternalWaitTimeout = duration });
    }

    private sealed class FixtureClaim : BackgroundJobRoutingClaim.MintingSource
    {
        internal static async Task<BackgroundJobRoutingClaim> ClaimAsync(DisposableSchemaDatabase database, BackgroundJobId job)
        {
            await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
            await using var command = new NpgsqlCommand("""
                UPDATE background_jobs SET ownership_token=@token,ownership_kind='routing',
                    ownership_until=clock_timestamp()+interval '4 minutes' WHERE id=@job
                RETURNING id,tenant_id,organization_id,definition_key,queue,wake_generation,ownership_token,
                    ownership_until,original_due_at,profile,phase,clock_timestamp()
                """, (NpgsqlConnection)dispatcher);
            command.Parameters.AddWithValue("job", job.Value);
            command.Parameters.AddWithValue("token", Guid.NewGuid());
            await using var row = await command.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            return MintClaim(row);
        }
    }
}
