using System.Reflection;
using System.Runtime.ExceptionServices;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData("host_stop")]
    [InlineData("lease_loss")]
    public async Task Native_stop_returns_the_wrapper_before_a_held_external_constructor_really_cleans_up(string reason)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        var construction = new BackgroundJobPositiveProviderConstructionProbe(hold: true);
        var bounds = new BackgroundJobRuntimeOptions { RenewalInterval = TimeSpan.FromSeconds(1) };
        await using var host = Fixture.Host(database, provider.Settings, construction, services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        });
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var runtime = NativeSdkRuntime(host.Services, source, bounds);
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(Guid.CreateVersion7(), prepared, finalized));
        submitted.IsSuccess.Should().BeTrue();
        var job = submitted.Value;
        HangfireGenerationWork.Invocation? owned = null;
        Task<bool>? wrapper = null;
        Task? actual = null;
        ExceptionDispatchInfo? failure = null;
        var boundary = "start";
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(startup.Token);
            boundary = "handoff";
            await EnsureNativeSdkStartAsync(runtime, source, job, construction.Entered, startup.Token);
            boundary = "constructor";
            await construction.Entered.WaitAsync(startup.Token);
            boundary = "capture";
            owned = NativeSdkInvocation(runtime);
            var framework = (HangfireInvocationContract.BoundInvocation)NativeSdkField(owned, "_framework");
            wrapper = (Task<bool>)NativeSdkField(framework, "_wrapperWait");
            actual = (Task)NativeSdkField(framework, "_operationObserver");
            boundary = "cancel";
            if (reason == "host_stop") runtime.RequestStop();
            else
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var expire = new NpgsqlCommand("""
                    UPDATE background_jobs SET ownership_until=clock_timestamp()-interval '1 microsecond'
                    WHERE id=@id AND ownership_kind='execution' AND phase='executing'
                    """, connection) { CommandTimeout = 5 };
                expire.Parameters.AddWithValue("id", job.Value);
                (await expire.ExecuteNonQueryAsync()).Should().Be(1);
            }
            boundary = "return";
            await owned.ReturnRequested.WaitAsync(TimeSpan.FromSeconds(15));
            boundary = "wrapper";
            (await wrapper.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
            boundary = "held";
            actual.IsCompleted.Should().BeFalse("the actual synchronous constructor is still held, despite the native wrapper's return");
            owned.Token.IsCancellationRequested.Should().BeTrue();
            owned.Token.WaitHandle.Should().NotBeNull();
            runtime.Snapshot.RetainedInvocations.Should().Be(1);
            runtime.Snapshot.Control!.Executions.Should().Be(1);
            runtime.Completion.IsCompleted.Should().BeFalse();
            construction.Calls.Should().Be(1);
            construction.ExternalDenial.Should().BeTrue();
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, prepared.Value)).Should().Be(1);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(0);
        }
        catch (Exception error) when (NativeWaitCatchable(error))
        {
            failure = ExceptionDispatchInfo.Capture(error);
            WriteNativeWaitObservation("failure", boundary, construction, owned, wrapper, actual, runtime);
        }
        finally
        {
            construction.Release();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            boundary = "cleanup_request";
            try
            {
                runtime.RequestStop();
                boundary = "cleanup_stop";
                await runtime.StopAsync(cleanup.Token);
                boundary = "cleanup_completion";
                await runtime.Completion.WaitAsync(cleanup.Token);
                boundary = "cleanup_complete";
            }
            catch (Exception error) when (NativeWaitCatchable(error))
            {
                if (failure is null)
                {
                    failure = ExceptionDispatchInfo.Capture(error);
                    WriteNativeWaitObservation("failure", boundary, construction, owned, wrapper, actual, runtime);
                }
            }
            if (failure is not null)
                WriteNativeWaitObservation("cleanup", boundary, construction, owned, wrapper, actual, runtime);
        }
        failure?.Throw();
        runtime.Snapshot.Control!.Executions.Should().Be(0);
        runtime.Snapshot.RetainedInvocations.Should().Be(0);
        var handle = () => owned!.Token.WaitHandle;
        handle.Should().Throw<ObjectDisposedException>();
        // Cancellation at the final typed method check refuses the provider:
        // a prepared plan and durable Arm are not themselves an external effect.
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await using var inspect = new NpgsqlCommand("""
            SELECT j.operation_id,(SELECT count(*) FROM background_job_reservations r
                WHERE r.job_id=j.id AND r.released_at IS NULL)
            FROM background_jobs j WHERE j.id=@id
            """, (NpgsqlConnection)app, (NpgsqlTransaction)transaction) { CommandTimeout = 5 };
        inspect.Parameters.AddWithValue("id", job.Value);
        await using var row = await inspect.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        (await provider.CountsAsync(row.GetGuid(0))).Should().Be((0L, 0L, 0L));
        row.GetInt64(1).Should().Be(1, "lease/host cancellation does not prove no intent or release occupancy");
    }

    // Pure diagnostic projection: no SQL, provider call, cancellation or completion
    // capability is issued here. A failed cleanup never replaces the first failure.
    private void WriteNativeWaitObservation(string kind, string boundary,
        BackgroundJobPositiveProviderConstructionProbe construction, HangfireGenerationWork.Invocation? owned,
        Task<bool>? wrapper, Task? actual, BackgroundJobRuntime runtime)
    {
        static object? Field(object owner, string name) => owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner);
        static string State(Task? task) => task?.Status switch
        {
            null => "not_issued",
            TaskStatus.RanToCompletion => "succeeded",
            TaskStatus.Canceled => "cancelled",
            TaskStatus.Faulted => "faulted",
            _ => "pending",
        };
        try
        {
            var jobs = (BackgroundJobControl)NativeSdkField(runtime, "_jobs");
            HangfirePoolOwner[] pools;
            lock (NativeSdkField(runtime, "_sync"))
                pools = ((IEnumerable<HangfirePoolOwner>)NativeSdkField(runtime, "_pools")).ToArray();
            var shared = Field(runtime, "_shared") as HangfireSharedComponents;
            var snapshot = runtime.Snapshot;
            var runtimeState = snapshot.State switch
            {
                BackgroundJobRuntimeState.NotStarted => "not_started",
                BackgroundJobRuntimeState.Bootstrapping => "bootstrapping",
                BackgroundJobRuntimeState.Starting => "starting",
                BackgroundJobRuntimeState.Running => "running",
                BackgroundJobRuntimeState.Stopping => "stopping",
                BackgroundJobRuntimeState.Retained => "retained",
                BackgroundJobRuntimeState.Stopped => "stopped",
                _ => "unknown",
            };
            var poolStates = pools.Length == 0 ? "not_issued" : string.Join(',', pools.Select(pool => State(pool.Completion)));
            output.WriteLine("Native wait " + kind + ";boundary=" + boundary
                + ";constructor=" + (construction.Entered.IsCompleted ? "entered" : "pending")
                + ";invocation=" + (owned is null ? "not_issued" : "captured")
                + ";wrapper=" + State(wrapper) + ";actual=" + State(actual)
                + ";runtime=" + runtimeState + ";completion=" + State(runtime.Completion)
                + ";control=" + State(jobs.Completion) + ";pools=" + poolStates
                + ";shared=" + State(shared?.Completion)
                + ";bridgeLoop=" + State(Field(runtime, "_bridgeLoop") as Task)
                + ";bridgeStop=" + State(Field(runtime, "_bridgeStop") as Task) + ".");
        }
        catch (Exception error) when (NativeWaitCatchable(error))
        {
            // Diagnostic failure is not authority to replace the test failure.
            try { output.WriteLine("Native wait " + kind + ";boundary=" + boundary + ";snapshot=unavailable."); }
            catch (Exception outputError) when (NativeWaitCatchable(outputError)) { }
        }
    }

    private static bool NativeWaitCatchable(Exception error) => error.GetType() != typeof(OutOfMemoryException)
        && error.GetType() != typeof(StackOverflowException);

    [Fact]
    public async Task Native_external_constructor_uses_the_long_external_wait_after_short_Prepare_finishes()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        var construction = new BackgroundJobPositiveProviderConstructionProbe(hold: true);
        var bounds = new BackgroundJobRuntimeOptions { TransactionTimeout = TimeSpan.FromSeconds(2) };
        await using var host = Fixture.Host(database, provider.Settings, construction, services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
        });
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        using var globals = new NativeSdkGlobalFilterSnapshot();
        var runtime = NativeSdkRuntime(host.Services, source, bounds);
        var submitted = await Fixture.SubmitAsync(host.Services, Fixture.Parameters(resource, prepared, finalized));
        submitted.IsSuccess.Should().BeTrue();
        var job = submitted.Value;
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await runtime.StartAsync(startup.Token);
            await EnsureNativeSdkStartAsync(runtime, source, job, construction.Entered, startup.Token);
            await construction.Entered.WaitAsync(startup.Token);
            // Deliberately outlive two short-root budgets while the actual
            // external constructor is still blocked. Its independent deadline
            // and real SQL lease remain live; no timing performance is asserted.
            await Task.Delay(bounds.TransactionTimeout + bounds.TransactionTimeout);
            runtime.Snapshot.RetainedInvocations.Should().Be(0, "long external work cannot inherit the short root's wrapper deadline");
            runtime.Snapshot.Control!.Executions.Should().Be(1);
            construction.Calls.Should().Be(1);
            var held = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!;
            held.Phase.Should().Be("executing");
            held.NormalAttempts.Should().Be(1);
            construction.Release();
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job))!.Phase != "completed")
                await Task.Delay(20, completion.Token);
            (await BackgroundJobRuntimeRows.CountOrganizationAsync(database.AppConnectionString, finalized.Value)).Should().Be(1);
        }
        finally
        {
            construction.Release();
            runtime.RequestStop();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await runtime.StopAsync(cleanup.Token);
            await runtime.Completion.WaitAsync(cleanup.Token);
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.Control!.Executions.Should().Be(0);
    }

    private static BackgroundJobRuntime NativeSdkRuntime(IServiceProvider services, PrivilegedDataSource source,
        BackgroundJobRuntimeOptions bounds, TimeProvider? time = null)
    {
        var gate = new BackgroundJobRuntimeGate(Fixture.Registry.Infrastructure);
        var ids = new SystemGuidFactory();
        var runtime = new BackgroundJobRuntime(source, Fixture.Registry, BackgroundJobRecurrenceRegistry.CreateEmpty(),
            services.GetRequiredService<JobExecutor>(), new(source, Fixture.Registry, bounds, ids, gate),
            gate, bounds, ids, time ?? TimeProvider.System, new(false, 100), new HangfireBoundedLogProvider(),
            services.GetRequiredService<ApplicationDataSourceLifetime>());
        runtime.BindMessagingLifetime(Task.CompletedTask, Task.CompletedTask);
        return runtime;
    }

    private static HangfireGenerationWork.Invocation NativeSdkInvocation(BackgroundJobRuntime runtime)
    {
        var pools = (IEnumerable<HangfirePoolOwner>)NativeSdkField(runtime, "_pools");
        var generation = (HangfireGeneration)NativeSdkField(pools.Single(pool => pool.Snapshot.Pool == "external"), "_generation");
        var work = (HangfireGenerationWork)NativeSdkField(generation, "_work");
        lock (NativeSdkField(work, "_sync"))
            return ((IEnumerable<HangfireGenerationWork.Invocation>)NativeSdkField(work, "_active"))
                .Single(candidate => typeof(HangfireGenerationWork.Invocation)
                    .GetField("_execution", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(candidate)
                        is BackgroundJobExecutionControl);
    }

    // Observe the actual identities under their existing lock. No field, task,
    // result, completion capability or timer is replaced or manufactured.
    private static object NativeSdkField(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
        ?? throw new InvalidOperationException("The actual native SDK owner field is missing.");
}
