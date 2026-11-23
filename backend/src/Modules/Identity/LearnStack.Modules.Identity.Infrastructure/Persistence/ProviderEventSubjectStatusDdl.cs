using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// ADR-0107 §§ 7 and 9: the delayed tenant mirror of a provider fact refuses a person whose User is no longer Active, as
/// provider intake already does. The mirror's delivery asks this definer for the affected person's status, read in the
/// database under the person's User fence held shared, so it orders against a closure's Begin and Finalize, which hold
/// that fence exclusively.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a definer.</b> The application role reads no other person's <c>users</c> row, and the request guard's User
/// status witness answers only inside a trigger under the exclusive User fence, so neither serves a delivery. This one
/// returns the status alone, of the affected person of one target only: a target of the tenant the delivery announced,
/// already enqueued, which is exactly the target the mirror's delivery reads. Anything else is NULL, which the reader
/// treats as not Active.
/// </para>
/// <para>
/// <b>Why the fence.</b> Intake takes the same shared User key before it reads the status (ADR-0097), so a delivery that
/// read Active commits before a Begin can make the User Closing, and its row is then one Finalize's redaction selects; a
/// delivery that waited for a Begin or a Finalize reads the status it committed. The key is the transaction's, released at
/// the delivery's commit, and taken after nothing else of the Identity families, so it orders no cycle.
/// </para>
/// </remarks>
internal static class ProviderEventSubjectStatusDdl
{
    internal static void Install(MigrationBuilder migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        migration.Sql(SubjectStatus);
    }

    private const string SubjectStatus = """
        CREATE FUNCTION public.fn_identity_provider_event_subject_status(target uuid) RETURNS text
            LANGUAGE plpgsql VOLATILE STRICT SECURITY DEFINER SET search_path = pg_catalog
        AS $$
        DECLARE affected uuid; selected text;
        BEGIN
            SELECT t.affected_user_id INTO affected FROM public.identity_provider_event_targets AS t
                WHERE t.id = target AND t.tenant_id = NULLIF(pg_catalog.current_setting('app.tenant_id', true), '')::uuid
                  AND t.enqueued_at IS NOT NULL;
            IF affected IS NULL THEN
                RETURN NULL;
            END IF;
            PERFORM pg_catalog.pg_advisory_xact_lock_shared(public.fn_identity_contextual_lock_key('user', affected));
            SELECT person.status INTO selected FROM public.users AS person WHERE person.id = affected;
            RETURN selected;
        END;
        $$;
        ALTER FUNCTION public.fn_identity_provider_event_subject_status(uuid) OWNER TO learnstack_migration;
        REVOKE ALL ON FUNCTION public.fn_identity_provider_event_subject_status(uuid) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION public.fn_identity_provider_event_subject_status(uuid) TO learnstack_app;
        """;
}
