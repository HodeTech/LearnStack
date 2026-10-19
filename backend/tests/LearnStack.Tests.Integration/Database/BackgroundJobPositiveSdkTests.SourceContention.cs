using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    private static readonly string[] SourceContentionRequestNames = ["Submit", "TenantRoot"];

    [Fact]
    public async Task Native_transactional_pool_completes_at_the_app_source_floor_while_actual_external_reconcile_is_held()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        await provider.SetModeAsync(resource, "running");
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        var unrelatedOrganization = OrganizationId.From(Guid.CreateVersion7());
        var reconcile = new BackgroundJobPositiveReconcileProbe("host_stop");
        var transactional = BackgroundJobRuntimeFixture.Shared;
        var registry = SourceContentionRegistry(transactional);
        var releaseAuxiliary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<SourceContentionOwner>(TaskCreationOptions.RunContinuationsAsynchronously);
        BackgroundJobId? transactionalJob = null;
        var probe = new BackgroundJobFixtureProbe
        {
            Body = async (request, services, token) =>
            {
                if (request == "Submit")
                {
                    transactionalJob = await transactional.EnqueueAsync(services);
                    return Result<None>.Ok(None.Value);
                }
                if (request != "TenantRoot") throw new InvalidOperationException("Unexpected contention fixture root.");
                var owner = JobApplicationSourceOwner.Current
                    ?? throw new InvalidOperationException("The native transactional root has no actual application owner.");
                var unit = services.GetRequiredService<IUnitOfWork>();
                await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction);
                var main = (NpgsqlConnection)unit.Connection;
                var auxiliary = owner.BeginStandaloneAudit();
                var opened = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var actual = HoldSourceContentionAuxiliaryAsync(
                    services.GetRequiredService<ApplicationDataSourceLifetime>().Source.Value,
                    auxiliary, releaseAuxiliary.Task, opened, token);
                auxiliary.HoldActualTask(actual);
                var backend = await opened.Task.WaitAsync(token);
                entered.TrySetResult(new(owner, main.ProcessID, backend, actual));
                await actual;
                owner.RequireBusinessReady();
                var written = await services.GetRequiredService<ISender>().Send(new CreateOrganizationCommand(
                    unrelatedOrganization, "contention-" + unrelatedOrganization.Value.ToString("N"),
                    "Independent transactional pool"), token);
                return written.IsFailure ? Result<None>.Fail(written.Error) : Result<None>.Ok(None.Value);
            },
        };
        var bounds = new BackgroundJobRuntimeOptions
        {
            ProbeTimeout = TimeSpan.FromSeconds(30),
            RecoveryProbeInterval = TimeSpan.FromSeconds(5),
            HandoffRepairGrace = TimeSpan.FromSeconds(15),
        };
        await using var host = Fixture.Host(database, provider.Settings, configure: services =>
        {
            services.RemoveAll<BackgroundJobDefinitionRegistry>();
            services.AddSingleton(registry);
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton<IBackgroundJobFixtureProbe>(probe);
            services.AddSingleton<IAuditCatalogSource>(new SourceContentionAuditSource(transactional));
            foreach (var name in SourceContentionRequestNames)
            {
                var request = transactional.RequestType(name);
                foreach (var handler in request.Assembly.GetTypes())
                    foreach (var contract in handler.GetInterfaces().Where(candidate => candidate.IsGenericType
                        && candidate.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
                        && candidate.GenericTypeArguments[0] == request))
                        services.AddTransient(contract, handler);
            }
        }, applicationPoolSize: 32, reconcileProbe: reconcile);
        var lifetime = host.Services.GetRequiredService<ApplicationDataSourceLifetime>();
        var privilegedConnection = new NpgsqlConnectionStringBuilder(database.OutboxConnectionString)
        { Pooling = true, MaxPoolSize = 52 }.ConnectionString;
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(privilegedConnection));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var gate = new BackgroundJobRuntimeGate(registry.Infrastructure);
        var ids = new SystemGuidFactory();
        var runtime = new BackgroundJobRuntime(source, registry, BackgroundJobRecurrenceRegistry.CreateEmpty(),
            host.Services.GetRequiredService<JobExecutor>(), new(source, registry, bounds, ids, gate),
            gate, bounds, ids, TimeProvider.System, new(true, 52), new HangfireBoundedLogProvider(), lifetime);
        // Neither messaging worker starts in the existing construction-only host.
        runtime.BindMessagingLifetime(Task.CompletedTask, Task.CompletedTask);
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(resource, prepared, finalized));
        submitted.IsSuccess.Should().BeTrue();
        var externalJob = submitted.Value;
        var pressure = new List<NpgsqlConnection>();
        SourceContentionOwner? transactionalOwner = null;
        Task? heldProvider = null;
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StartAsync(startup.Token);
            lifetime.PoolSettings.Should().Be(new BackgroundJobApplicationPoolSettings(true, 32));
            runtime.Snapshot.ApplicationCapacity!.RequiredConnections.Should().Be(32);
            await using (var physical = await source.OpenConnectionAsync(startup.Token))
                new NpgsqlConnectionStringBuilder(physical.ConnectionString).MaxPoolSize.Should().Be(52);
            // Execute and the provider journal commit are real. The native recovery
            // then enters the SDK's post-call barrier, retaining its actual task.
            await reconcile.Entered.WaitAsync(startup.Token);
            var external = SourceContentionExecution(runtime, "external", externalJob);
            external.Identity!.IsRecovery.Should().BeTrue();
            heldProvider = (Task)NativeSdkField(external, "_externalProvider");
            heldProvider.IsCompleted.Should().BeFalse();
            ((JobApplicationSourceOwner)NativeSdkField(external, "_recoverySource")).CleanupConfirmed.Should().BeTrue(
                "the actual provider/reconcile task does not keep its preceding app-role read open");
            var context = reconcile.Context!;
            context.JobId.Should().Be(externalJob);
            (await provider.CountsAsync(context.OperationId)).Should().Be((1L, 1L, 0L));

            // These are 30 explicit physical pressure borrowers on the SAME app
            // source, not 30 claimed native invocations or maximum-topology proof.
            using var pressureDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            for (var index = 0; index < 30; index++)
            {
                var connection = await lifetime.Source.Value.OpenConnectionAsync(pressureDeadline.Token);
                pressure.Add(connection);
                await SchemaQueries.AssertApplicationRoleAsync(connection);
            }
            await using (var submission = host.Services.CreateAsyncScope())
                (await transactional.SendAsync(submission.ServiceProvider)).IsSuccess.Should().BeTrue();
            transactionalJob.Should().NotBeNull();
            var acceptedTransactionalJob = transactionalJob!.Value;
            acceptedTransactionalJob.IsInitialized().Should().BeTrue();
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            transactionalOwner = await entered.Task.WaitAsync(completion.Token);
            var actualTransactional = SourceContentionExecution(runtime, "transactional", acceptedTransactionalJob);
            actualTransactional.Identity!.Profile.Should().Be("transactional");
            var admission = (Task<JobStartExecutionResult>)NativeSdkField(actualTransactional, "_admission");
            (await admission.WaitAsync(completion.Token)).SourceOwner.Should().BeSameAs(transactionalOwner.Owner);
            transactionalOwner.ActualAuxiliary.IsCompleted.Should().BeFalse();
            var allBackends = pressure.Select(connection => connection.ProcessID)
                .Append(transactionalOwner.MainBackend).Append(transactionalOwner.AuxiliaryBackend).ToArray();
            allBackends.Should().HaveCount(32).And.OnlyHaveUniqueItems(
                "the native root owns the final real main/auxiliary pair in the configured app pool");
            using (var overflowDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            {
                Func<Task> overflow = async () =>
                {
                    await using var unexpected = await lifetime.Source.Value.OpenConnectionAsync(overflowDeadline.Token);
                };
                await overflow.Should().ThrowAsync<OperationCanceledException>();
            }
            heldProvider.IsCompleted.Should().BeFalse("the external operation stays held through actual source saturation");
            releaseAuxiliary.TrySetResult();
            while ((await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, acceptedTransactionalJob))!.Phase != "completed")
                await Task.Delay(20, completion.Token);
            await AssertSourceContentionCommittedAsync(database, acceptedTransactionalJob, unrelatedOrganization);
            heldProvider.IsCompleted.Should().BeFalse(
                "the unrelated transactional native worker commits before the held external task is released");
            (await provider.CountsAsync(context.OperationId)).Should().Be((1L, 1L, 0L));
            var held = await NativeUnresolvedFactsAsync(database, externalJob);
            held.Unreleased.Should().Be(1);
            held.Normal.Should().Be(1);
            held.Finalize.Should().Be(0);
            held.Outcomes.Should().Be(0);
            held.Audits.Should().Be(0);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(0);
        }
        finally
        {
            releaseAuxiliary.TrySetResult();
            foreach (var connection in pressure) await connection.DisposeAsync();
            runtime.RequestStop();
            reconcile.Release();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
        }
        heldProvider!.IsCompleted.Should().BeTrue();
        transactionalOwner!.ActualAuxiliary.IsCompletedSuccessfully.Should().BeTrue();
        transactionalOwner.Owner.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.RetainedInvocations.Should().Be(0);
        runtime.Snapshot.Control!.Executions.Should().Be(0);
    }

    private static BackgroundJobDefinitionRegistry SourceContentionRegistry(BackgroundJobRuntimeFixture transactional)
    {
        var external = Fixture.Registry.Resolve(BackgroundJobPositiveSdkFixture.Key).Definition!;
        var canary = transactional.Registry.Resolve(BackgroundJobRuntimeFixture.ExternalKey).Definition!;
        if (!Fixture.Registry.Infrastructure.TryGetAdapter(external.ProfileRegistration.ExternalPortType!, out var adapter)
            || !transactional.Registry.Infrastructure.TryGetAdapter(canary.ProfileRegistration.ExternalPortType!, out var canaryAdapter))
            throw new InvalidOperationException("The genuine fixture module bindings are missing.");
        BackgroundJobModuleRegistration[] modules =
        [
            new(external.ParameterType.Assembly, Fixture.RequestType("Declarations").Assembly, adapter.ImplementationType.Assembly),
            new(transactional.ParameterType.Assembly, transactional.RequestType("Declarations").Assembly, canaryAdapter.ImplementationType.Assembly),
            new(typeof(CreateOrganizationCommand).Assembly, typeof(LearnStack.Modules.Tenancy.Application.AssemblyMarker).Assembly,
                typeof(LearnStack.Modules.Tenancy.Infrastructure.AssemblyMarker).Assembly),
        ];
        var topology = BackgroundJobInfrastructureRegistry.Create(Fixture.Registry.Infrastructure.Pools.Values,
            Fixture.Registry.Infrastructure.CapacityGroups.Values, [adapter], modules);
        return BackgroundJobDefinitionRegistry.Create(
        [
            new(external.Key, Fixture.RequestType("Declarations"), external.ParameterType, external.Scope,
                external.Readiness, external.Queue, external.ProfileRegistration, external.NestedRequestTypes, external.CapacityGroup),
            new(BackgroundJobRuntimeFixture.TenantKey, transactional.RequestType("Declarations"), transactional.ParameterType,
                BackgroundJobScope.TenantWide, BackgroundJobDefinitionReadiness.Active, "transactional",
                BackgroundJobProfileRegistration.Transactional(transactional.RequestType("TenantRoot")), [typeof(CreateOrganizationCommand)]),
        ], Fixture.Registry.All.Concat(new[]
        {
            new BackgroundJobRequestDescriptor(transactional.RequestType("Submit"), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.TenantWide),
            new BackgroundJobRequestDescriptor(transactional.RequestType("TenantRoot"), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.TenantWide),
        }), modules, infrastructure: topology);
    }

    private static BackgroundJobExecutionControl SourceContentionExecution(BackgroundJobRuntime runtime,
        string poolName, BackgroundJobId job)
    {
        var pools = (IEnumerable<HangfirePoolOwner>)NativeSdkField(runtime, "_pools");
        var generation = (HangfireGeneration)NativeSdkField(pools.Single(pool => pool.Snapshot.Pool == poolName), "_generation");
        var work = (HangfireGenerationWork)NativeSdkField(generation, "_work");
        lock (NativeSdkField(work, "_sync"))
            return ((IEnumerable<HangfireGenerationWork.Invocation>)NativeSdkField(work, "_active"))
                .Select(invocation => typeof(HangfireGenerationWork.Invocation).GetField("_execution", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(invocation)).OfType<BackgroundJobExecutionControl>()
                .Single(execution => execution.Identity?.JobId == job);
    }

    private static async Task HoldSourceContentionAuxiliaryAsync(NpgsqlDataSource source,
        JobApplicationAuxiliaryResources resources, Task release, TaskCompletionSource<int> opened, CancellationToken token)
    {
        try
        {
            var connection = source.CreateConnection();
            resources.HoldConnection(connection);
            await connection.OpenAsync(token);
            var transaction = await connection.BeginTransactionAsync(token);
            resources.HoldTransaction(transaction);
            var command = new NpgsqlCommand("SELECT current_user,rolbypassrls OR rolsuper FROM pg_roles WHERE rolname=current_user",
                connection, transaction)
            { CommandTimeout = 5 };
            resources.HoldCommand(command);
            var reader = await command.ExecuteReaderAsync(token);
            resources.HoldReader(reader);
            (await reader.ReadAsync(token)).Should().BeTrue();
            reader.GetString(0).Should().Be("learnstack_app");
            reader.GetBoolean(1).Should().BeFalse();
            await resources.DisposeReaderAsync();
            opened.TrySetResult(connection.ProcessID);
            await release;
            await transaction.CommitAsync(token);
        }
        finally { await resources.CleanupAsync(); }
    }

    private static async Task AssertSourceContentionCommittedAsync(DisposableSchemaDatabase database,
        BackgroundJobId job, OrganizationId organization)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var inspect = new NpgsqlCommand("""
            SELECT j.phase,j.profile,j.normal_attempts,j.finalize_attempts,j.processed_at IS NOT NULL,
                (SELECT count(*) FROM organizations WHERE id=@organization),
                (SELECT count(*) FROM audit_log WHERE entity_id=@subject AND operation='tenancy.organization.create' AND outcome='success')
            FROM background_jobs j WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        inspect.Parameters.AddWithValue("id", job.Value);
        inspect.Parameters.AddWithValue("organization", organization.Value);
        inspect.Parameters.AddWithValue("subject", organization.Value.ToString("D"));
        await using var row = await inspect.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetString(0).Should().Be("completed");
        row.GetString(1).Should().Be("transactional");
        row.GetInt32(2).Should().Be(1);
        row.GetInt32(3).Should().Be(0);
        row.GetBoolean(4).Should().BeTrue();
        row.GetInt64(5).Should().Be(1);
        row.GetInt64(6).Should().Be(1);
    }

    private sealed record SourceContentionOwner(JobApplicationSourceOwner Owner, int MainBackend,
        int AuxiliaryBackend, Task ActualAuxiliary);

    private sealed class SourceContentionAuditSource(BackgroundJobRuntimeFixture transactional) : IAuditCatalogSource
    {
        public string ModuleName => "jobssubmissionfixture";
        public void Describe(IAuditCatalogBuilder builder)
        {
            foreach (var name in SourceContentionRequestNames)
                typeof(IAuditCatalogBuilder).GetMethod(nameof(IAuditCatalogBuilder.Off))!
                    .MakeGenericMethod(transactional.RequestType(name)).Invoke(builder, null);
        }
    }
}
