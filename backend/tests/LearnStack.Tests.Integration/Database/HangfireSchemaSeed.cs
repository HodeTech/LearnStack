using System.Text.Json;
using LearnStack.Infrastructure.Jobs;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Nonempty pinned vendor subjects for every-table ACL sweeps.</summary>
internal static class HangfireSchemaSeed
{
    private static readonly string[] TickArguments = ["\"fixture.tenants\"", "1"];

    public static async Task SeedAsync(string dispatcherConnectionString)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(dispatcherConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await HangfireStorageTests.InsertJobAsync(connection);
        var args = JsonSerializer.Serialize(TickArguments);
        var tick = JsonSerializer.Serialize(new
        {
            Type = HangfireStorageContract.RecurrenceTickType,
            Method = "RunAsync",
            ParameterTypes = "[\"System.String\",\"System.Int64\"]",
            Arguments = args,
        });
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.jobparameter(jobid,name,value) VALUES (@id,'RetryCount','0');
            INSERT INTO public.state(jobid,name,createdat,data) VALUES (@id,'Enqueued',now(),'{"Queue":"schema_fixture"}');
            INSERT INTO public.jobqueue(jobid,queue) VALUES (@id,'schema_fixture');
            INSERT INTO public.counter(key,value) VALUES ('stats:succeeded',1);
            INSERT INTO public.aggregatedcounter(key,value) VALUES ('stats:succeeded',1);
            INSERT INTO public.hash(key,field,value) VALUES ('recurring-job:fixture.tenants','Job',@tick);
            INSERT INTO public.set(key,score,value) VALUES ('schedule',0,@textid);
            INSERT INTO public.list(key,value) VALUES ('failed',@textid);
            INSERT INTO public.lock(resource,acquired) VALUES ('locks:expirationmanager',now());
            INSERT INTO public.server(id,data,lastheartbeat)
            VALUES ('learnstack-schema_fixture:1:11111111-1111-4111-8111-111111111111',
              '{"WorkerCount":1,"Queues":["schema_fixture"],"StartedAt":"2026-10-16T00:00:00Z"}',now());
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("textid", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("tick", tick);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
}
