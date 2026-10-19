using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Infrastructure.Persistence.Migrations
{
    /// <summary>Fence actual recurring invocation replacement inside the vendor transaction.</summary>
    public partial class fence_hangfire_recurrence_revision : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.fn_hangfire_metadata_guard() RETURNS trigger
                LANGUAGE plpgsql VOLATILE SET search_path = pg_catalog, public
                AS $$
                DECLARE payload jsonb;
                BEGIN
                    IF TG_TABLE_NAME = 'hash' THEN
                      IF NEW.field = 'Error' THEN NEW.value := 'transport_redacted'; END IF;
                    END IF;
                    IF public.fn_hangfire_metadata_valid(TG_TABLE_NAME,to_jsonb(NEW)) IS DISTINCT FROM true
                    THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
                    IF TG_TABLE_NAME = 'hash' THEN
                      IF NEW.field = 'Job' THEN
                        payload := NEW.value::jsonb;
                        -- A fresh trigger query and SHARE lock serialize the actual vendor write
                        -- with supersession, including waits before this trigger is entered.
                        PERFORM 1 FROM public.background_job_recurrences
                          WHERE recurrence_key = substr(NEW.key,15)
                            AND revision = ((payload->>'Arguments')::jsonb->>1)::bigint
                            AND superseded_at IS NULL
                          FOR SHARE;
                        IF NOT FOUND THEN
                          RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514';
                        END IF;
                      END IF;
                    END IF;
                    RETURN NEW;
                END $$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.fn_hangfire_metadata_guard() RETURNS trigger
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
                """);
        }
    }
}
