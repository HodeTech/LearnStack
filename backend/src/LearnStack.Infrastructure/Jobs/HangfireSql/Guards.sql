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

CREATE FUNCTION public.fn_hangfire_state_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, public
AS $$
DECLARE pair record; allowed text[];
BEGIN
    allowed := CASE NEW.name
      WHEN 'Enqueued' THEN ARRAY['EnqueuedAt','Queue']
      WHEN 'Scheduled' THEN ARRAY['EnqueueAt','ScheduledAt']
      WHEN 'Processing' THEN ARRAY['StartedAt','ServerId','WorkerId']
      WHEN 'Succeeded' THEN ARRAY['SucceededAt','PerformanceDuration','Latency','Result']
      WHEN 'Failed' THEN ARRAY['FailedAt','ExceptionType','ExceptionMessage','ExceptionDetails','ServerId']
      WHEN 'Deleted' THEN ARRAY['DeletedAt','Exception'] ELSE NULL END;
    IF allowed IS NULL OR (NEW.data IS NOT NULL AND jsonb_typeof(NEW.data) <> 'object')
    THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
    -- Free-form text is replaced BEFORE size/shape checking, including fallback
    -- exceptions so a very large raw failure still reaches a bounded Failed row.
    IF NEW.reason IS NOT NULL THEN NEW.reason := 'transport_redacted'; END IF;
    FOR pair IN SELECT * FROM jsonb_each(COALESCE(NEW.data,'{}'::jsonb)) LOOP
      IF NOT pair.key = ANY(allowed) THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
      IF pair.key IN ('ExceptionType','ExceptionMessage','ExceptionDetails','Exception','Result') THEN
        NEW.data := jsonb_set(NEW.data,ARRAY[pair.key],'"transport_redacted"'::jsonb);
      ELSIF jsonb_typeof(pair.value) <> 'string'
        OR (pair.key = 'Queue' AND NOT public.fn_hangfire_queue(pair.value #>> '{}'))
        OR (pair.key = 'ServerId' AND NOT public.fn_hangfire_server(pair.value #>> '{}'))
        OR (pair.key = 'WorkerId' AND pair.value #>> '{}' !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$')
        OR (pair.key IN ('PerformanceDuration','Latency') AND pair.value #>> '{}' !~ '^[0-9]{1,15}$')
        OR (pair.key IN ('EnqueuedAt','EnqueueAt','ScheduledAt','StartedAt','SucceededAt','FailedAt','DeletedAt') AND pair.value #>> '{}' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)$')
      THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
    END LOOP;
    IF octet_length(COALESCE(NEW.data::text,'')) > 1024 THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER tg_hangfire_state_guard BEFORE INSERT OR UPDATE ON public.state FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_state_guard();

CREATE FUNCTION public.fn_hangfire_parameter_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, public
AS $$
DECLARE value jsonb;
BEGIN
    value := NEW.value::jsonb;
    IF ((NEW.name IN ('CurrentCulture','CurrentUICulture') AND value = '""'::jsonb)
       OR (NEW.name = 'RetryCount' AND NEW.value = '0')
       OR (NEW.name = 'Time' AND NEW.value ~ '^[0-9]{1,15}$')
       OR (NEW.name = 'RecurringJobId' AND jsonb_typeof(value) = 'string' AND public.fn_hangfire_recurrence(value #>> '{}'))) IS DISTINCT FROM true
       OR octet_length(NEW.value) > 256
       OR (TG_OP = 'UPDATE' AND (NEW.id,NEW.jobid,NEW.name) IS DISTINCT FROM (OLD.id,OLD.jobid,OLD.name))
    THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
    RETURN NEW;
EXCEPTION WHEN invalid_text_representation THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514';
END $$;
CREATE TRIGGER tg_hangfire_parameter_guard BEFORE INSERT OR UPDATE ON public.jobparameter FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_parameter_guard();

CREATE FUNCTION public.fn_hangfire_metadata_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, public
AS $$
DECLARE valid boolean := false; pair record; payload jsonb;
BEGIN
    CASE TG_TABLE_NAME
    WHEN 'jobqueue' THEN valid := public.fn_hangfire_queue(NEW.queue) AND NEW.jobid > 0;
    WHEN 'server' THEN
      valid := public.fn_hangfire_server(NEW.id) AND jsonb_typeof(NEW.data) = 'object'
        AND NEW.data ?& ARRAY['WorkerCount','Queues','StartedAt']
        AND (SELECT count(*) FROM jsonb_object_keys(NEW.data)) = 3
        AND NEW.data->>'WorkerCount' ~ '^([1-9]|1[0-6])$'
        AND jsonb_typeof(NEW.data->'WorkerCount') = 'number'
        AND jsonb_typeof(NEW.data->'Queues') = 'array' AND jsonb_array_length(NEW.data->'Queues') BETWEEN 1 AND 64
        AND NOT EXISTS (SELECT FROM jsonb_array_elements(NEW.data->'Queues') queue WHERE jsonb_typeof(queue) <> 'string' OR public.fn_hangfire_queue(queue #>> '{}') IS DISTINCT FROM true)
        AND NEW.data->>'StartedAt' ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)$';
    WHEN 'counter','aggregatedcounter' THEN valid := NEW.key IN ('stats:succeeded','stats:deleted') OR NEW.key ~ '^stats:(succeeded|failed|deleted):[0-9]{4}-[0-9]{2}-[0-9]{2}(-[0-9]{2})?$';
    WHEN 'lock' THEN valid := NEW.resource IN ('locks:expirationmanager','public:locks:schedulepoller','public:recurring-jobs:lock')
        OR NEW.resource ~ '^public:job:[1-9][0-9]{0,18}:state-lock$'
        OR (NEW.resource LIKE 'public:lock:recurring-job:%' AND public.fn_hangfire_recurrence(substr(NEW.resource,27)));
    WHEN 'set' THEN valid := NEW.score NOT IN ('Infinity'::float8,'-Infinity'::float8,'NaN'::float8)
        AND ((NEW.key IN ('schedule','failed','succeeded','deleted','processing') AND NEW.value ~ '^[1-9][0-9]{0,18}$')
        OR (NEW.key = 'recurring-jobs' AND public.fn_hangfire_recurrence(NEW.value)));
    WHEN 'list' THEN valid := NEW.key IN ('failed','succeeded','deleted') AND NEW.value ~ '^[1-9][0-9]{0,18}$';
    WHEN 'hash' THEN
      IF NEW.key LIKE 'recurring-job:%' AND public.fn_hangfire_recurrence(substr(NEW.key,15)) THEN
        CASE NEW.field
        WHEN 'Job' THEN
          payload := NEW.value::jsonb;
          valid := public.fn_hangfire_invocation(payload,(payload->>'Arguments')::jsonb)
            AND payload->>'Type' = 'LearnStack.Infrastructure.Jobs.HangfireRecurrenceTick, LearnStack.Infrastructure'
            AND ((payload->>'Arguments')::jsonb->>0)::jsonb #>> '{}' = substr(NEW.key,15);
        WHEN 'Queue' THEN valid := public.fn_hangfire_queue(NEW.value);
        WHEN 'TimeZoneId' THEN valid := NEW.value = 'UTC';
        WHEN 'Cron' THEN valid := NEW.value = '* * * * *';
        WHEN 'V' THEN valid := NEW.value = '2';
        WHEN 'Misfire' THEN valid := NEW.value = '0';
        WHEN 'RetryAttempt' THEN valid := NEW.value ~ '^[0-6]$';
        WHEN 'LastJobId' THEN valid := NEW.value = '' OR NEW.value ~ '^[1-9][0-9]{0,18}$';
        WHEN 'CreatedAt','LastExecution','NextExecution' THEN valid := NEW.value = '' OR NEW.value ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)$';
        WHEN 'Error' THEN NEW.value := 'transport_redacted'; valid := true;
        ELSE valid := false; END CASE;
      END IF;
      valid := valid AND octet_length(COALESCE(NEW.value,'')) <= 2048;
    ELSE valid := false; END CASE;
    IF valid IS DISTINCT FROM true THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
    RETURN NEW;
EXCEPTION WHEN invalid_text_representation THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514';
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
REVOKE ALL ON FUNCTION public.fn_hangfire_queue(text), public.fn_hangfire_server(text), public.fn_hangfire_recurrence(text), public.fn_hangfire_invocation(jsonb,jsonb), public.fn_hangfire_job_guard(), public.fn_hangfire_state_guard(), public.fn_hangfire_parameter_guard(), public.fn_hangfire_metadata_guard() FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT EXECUTE ON FUNCTION public.fn_hangfire_queue(text), public.fn_hangfire_server(text), public.fn_hangfire_recurrence(text), public.fn_hangfire_invocation(jsonb,jsonb), public.fn_hangfire_job_guard(), public.fn_hangfire_state_guard(), public.fn_hangfire_parameter_guard(), public.fn_hangfire_metadata_guard() TO learnstack_outbox_admin;
