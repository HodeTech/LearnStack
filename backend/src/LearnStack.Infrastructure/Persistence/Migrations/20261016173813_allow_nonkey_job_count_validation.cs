using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations;

/// <summary>Serialize counted admissions without blocking a held child-parent key-share lock during renewal.</summary>
public partial class allow_nonkey_job_count_validation : Migration
{
    // NO KEY UPDATE still serializes concurrent count/admission writers. The
    // immutable parent key remains protected; read-only child references may
    // coexist with a lease-only UPDATE and its deferred validation at COMMIT.
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION public.fn_background_job_attempt_counts() RETURNS trigger
            LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
            DECLARE target_tenant uuid; target_job uuid; job record; normal_count bigint; finalize_count bigint;
            BEGIN
                target_tenant := NEW.tenant_id;
                IF TG_TABLE_NAME = 'background_jobs' THEN target_job := NEW.id; ELSE target_job := NEW.job_id; END IF;
                SELECT j.normal_attempts, j.finalize_attempts, j.current_attempt_id, j.capacity_group INTO job
                  FROM public.background_jobs j WHERE j.tenant_id = target_tenant AND j.id = target_job FOR NO KEY UPDATE;
                IF NOT FOUND THEN RAISE EXCEPTION 'attempt job unavailable' USING ERRCODE = '23514'; END IF;
                SELECT count(*) FILTER (WHERE a.stage = 'normal'), count(*) FILTER (WHERE a.stage = 'finalize')
                  INTO normal_count, finalize_count FROM public.background_job_attempts a
                  WHERE a.tenant_id = target_tenant AND a.job_id = target_job;
                IF job.normal_attempts <> normal_count OR job.finalize_attempts <> finalize_count
                   OR ((normal_count + finalize_count = 0) <> (job.current_attempt_id IS NULL))
                   OR EXISTS (SELECT 1 FROM public.background_job_attempts a
                       WHERE a.tenant_id = target_tenant AND a.job_id = target_job
                         AND a.ordinal > CASE WHEN a.stage = 'normal' THEN normal_count ELSE finalize_count END) THEN
                    RAISE EXCEPTION 'job counts and durable attempt receipts must commit together' USING ERRCODE = '23514';
                END IF;
                IF job.capacity_group IS NOT NULL AND EXISTS (
                    SELECT 1 FROM public.background_job_attempts a
                    WHERE a.tenant_id = target_tenant AND a.job_id = target_job AND a.stage = 'normal'
                      AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r
                          WHERE r.tenant_id = a.tenant_id AND r.job_id = a.job_id AND r.attempt_id = a.id
                            AND r.capacity_group = job.capacity_group)) THEN
                    RAISE EXCEPTION 'counted capacity admission requires its durable reservation' USING ERRCODE = '23514';
                END IF;
                RETURN NULL;
            END;
            $$;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION public.fn_background_job_attempt_counts() RETURNS trigger
            LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
            DECLARE target_tenant uuid; target_job uuid; job record; normal_count bigint; finalize_count bigint;
            BEGIN
                target_tenant := NEW.tenant_id;
                IF TG_TABLE_NAME = 'background_jobs' THEN target_job := NEW.id; ELSE target_job := NEW.job_id; END IF;
                SELECT j.normal_attempts, j.finalize_attempts, j.current_attempt_id, j.capacity_group INTO job
                  FROM public.background_jobs j WHERE j.tenant_id = target_tenant AND j.id = target_job FOR UPDATE;
                IF NOT FOUND THEN RAISE EXCEPTION 'attempt job unavailable' USING ERRCODE = '23514'; END IF;
                SELECT count(*) FILTER (WHERE a.stage = 'normal'), count(*) FILTER (WHERE a.stage = 'finalize')
                  INTO normal_count, finalize_count FROM public.background_job_attempts a
                  WHERE a.tenant_id = target_tenant AND a.job_id = target_job;
                IF job.normal_attempts <> normal_count OR job.finalize_attempts <> finalize_count
                   OR ((normal_count + finalize_count = 0) <> (job.current_attempt_id IS NULL))
                   OR EXISTS (SELECT 1 FROM public.background_job_attempts a
                       WHERE a.tenant_id = target_tenant AND a.job_id = target_job
                         AND a.ordinal > CASE WHEN a.stage = 'normal' THEN normal_count ELSE finalize_count END) THEN
                    RAISE EXCEPTION 'job counts and durable attempt receipts must commit together' USING ERRCODE = '23514';
                END IF;
                IF job.capacity_group IS NOT NULL AND EXISTS (
                    SELECT 1 FROM public.background_job_attempts a
                    WHERE a.tenant_id = target_tenant AND a.job_id = target_job AND a.stage = 'normal'
                      AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r
                          WHERE r.tenant_id = a.tenant_id AND r.job_id = a.job_id AND r.attempt_id = a.id
                            AND r.capacity_group = job.capacity_group)) THEN
                    RAISE EXCEPTION 'counted capacity admission requires its durable reservation' USING ERRCODE = '23514';
                END IF;
                RETURN NULL;
            END;
            $$;
            """);
}
