using LearnStack.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class add_guarded_hangfire_storage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var statement in HangfireStorageContract.InstallStatements()) migrationBuilder.Sql(statement);
        migrationBuilder.Sql(HangfireStorageContract.Resource("Guards.sql"));
        migrationBuilder.Sql(HangfireStorageContract.Grants());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // No CASCADE: accidental dependencies refuse destructive rollback.
        foreach (var table in new[] { "jobparameter", "state", "jobqueue", "job", "hash", "list", "set", "server", "lock", "counter", "aggregatedcounter", "schema" })
            migrationBuilder.Sql($"DROP TABLE public.\"{table}\";");
        migrationBuilder.Sql("""
            DROP FUNCTION public.fn_hangfire_job_guard(), public.fn_hangfire_state_guard(),
                public.fn_hangfire_parameter_guard(), public.fn_hangfire_metadata_guard();
            DROP FUNCTION public.fn_hangfire_invocation(jsonb,jsonb), public.fn_hangfire_recurrence(text),
                public.fn_hangfire_queue(text), public.fn_hangfire_server(text);
            """);
    }
}
