using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class HangfireStorageTests
{
    [Theory]
    [InlineData("job", "UPDATE public.job SET statename='Unknown' WHERE id=@id")]
    [InlineData("jobqueue", "INSERT INTO public.jobqueue(jobid,queue) VALUES (0,'control')")]
    [InlineData("state", "INSERT INTO public.state(jobid,name,createdat,data) VALUES (@id,'Processing',now(),'{\"ServerId\":\"synthetic-secret-canary\"}')")]
    [InlineData("state", "INSERT INTO public.state(jobid,name,createdat,data) VALUES (@id,'Processing',now(),'{\"WorkerId\":\"synthetic-secret-canary\"}')")]
    [InlineData("state", "INSERT INTO public.state(jobid,name,createdat,data) VALUES (@id,'Processing',now(),'{\"StartedAt\":\"synthetic-secret-canary\"}')")]
    [InlineData("state", "INSERT INTO public.state(jobid,name,createdat,data) VALUES (@id,'Succeeded',now(),'{\"PerformanceDuration\":\"synthetic-secret-canary\"}')")]
    [InlineData("state", "INSERT INTO public.state(jobid,name,createdat,data) VALUES (@id,'Succeeded',now(),'{\"Result\":\"synthetic-secret-canary\"}')")]
    [InlineData("state", "INSERT INTO public.state(jobid,name,createdat,data) VALUES (@id,'Unknown',now(),'{}')")]
    [InlineData("state", "INSERT INTO public.state(jobid,name,reason,createdat) VALUES (@id,'Failed','synthetic-secret-canary',now())")]
    [InlineData("jobparameter", "INSERT INTO public.jobparameter(jobid,name,value) VALUES (@id,'TraceParent','\"synthetic-secret-canary\"')")]
    [InlineData("jobparameter", "INSERT INTO public.jobparameter(jobid,name,value) VALUES (@id,'RetryCount','1')")]
    [InlineData("jobparameter", "INSERT INTO public.jobparameter(jobid,name,value) VALUES (@id,'CurrentCulture','\"synthetic-secret-canary\"')")]
    [InlineData("jobparameter", "INSERT INTO public.jobparameter(jobid,name,value) VALUES (@id,'CurrentCulture','invalid-json')")]
    [InlineData("server", "INSERT INTO public.server(id,data,lastheartbeat) VALUES ('learnstack-fixture:1:11111111-1111-4111-8111-111111111111','{\"WorkerCount\":1,\"Queues\":[\"control\"],\"StartedAt\":\"synthetic-secret-canary\"}',now())")]
    [InlineData("counter", "INSERT INTO public.counter(key,value) VALUES ('synthetic-secret-canary',1)")]
    [InlineData("aggregatedcounter", "INSERT INTO public.aggregatedcounter(key,value) VALUES ('synthetic-secret-canary',1)")]
    [InlineData("lock", "INSERT INTO public.lock(resource) VALUES ('synthetic-secret-canary')")]
    [InlineData("set", "INSERT INTO public.set(key,score,value) VALUES ('schedule','Infinity','1')")]
    [InlineData("set", "INSERT INTO public.set(key,score,value) VALUES ('schedule',0,'synthetic-secret-canary')")]
    [InlineData("set", "INSERT INTO public.set(key,score,value) VALUES ('schedule',0,'Transactional:1')")]
    [InlineData("set", "INSERT INTO public.set(key,score,value) VALUES ('schedule',0,'transactional:0')")]
    [InlineData("set", "INSERT INTO public.set(key,score,value) VALUES ('schedule',0,'transactional:1:2')")]
    [InlineData("set", "INSERT INTO public.set(key,score,value) VALUES ('failed',0,'transactional:1')")]
    [InlineData("set", "INSERT INTO public.set(key,score,value) VALUES ('arbitrary',0,'1')")]
    [InlineData("list", "INSERT INTO public.list(key,value) VALUES ('arbitrary','1')")]
    [InlineData("hash", "INSERT INTO public.hash(key,field,value) VALUES ('arbitrary','Secret','synthetic-secret-canary')")]
    [InlineData("hash", "INSERT INTO public.hash(key,field,value) VALUES ('recurring-job:fixture.tenants','TimeZoneId','synthetic-secret-canary')")]
    [InlineData("hash", "INSERT INTO public.hash(key,field,value) VALUES ('recurring-job:fixture.tenants','Cron','synthetic-secret-canary')")]
    [InlineData("hash", "INSERT INTO public.hash(key,field,value) VALUES ('recurring-job:fixture.tenants','Error','synthetic-secret-canary')")]
    [InlineData("hash", "INSERT INTO public.hash(key,field,value) VALUES ('recurring-job:fixture.tenants','Job','{}')")]
    public async Task Bootstrap_uses_write_predicates_to_refuse_committed_metadata_drift_in_every_vendor_store(
        string table, string mutation)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = new PrivilegedDataSource(() => OutboxDispatcherDataSource.Build(database.OutboxConnectionString));
        await using var dispatcher = await source.OpenConnectionAsync();
        var id = await InsertJobAsync(dispatcher);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await ExecuteAsync(dispatcher, """
            INSERT INTO background_job_recurrences (recurrence_key,revision,manifest_hash,target_definition_key,
                schedule,activates_at,anchor_at,period_microseconds,jitter_microseconds,recipient_policy,next_scheduled_at)
            VALUES ('fixture.tenants',1,repeat('a',64),'fixture.run.v1','interval',now(),now(),60000000,0,'serving_tenants',now());
            """);
        var guard = table switch
        {
            "job" => "tg_hangfire_job_guard",
            "state" => "tg_hangfire_state_guard",
            "jobparameter" => "tg_hangfire_parameter_guard",
            _ => "tg_hangfire_metadata_guard",
        };
        await using (var transaction = await owner.BeginTransactionAsync())
        {
            await ExecuteAsync(owner, $"ALTER TABLE public.\"{table}\" DISABLE TRIGGER {guard}");
            await ExecuteAsync(owner, mutation, ("id", id));
            await ExecuteAsync(owner, $"ALTER TABLE public.\"{table}\" ENABLE ALWAYS TRIGGER {guard}");
            await transaction.CommitAsync();
        }
        // The corrupt row is durable and the exact write guard is enabled again.
        ((long)(await ScalarAsync(owner, $"SELECT count(*) FROM public.\"{table}\""))!).Should().BeGreaterThan(0L);
        var create = () => HangfireStorageBootstrap.Create(source, BackgroundJobInfrastructureRegistry.CreateDefault(), Diagnostics());
        create.Should().Throw<HangfireStorageRefusedException>().Which.InnerException.Should().BeNull();

        await ExecuteAsync(owner, $"DELETE FROM public.\"{table}\"");
        create.Should().NotThrow("removing only the dirty row restores the same real bootstrap");
    }
}
