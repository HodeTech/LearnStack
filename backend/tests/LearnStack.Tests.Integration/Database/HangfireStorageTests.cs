using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

// Own container: ACL/version/guard mutation never races another schema proof.
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class HangfireStorageTests(SchemaFixture schema) : IClassFixture<SchemaFixture>
{
    private static readonly string[] FourQueues = ["transactional", "external", "control", "feature_media_v2"];
    private static readonly string[] TransactionalQueues = ["transactional"];
    private static readonly string[] OpaqueArguments = ["\"11111111-1111-7111-8111-111111111111\"", "1"];

    [Fact]
    public async Task Bootstrap_constructs_real_stock_storage_only_after_exact_schema_guards_and_acl_verification()
    {
        await using var source = Source();
        var storage = HangfireStorageBootstrap.Create(source, BackgroundJobInfrastructureRegistry.CreateDefault(), Diagnostics());
        using var connection = storage.GetConnection();
        connection.Should().NotBeNull();
        await using var sql = await source.OpenConnectionAsync();
        (await ScalarAsync(sql, "SELECT version FROM public.\"schema\"")).Should().Be(23);
        (await ScalarAsync(sql, "SELECT count(*) FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind='S'")).Should().Be(9L);
    }

    [Theory]
    [InlineData("UPDATE public.\"schema\" SET version=22", "UPDATE public.\"schema\" SET version=23")]
    [InlineData("ALTER TABLE public.state DISABLE TRIGGER tg_hangfire_state_guard", "ALTER TABLE public.state ENABLE ALWAYS TRIGGER tg_hangfire_state_guard")]
    [InlineData("GRANT SELECT ON public.job TO learnstack_app", "REVOKE SELECT ON public.job FROM learnstack_app")]
    [InlineData("GRANT UPDATE ON public.job_id_seq TO learnstack_outbox_admin", "REVOKE UPDATE ON public.job_id_seq FROM learnstack_outbox_admin")]
    [InlineData("GRANT UPDATE ON public.state TO learnstack_outbox_admin", "REVOKE UPDATE ON public.state FROM learnstack_outbox_admin")]
    [InlineData("GRANT UPDATE(statename) ON public.job TO learnstack_outbox_admin WITH GRANT OPTION", "REVOKE GRANT OPTION FOR UPDATE(statename) ON public.job FROM learnstack_outbox_admin")]
    public async Task Version_guard_and_acl_drift_refuse_before_real_storage_construction(string mutation, string restoration)
    {
        await using var source = Source();
        try
        {
            await OwnerAsync(mutation);
            var create = () => HangfireStorageBootstrap.Create(source, BackgroundJobInfrastructureRegistry.CreateDefault(), Diagnostics());
            create.Should().Throw<HangfireStorageRefusedException>().Which.InnerException.Should().BeNull();
        }
        finally { await OwnerAsync(restoration); }
        var healthy = HangfireStorageBootstrap.Create(source, BackgroundJobInfrastructureRegistry.CreateDefault(), Diagnostics());
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[12]")]
    [InlineData("[\"invalid-queue\"]")]
    public async Task Server_queue_metadata_refuses_nonstring_null_and_invalid_tokens(string queues)
    {
        await using var source = Source();
        await using var connection = await source.OpenConnectionAsync();
        var data = "{\"WorkerCount\":1,\"Queues\":" + queues + ",\"StartedAt\":\"2026-10-16T00:00:00Z\"}";
        using var command = new NpgsqlCommand("INSERT INTO public.server(id,data,lastheartbeat) VALUES (@id,@data::jsonb,now())", connection);
        command.Parameters.AddWithValue("id", "learnstack-fixture:1:11111111-1111-4111-8111-111111111111");
        command.Parameters.AddWithValue("data", data);
        var insert = async () => await command.ExecuteNonQueryAsync();
        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Server_metadata_accepts_four_declared_shape_queues_and_refuses_more_than_sixtyfour()
    {
        await using var source = Source();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var queues = JsonSerializer.Serialize(FourQueues);
        var data = "{\"WorkerCount\":1,\"Queues\":" + queues + ",\"StartedAt\":\"2026-10-16T00:00:00Z\"}";
        await ExecuteAsync(connection, "INSERT INTO public.server(id,data,lastheartbeat) VALUES ('learnstack-fixture:1:11111111-1111-4111-8111-111111111111',@data::jsonb,now())", ("data", data));
        var tooMany = JsonSerializer.Serialize(new { WorkerCount = 1, Queues = Enumerable.Repeat("control", 65).ToArray(), StartedAt = "2026-10-16T00:00:00Z" });
        var insert = async () => await ExecuteAsync(connection, "UPDATE public.server SET data=@data::jsonb WHERE id='learnstack-fixture:1:11111111-1111-4111-8111-111111111111'", ("data", tooMany));
        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Bootstrap_refuses_extra_durable_metadata_even_if_an_owner_reenables_the_guard_after_a_dirty_write()
    {
        await using var source = Source();
        const string server = "learnstack-dirty_fixture:1:11111111-1111-4111-8111-111111111111";
        const string valid = "{\"WorkerCount\":1,\"Queues\":[\"control\"],\"StartedAt\":\"2026-10-16T00:00:00Z\"}";
        await using var connection = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, "INSERT INTO public.server(id,data,lastheartbeat) VALUES (@id,@data::jsonb,now())", ("id", server), ("data", valid));
        try
        {
            await OwnerAsync("ALTER TABLE public.server DISABLE TRIGGER tg_hangfire_metadata_guard; UPDATE public.server SET data=jsonb_set(data,'{Secret}','\"synthetic-secret-canary\"') WHERE id='" + server + "'; ALTER TABLE public.server ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard");
            var create = () => HangfireStorageBootstrap.Create(source, BackgroundJobInfrastructureRegistry.CreateDefault(), Diagnostics());
            create.Should().Throw<HangfireStorageRefusedException>();
        }
        finally
        {
            await ExecuteAsync(connection, "DELETE FROM public.server WHERE id=@id", ("id", server));
        }
    }

    [Fact]
    public async Task Exact_function_body_drift_refuses_even_when_the_trigger_is_still_enabled()
    {
        await using var source = Source();
        const string read = "SELECT pg_get_functiondef('public.fn_hangfire_state_guard()'::regprocedure)";
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.MigrationConnectionString);
        var definition = (string)(await ScalarAsync(owner, read))!;
        try
        {
            await ExecuteAsync(owner, definition.Replace("transport_redacted", "different_code", StringComparison.Ordinal));
            var create = () => HangfireStorageBootstrap.Create(source, BackgroundJobInfrastructureRegistry.CreateDefault(), Diagnostics());
            create.Should().Throw<HangfireStorageRefusedException>();
        }
        finally { await ExecuteAsync(owner, definition); }
    }

    [Theory]
    [InlineData("INSERT INTO public.jobparameter(jobid,name,value) VALUES (@id,'TraceParent','\"synthetic-secret-canary\"')")]
    [InlineData("INSERT INTO public.jobparameter(jobid,name,value) VALUES (@id,'CurrentCulture','\"synthetic-secret-canary\"')")]
    [InlineData("INSERT INTO public.hash(key,field,value) VALUES ('arbitrary','Secret','synthetic-secret-canary')")]
    [InlineData("INSERT INTO public.set(key,score,value) VALUES ('schedule',0,'synthetic-secret-canary')")]
    [InlineData("INSERT INTO public.list(key,value) VALUES ('failed','synthetic-secret-canary')")]
    [InlineData("INSERT INTO public.counter(key,value) VALUES ('synthetic-secret-canary',1)")]
    [InlineData("INSERT INTO public.lock(resource) VALUES ('synthetic-secret-canary')")]
    [InlineData("INSERT INTO public.jobqueue(jobid,queue) VALUES (@id,'invalid-queue')")]
    [InlineData("INSERT INTO public.server(id,data,lastheartbeat) VALUES ('synthetic-secret-canary','{}',now())")]
    [InlineData("UPDATE public.job SET invocationdata=jsonb_set(invocationdata,'{Method}','\"ToString\"') WHERE id=@id")]
    [InlineData("UPDATE public.job SET arguments='[\"synthetic-secret-canary\",\"1\"]' WHERE id=@id")]
    [InlineData("INSERT INTO public.state(jobid,name,createdat,data) VALUES (@id,'Failed',now(),'{\"Unexpected\":\"synthetic-secret-canary\"}')")]
    public async Task Direct_vendor_writes_cannot_store_extra_fields_or_arbitrary_metadata(string dirtySql)
    {
        await using var source = Source();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await InsertJobAsync(connection);
        using var command = new NpgsqlCommand(dirtySql, connection) { Transaction = transaction };
        command.Parameters.AddWithValue("id", id);
        var write = async () => await command.ExecuteNonQueryAsync();
        (await write.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task State_guard_redacts_raw_reason_exception_and_result_below_all_filters()
    {
        await using var source = Source();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await InsertJobAsync(connection);
        await ExecuteAsync(connection, """
            INSERT INTO public.state(jobid,name,reason,createdat,data)
            VALUES (@id,'Failed','synthetic-secret-canary',now(),
              '{"ExceptionType":"synthetic-secret-canary","ExceptionMessage":"synthetic-secret-canary","ExceptionDetails":"synthetic-secret-canary"}');
            INSERT INTO public.state(jobid,name,reason,createdat,data)
            VALUES (@id,'Succeeded','synthetic-secret-canary',now(),'{"Result":{"raw":"synthetic-secret-canary"}}');
            """, ("id", id));
        var text = (string)(await ScalarAsync(connection,
            "SELECT string_agg(reason || data::text,'') FROM public.state WHERE jobid=@id", ("id", id)))!;
        text.Should().Contain("transport_redacted").And.NotContain("synthetic-secret-canary");
    }

    [Fact]
    public async Task A_valid_newer_capability_queue_survives_bootstrap_on_an_older_topology()
    {
        await using var source = Source();
        long id;
        await using (var connection = await source.OpenConnectionAsync())
        {
            id = await InsertJobAsync(connection);
            await ExecuteAsync(connection, "INSERT INTO public.jobqueue(jobid,queue) VALUES (@id,'future_feature_v2')", ("id", id));
        }
        try
        {
            var topology = BackgroundJobInfrastructureRegistry.CreateDefault();
            topology.QueueNames.Should().NotContain("future_feature_v2");
            var storage = HangfireStorageBootstrap.Create(source, topology, Diagnostics());
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await ExecuteAsync(connection, "DELETE FROM public.jobqueue WHERE jobid=@id; DELETE FROM public.job WHERE id=@id", ("id", id));
        }
    }

    [Fact]
    public async Task Real_pinned_worker_deserialize_refusal_and_disabled_filter_fallback_persist_only_redacted_failure()
    {
        await using var source = Source();
        var storage = HangfireStorageBootstrap.Create(source, BackgroundJobInfrastructureRegistry.CreateDefault(), Diagnostics());
        long id;
        await using (var connection = await source.OpenConnectionAsync())
        {
            id = await InsertJobAsync(connection);
            await ExecuteAsync(connection, "UPDATE public.job SET statename='Enqueued' WHERE id=@id; INSERT INTO public.jobqueue(jobid,queue) VALUES (@id,'transactional')", ("id", id));
        }
        try
        {
            using (var vendor = storage.GetConnection())
            {
                var load = () => vendor.GetJobData(id.ToString(System.Globalization.CultureInfo.InvariantCulture)).EnsureLoaded();
                load.Should().Throw<JobLoadException>();
            }
            var performer = new RefusePerformer();
            var changer = new FilterFailure(new BackgroundJobStateChanger(new EmptyFilters()));
            var constructor = typeof(Worker).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(IEnumerable<string>), typeof(IBackgroundJobPerformer), typeof(IBackgroundJobStateChanger), typeof(TimeSpan), typeof(int)], null)!;
            var worker = (Worker)constructor.Invoke([TransactionalQueues, performer, changer, TimeSpan.FromSeconds(10), 1]);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            worker.Execute(new BackgroundProcessContext("learnstack-transactional:1:11111111-1111-4111-8111-111111111111",
                storage, new Dictionary<string, object>(), Guid.NewGuid(), stop.Token, stop.Token, stop.Token));
            changer.DisabledFiltersObserved.Should().BeTrue();
            performer.WasCalled.Should().BeFalse();
            await using var connection = await source.OpenConnectionAsync();
            ((string)(await ScalarAsync(connection, "SELECT statename FROM public.job WHERE id=@id", ("id", id)))!).Should().Be("Failed");
            var state = (string)(await ScalarAsync(connection, "SELECT reason || data::text FROM public.state WHERE jobid=@id ORDER BY id DESC LIMIT 1", ("id", id)))!;
            state.Should().Contain("transport_redacted").And.NotContain("synthetic-secret-canary").And.NotContain("Exception:").And.NotContain("Could not load");
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await ExecuteAsync(connection, "DELETE FROM public.jobqueue WHERE jobid=@id; DELETE FROM public.job WHERE id=@id", ("id", id));
        }
    }

    [Fact]
    public async Task Startup_sql_lock_and_idle_deadlines_survive_reset_and_warm_pool_reuse_with_unlimited_command_timeout()
    {
        await using var source = Source();
        int process;
        await using (var cold = await source.OpenConnectionAsync())
        {
            process = cold.ProcessID;
            await AssertBudgetsAsync(cold);
            await ExecuteAsync(cold, "SET statement_timeout=0; SET lock_timeout=0; SET idle_in_transaction_session_timeout=0");
        }
        await using var warm = await source.OpenConnectionAsync();
        warm.ProcessID.Should().Be(process);
        await AssertBudgetsAsync(warm);
        using var command = new NpgsqlCommand("SELECT pg_sleep(20)", warm) { CommandTimeout = 0 };
        var timer = Stopwatch.StartNew();
        var blocked = async () => await command.ExecuteNonQueryAsync();
        (await blocked.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.QueryCanceled);
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Real_stock_counter_aggregator_commandTimeout_zero_obeys_server_sql_and_lock_deadlines_only()
    {
        await using var source = Source();
        var storage = HangfireStorageBootstrap.Create(source, BackgroundJobInfrastructureRegistry.CreateDefault(), Diagnostics());
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await ExecuteAsync(owner, "LOCK TABLE public.counter IN ACCESS EXCLUSIVE MODE");
#pragma warning disable CS0618 // Exercise the actual pinned provider component, whose Dapper call sets commandTimeout:0.
        var aggregator = storage.GetComponents().Single(component => component.GetType().Name == "CountersAggregator");
#pragma warning restore CS0618
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var timer = Stopwatch.StartNew();
        var blocked = () => aggregator.Execute(stop.Token);
        blocked.Should().Throw<PostgresException>().Which.SqlState.Should().BeOneOf(PostgresErrorCodes.QueryCanceled, PostgresErrorCodes.LockNotAvailable);
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    private PrivilegedDataSource Source() => new(() => OutboxDispatcherDataSource.Build(schema.Postgres.OutboxConnectionString));
    private static HangfireBoundedLogProvider Diagnostics() => new(_ => { });
    private async Task OwnerAsync(string sql)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.MigrationConnectionString);
        await ExecuteAsync(owner, sql);
    }

    internal static async Task<long> InsertJobAsync(NpgsqlConnection connection)
    {
        var args = JsonSerializer.Serialize(OpaqueArguments);
        var invocation = JsonSerializer.Serialize(new
        {
            Type = HangfireStorageContract.JobWakeupType,
            Method = "RunAsync",
            ParameterTypes = "[\"LearnStack.SharedKernel.Jobs.BackgroundJobId, LearnStack.SharedKernel\",\"System.Int64\"]",
            Arguments = args,
        });
        return (long)(await ScalarAsync(connection,
            "INSERT INTO public.job(invocationdata,arguments,createdat) VALUES (@invocation::jsonb,@args::jsonb,now()) RETURNING id",
            ("invocation", invocation), ("args", args)))!;
    }

    private static async Task AssertBudgetsAsync(NpgsqlConnection connection)
    {
        (await ScalarAsync(connection, "SELECT current_setting('statement_timeout')")).Should().Be("5s");
        (await ScalarAsync(connection, "SELECT current_setting('lock_timeout')")).Should().Be("5s");
        (await ScalarAsync(connection, "SELECT current_setting('idle_in_transaction_session_timeout')")).Should().Be("5s");
        connection.ConnectionTimeout.Should().Be(5);
        connection.CommandTimeout.Should().Be(5);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) =>
        _ = await ScalarAsync(connection, sql, parameters);

    private sealed class EmptyFilters : IJobFilterProvider
    {
        public IEnumerable<JobFilter> GetFilters(Job job) => [];
    }
    private sealed class RefusePerformer : IBackgroundJobPerformer
    {
        public bool WasCalled { get; private set; }
        public object Perform(PerformContext context)
        {
            WasCalled = true;
            throw new InvalidOperationException("A missing wrapper must never activate a business performer.");
        }
    }
    private sealed class FilterFailure(IBackgroundJobStateChanger inner) : IBackgroundJobStateChanger
    {
        public bool DisabledFiltersObserved { get; private set; }
        public IState ChangeState(StateChangeContext context)
        {
            if (!context.DisableFilters) throw new InvalidOperationException("synthetic-secret-canary");
            DisabledFiltersObserved = true;
            return inner.ChangeState(context);
        }
    }
}
