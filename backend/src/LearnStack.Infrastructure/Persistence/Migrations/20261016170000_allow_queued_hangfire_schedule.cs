using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations;

/// <summary>Keep already-migrated H1 stores compatible with stock explicit-queue delayed wakeups.</summary>
public partial class allow_queued_hangfire_schedule : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION public.fn_hangfire_metadata_valid(table_name text, row_data jsonb) RETURNS boolean
            LANGUAGE plpgsql STABLE SET search_path = pg_catalog, public
            AS $$
            DECLARE valid boolean := false; payload jsonb;
            BEGIN
                CASE table_name
                WHEN 'jobqueue' THEN valid := public.fn_hangfire_queue((row_data->>'queue')) AND (row_data->>'jobid')::bigint > 0;
                WHEN 'server' THEN
                  valid := public.fn_hangfire_server((row_data->>'id')) AND jsonb_typeof((row_data->'data')) = 'object'
                    AND (row_data->'data') ?& ARRAY['WorkerCount','Queues','StartedAt']
                    AND (SELECT count(*) FROM jsonb_object_keys((row_data->'data'))) = 3
                    AND (row_data->'data')->>'WorkerCount' ~ '^([1-9]|1[0-6])$'
                    AND jsonb_typeof((row_data->'data')->'WorkerCount') = 'number'
                    AND jsonb_typeof((row_data->'data')->'Queues') = 'array' AND jsonb_array_length((row_data->'data')->'Queues') BETWEEN 1 AND 64
                    AND NOT EXISTS (SELECT FROM jsonb_array_elements((row_data->'data')->'Queues') queue WHERE jsonb_typeof(queue) <> 'string' OR public.fn_hangfire_queue(queue #>> '{}') IS DISTINCT FROM true)
                    AND (row_data->'data')->>'StartedAt' ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)$';
                WHEN 'counter','aggregatedcounter' THEN valid := (row_data->>'key') IN ('stats:succeeded','stats:deleted') OR (row_data->>'key') ~ '^stats:(succeeded|failed|deleted):[0-9]{4}-[0-9]{2}-[0-9]{2}(-[0-9]{2})?$';
                WHEN 'lock' THEN valid := (row_data->>'resource') IN ('locks:expirationmanager','public:locks:schedulepoller','public:recurring-jobs:lock')
                    OR (row_data->>'resource') ~ '^public:job:[1-9][0-9]{0,18}:state-lock$'
                    OR ((row_data->>'resource') LIKE 'public:lock:recurring-job:%' AND public.fn_hangfire_recurrence(substr((row_data->>'resource'),27)));
                WHEN 'set' THEN valid := (row_data->>'score')::float8 NOT IN ('Infinity'::float8,'-Infinity'::float8,'NaN'::float8)
                    AND (((row_data->>'key') IN ('schedule','failed','succeeded','deleted','processing') AND (row_data->>'value') ~ '^[1-9][0-9]{0,18}$')
                    OR ((row_data->>'key') = 'schedule' AND (row_data->>'value') ~ '^[a-z][a-z0-9_]{0,49}:[1-9][0-9]{0,18}$')
                    OR ((row_data->>'key') = 'recurring-jobs' AND public.fn_hangfire_recurrence((row_data->>'value'))));
                WHEN 'list' THEN valid := (row_data->>'key') IN ('failed','succeeded','deleted') AND (row_data->>'value') ~ '^[1-9][0-9]{0,18}$';
                WHEN 'hash' THEN
                  IF (row_data->>'key') LIKE 'recurring-job:%' AND public.fn_hangfire_recurrence(substr((row_data->>'key'),15)) THEN
                    CASE (row_data->>'field')
                    WHEN 'Job' THEN
                      payload := (row_data->>'value')::jsonb;
                      valid := public.fn_hangfire_invocation(payload,(payload->>'Arguments')::jsonb)
                        AND payload->>'Type' = 'LearnStack.Infrastructure.Jobs.HangfireRecurrenceTick, LearnStack.Infrastructure'
                        AND ((payload->>'Arguments')::jsonb->>0)::jsonb #>> '{}' = substr((row_data->>'key'),15);
                    WHEN 'Queue' THEN valid := public.fn_hangfire_queue((row_data->>'value'));
                    WHEN 'TimeZoneId' THEN valid := (row_data->>'value') = 'UTC';
                    WHEN 'Cron' THEN valid := (row_data->>'value') = '* * * * *';
                    WHEN 'V' THEN valid := (row_data->>'value') = '2';
                    WHEN 'Misfire' THEN valid := (row_data->>'value') = '0';
                    WHEN 'RetryAttempt' THEN valid := (row_data->>'value') ~ '^[0-6]$';
                    WHEN 'LastJobId' THEN valid := (row_data->>'value') = '' OR (row_data->>'value') ~ '^[1-9][0-9]{0,18}$';
                    WHEN 'CreatedAt','LastExecution','NextExecution' THEN valid := (row_data->>'value') = '' OR (row_data->>'value') ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)$';
                    WHEN 'Error' THEN valid := (row_data->>'value') = 'transport_redacted';
                    ELSE valid := false; END CASE;
                  END IF;
                  valid := valid AND octet_length(COALESCE((row_data->>'value'),'')) <= 2048;
                ELSE valid := false; END CASE;
                RETURN valid IS TRUE;
            EXCEPTION WHEN OTHERS THEN RETURN false;
            END $$;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION public.fn_hangfire_metadata_valid(table_name text, row_data jsonb) RETURNS boolean
            LANGUAGE plpgsql STABLE SET search_path = pg_catalog, public
            AS $$
            DECLARE valid boolean := false; payload jsonb;
            BEGIN
                CASE table_name
                WHEN 'jobqueue' THEN valid := public.fn_hangfire_queue((row_data->>'queue')) AND (row_data->>'jobid')::bigint > 0;
                WHEN 'server' THEN
                  valid := public.fn_hangfire_server((row_data->>'id')) AND jsonb_typeof((row_data->'data')) = 'object'
                    AND (row_data->'data') ?& ARRAY['WorkerCount','Queues','StartedAt']
                    AND (SELECT count(*) FROM jsonb_object_keys((row_data->'data'))) = 3
                    AND (row_data->'data')->>'WorkerCount' ~ '^([1-9]|1[0-6])$'
                    AND jsonb_typeof((row_data->'data')->'WorkerCount') = 'number'
                    AND jsonb_typeof((row_data->'data')->'Queues') = 'array' AND jsonb_array_length((row_data->'data')->'Queues') BETWEEN 1 AND 64
                    AND NOT EXISTS (SELECT FROM jsonb_array_elements((row_data->'data')->'Queues') queue WHERE jsonb_typeof(queue) <> 'string' OR public.fn_hangfire_queue(queue #>> '{}') IS DISTINCT FROM true)
                    AND (row_data->'data')->>'StartedAt' ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)$';
                WHEN 'counter','aggregatedcounter' THEN valid := (row_data->>'key') IN ('stats:succeeded','stats:deleted') OR (row_data->>'key') ~ '^stats:(succeeded|failed|deleted):[0-9]{4}-[0-9]{2}-[0-9]{2}(-[0-9]{2})?$';
                WHEN 'lock' THEN valid := (row_data->>'resource') IN ('locks:expirationmanager','public:locks:schedulepoller','public:recurring-jobs:lock')
                    OR (row_data->>'resource') ~ '^public:job:[1-9][0-9]{0,18}:state-lock$'
                    OR ((row_data->>'resource') LIKE 'public:lock:recurring-job:%' AND public.fn_hangfire_recurrence(substr((row_data->>'resource'),27)));
                WHEN 'set' THEN valid := (row_data->>'score')::float8 NOT IN ('Infinity'::float8,'-Infinity'::float8,'NaN'::float8)
                    AND (((row_data->>'key') IN ('schedule','failed','succeeded','deleted','processing') AND (row_data->>'value') ~ '^[1-9][0-9]{0,18}$')
                    OR ((row_data->>'key') = 'recurring-jobs' AND public.fn_hangfire_recurrence((row_data->>'value'))));
                WHEN 'list' THEN valid := (row_data->>'key') IN ('failed','succeeded','deleted') AND (row_data->>'value') ~ '^[1-9][0-9]{0,18}$';
                WHEN 'hash' THEN
                  IF (row_data->>'key') LIKE 'recurring-job:%' AND public.fn_hangfire_recurrence(substr((row_data->>'key'),15)) THEN
                    CASE (row_data->>'field')
                    WHEN 'Job' THEN
                      payload := (row_data->>'value')::jsonb;
                      valid := public.fn_hangfire_invocation(payload,(payload->>'Arguments')::jsonb)
                        AND payload->>'Type' = 'LearnStack.Infrastructure.Jobs.HangfireRecurrenceTick, LearnStack.Infrastructure'
                        AND ((payload->>'Arguments')::jsonb->>0)::jsonb #>> '{}' = substr((row_data->>'key'),15);
                    WHEN 'Queue' THEN valid := public.fn_hangfire_queue((row_data->>'value'));
                    WHEN 'TimeZoneId' THEN valid := (row_data->>'value') = 'UTC';
                    WHEN 'Cron' THEN valid := (row_data->>'value') = '* * * * *';
                    WHEN 'V' THEN valid := (row_data->>'value') = '2';
                    WHEN 'Misfire' THEN valid := (row_data->>'value') = '0';
                    WHEN 'RetryAttempt' THEN valid := (row_data->>'value') ~ '^[0-6]$';
                    WHEN 'LastJobId' THEN valid := (row_data->>'value') = '' OR (row_data->>'value') ~ '^[1-9][0-9]{0,18}$';
                    WHEN 'CreatedAt','LastExecution','NextExecution' THEN valid := (row_data->>'value') = '' OR (row_data->>'value') ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)$';
                    WHEN 'Error' THEN valid := (row_data->>'value') = 'transport_redacted';
                    ELSE valid := false; END CASE;
                  END IF;
                  valid := valid AND octet_length(COALESCE((row_data->>'value'),'')) <= 2048;
                ELSE valid := false; END CASE;
                RETURN valid IS TRUE;
            EXCEPTION WHEN OTHERS THEN RETURN false;
            END $$;
            """);
}
