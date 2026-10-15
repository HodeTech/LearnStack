-- ADR-0047: below-filter transport containment. No business payload or authority.
-- Functions are invoker-rights, migration-owned and have a fixed search path.
CREATE FUNCTION public.fn_hangfire_queue(value text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path = pg_catalog
AS $$ SELECT value ~ '^[a-z][a-z0-9_]{0,49}$' $$;

CREATE FUNCTION public.fn_hangfire_server(value text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path = pg_catalog
AS $$ SELECT value ~ '^learnstack-[a-z][a-z0-9_]{0,49}:[1-9][0-9]{0,9}:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$' $$;

CREATE FUNCTION public.fn_hangfire_recurrence(value text) RETURNS boolean
LANGUAGE sql STABLE SET search_path = pg_catalog, public
AS $$ SELECT EXISTS (SELECT FROM public.background_job_recurrences WHERE recurrence_key = value) $$;

CREATE FUNCTION public.fn_hangfire_invocation(data jsonb, args jsonb) RETURNS boolean
LANGUAGE plpgsql STABLE SET search_path = pg_catalog, public
AS $$
DECLARE parameter_types jsonb; argument0 jsonb; argument1 bigint; type_name text;
BEGIN
    IF jsonb_typeof(data) <> 'object' OR jsonb_typeof(args) <> 'array'
       OR octet_length(data::text) > 2048 OR octet_length(args::text) > 512
       OR jsonb_array_length(args) <> 2
       OR NOT data ?& ARRAY['Type','Method','ParameterTypes','Arguments']
       OR EXISTS (SELECT FROM jsonb_object_keys(data) key WHERE key NOT IN ('Type','Method','ParameterTypes','Arguments','Queue'))
       OR (data->>'Method') IS DISTINCT FROM 'RunAsync' OR (data ? 'Queue' AND data->>'Queue' IS NOT NULL AND NOT public.fn_hangfire_queue(data->>'Queue'))
       OR jsonb_typeof(data->'Type') <> 'string' OR jsonb_typeof(data->'ParameterTypes') <> 'string'
       OR jsonb_typeof(data->'Arguments') <> 'string'
       OR (data->>'Arguments')::jsonb <> args
       OR jsonb_typeof(args->0) <> 'string' OR jsonb_typeof(args->1) <> 'string'
    THEN RETURN false; END IF;
    parameter_types := (data->>'ParameterTypes')::jsonb;
    argument0 := (args->>0)::jsonb;
    IF (args->>1) !~ '^[1-9][0-9]{0,18}$' THEN RETURN false; END IF;
    argument1 := (args->>1)::bigint;
    type_name := data->>'Type';
    IF type_name = 'LearnStack.Infrastructure.Jobs.HangfireJobWakeup, LearnStack.Infrastructure' THEN
        RETURN parameter_types = '["LearnStack.SharedKernel.Jobs.BackgroundJobId, LearnStack.SharedKernel","System.Int64"]'::jsonb
          AND jsonb_typeof(argument0) = 'string'
          AND argument0 #>> '{}' ~ '^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$';
    ELSIF type_name = 'LearnStack.Infrastructure.Jobs.HangfireRecurrenceTick, LearnStack.Infrastructure' THEN
        RETURN parameter_types = '["System.String","System.Int64"]'::jsonb
          AND jsonb_typeof(argument0) = 'string'
          AND EXISTS (SELECT FROM public.background_job_recurrences WHERE recurrence_key = argument0 #>> '{}' AND revision = argument1);
    END IF;
    RETURN false;
EXCEPTION WHEN OTHERS THEN RETURN false;
END $$;

CREATE FUNCTION public.fn_hangfire_job_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, public
AS $$
BEGIN
    IF public.fn_hangfire_invocation(NEW.invocationdata,NEW.arguments) IS DISTINCT FROM true
       OR (NEW.statename IS NOT NULL AND NEW.statename NOT IN ('Enqueued','Scheduled','Processing','Succeeded','Failed','Deleted'))
       OR (TG_OP = 'UPDATE' AND (NEW.id,NEW.invocationdata,NEW.arguments,NEW.createdat) IS DISTINCT FROM (OLD.id,OLD.invocationdata,OLD.arguments,OLD.createdat))
    THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER tg_hangfire_job_guard BEFORE INSERT OR UPDATE ON public.job FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_job_guard();

CREATE FUNCTION public.fn_hangfire_state_valid(state_name text, state_reason text, state_data jsonb) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE SET search_path = pg_catalog, public
AS $$
DECLARE pair record; allowed text[];
BEGIN
    allowed := CASE state_name
      WHEN 'Enqueued' THEN ARRAY['EnqueuedAt','Queue']
      WHEN 'Scheduled' THEN ARRAY['EnqueueAt','ScheduledAt']
      WHEN 'Processing' THEN ARRAY['StartedAt','ServerId','WorkerId']
      WHEN 'Succeeded' THEN ARRAY['SucceededAt','PerformanceDuration','Latency','Result']
      WHEN 'Failed' THEN ARRAY['FailedAt','ExceptionType','ExceptionMessage','ExceptionDetails','ServerId']
      WHEN 'Deleted' THEN ARRAY['DeletedAt','Exception'] ELSE NULL END;
    IF allowed IS NULL OR (state_data IS NOT NULL AND jsonb_typeof(state_data) <> 'object')
    THEN RETURN false; END IF;
    IF state_reason IS NOT NULL AND state_reason <> 'transport_redacted' THEN RETURN false; END IF;
    FOR pair IN SELECT * FROM jsonb_each(COALESCE(state_data,'{}'::jsonb)) LOOP
      IF NOT pair.key = ANY(allowed) THEN RETURN false; END IF;
      IF pair.key IN ('ExceptionType','ExceptionMessage','ExceptionDetails','Exception','Result') THEN
        IF pair.value IS DISTINCT FROM '"transport_redacted"'::jsonb THEN RETURN false; END IF;
      ELSIF jsonb_typeof(pair.value) <> 'string'
        OR (pair.key = 'Queue' AND NOT public.fn_hangfire_queue(pair.value #>> '{}'))
        OR (pair.key = 'ServerId' AND NOT public.fn_hangfire_server(pair.value #>> '{}'))
        OR (pair.key = 'WorkerId' AND pair.value #>> '{}' !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$')
        OR (pair.key IN ('PerformanceDuration','Latency') AND pair.value #>> '{}' !~ '^[0-9]{1,15}$')
        OR (pair.key IN ('EnqueuedAt','EnqueueAt','ScheduledAt','StartedAt','SucceededAt','FailedAt','DeletedAt') AND pair.value #>> '{}' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)$')
      THEN RETURN false; END IF;
    END LOOP;
    IF octet_length(COALESCE(state_data::text,'')) > 1024 THEN RETURN false; END IF;
    RETURN true;
EXCEPTION WHEN OTHERS THEN RETURN false;
END $$;
CREATE FUNCTION public.fn_hangfire_state_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, public
AS $$
DECLARE field text;
BEGIN
    -- Redact before validating shape/size, including the stock disabled-filter fallback.
    IF NEW.reason IS NOT NULL THEN NEW.reason := 'transport_redacted'; END IF;
    IF jsonb_typeof(NEW.data) = 'object' THEN
      FOREACH field IN ARRAY ARRAY['ExceptionType','ExceptionMessage','ExceptionDetails','Exception','Result'] LOOP
        IF NEW.data ? field THEN NEW.data := jsonb_set(NEW.data,ARRAY[field],'"transport_redacted"'::jsonb); END IF;
      END LOOP;
    END IF;
    IF public.fn_hangfire_state_valid(NEW.name,NEW.reason,NEW.data) IS DISTINCT FROM true
    THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER tg_hangfire_state_guard BEFORE INSERT OR UPDATE ON public.state FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_state_guard();

CREATE FUNCTION public.fn_hangfire_parameter_valid(parameter_name text, parameter_value text) RETURNS boolean
LANGUAGE plpgsql STABLE SET search_path = pg_catalog, public
AS $$
DECLARE value jsonb;
BEGIN
    value := parameter_value::jsonb;
    IF ((parameter_name IN ('CurrentCulture','CurrentUICulture') AND value = '""'::jsonb)
       OR (parameter_name = 'RetryCount' AND parameter_value = '0')
       OR (parameter_name = 'Time' AND parameter_value ~ '^[0-9]{1,15}$')
       OR (parameter_name = 'RecurringJobId' AND jsonb_typeof(value) = 'string' AND public.fn_hangfire_recurrence(value #>> '{}'))) IS DISTINCT FROM true
       OR octet_length(parameter_value) > 256
    THEN RETURN false; END IF;
    RETURN true;
EXCEPTION WHEN OTHERS THEN RETURN false;
END $$;
CREATE FUNCTION public.fn_hangfire_parameter_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, public
AS $$
BEGIN
    IF public.fn_hangfire_parameter_valid(NEW.name,NEW.value) IS DISTINCT FROM true
       OR (TG_OP = 'UPDATE' AND (NEW.id,NEW.jobid,NEW.name) IS DISTINCT FROM (OLD.id,OLD.jobid,OLD.name))
    THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER tg_hangfire_parameter_guard BEFORE INSERT OR UPDATE ON public.jobparameter FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_parameter_guard();

CREATE FUNCTION public.fn_hangfire_metadata_valid(table_name text, row_data jsonb) RETURNS boolean
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
CREATE FUNCTION public.fn_hangfire_metadata_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, public
AS $$
BEGIN
    IF TG_TABLE_NAME = 'hash' THEN
      IF NEW.field = 'Error' THEN NEW.value := 'transport_redacted'; END IF;
    END IF;
    IF public.fn_hangfire_metadata_valid(TG_TABLE_NAME,to_jsonb(NEW)) IS DISTINCT FROM true
    THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.jobqueue FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.server FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.counter FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.aggregatedcounter FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.lock FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.set FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.list FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.hash FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();

ALTER TABLE public."job" ENABLE ALWAYS TRIGGER tg_hangfire_job_guard;
ALTER TABLE public."state" ENABLE ALWAYS TRIGGER tg_hangfire_state_guard;
ALTER TABLE public."jobparameter" ENABLE ALWAYS TRIGGER tg_hangfire_parameter_guard;
ALTER TABLE public."jobqueue" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
ALTER TABLE public."server" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
ALTER TABLE public."counter" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
ALTER TABLE public."aggregatedcounter" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
ALTER TABLE public."lock" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
ALTER TABLE public."set" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
ALTER TABLE public."list" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
ALTER TABLE public."hash" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
REVOKE ALL ON FUNCTION public.fn_hangfire_state_valid(text,text,jsonb), public.fn_hangfire_parameter_valid(text,text), public.fn_hangfire_metadata_valid(text,jsonb), public.fn_hangfire_queue(text), public.fn_hangfire_server(text), public.fn_hangfire_recurrence(text), public.fn_hangfire_invocation(jsonb,jsonb), public.fn_hangfire_job_guard(), public.fn_hangfire_state_guard(), public.fn_hangfire_parameter_guard(), public.fn_hangfire_metadata_guard() FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT EXECUTE ON FUNCTION public.fn_hangfire_state_valid(text,text,jsonb), public.fn_hangfire_parameter_valid(text,text), public.fn_hangfire_metadata_valid(text,jsonb), public.fn_hangfire_queue(text), public.fn_hangfire_server(text), public.fn_hangfire_recurrence(text), public.fn_hangfire_invocation(jsonb,jsonb), public.fn_hangfire_job_guard(), public.fn_hangfire_state_guard(), public.fn_hangfire_parameter_guard(), public.fn_hangfire_metadata_guard() TO learnstack_outbox_admin;
