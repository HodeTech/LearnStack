namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>Classify a changed retained receipt before testing the source window.</summary>
internal static class ProviderEventReceiptCollisionDdl
{
    internal static readonly string ReplaceAcceptance = Replace();

    private static string Replace()
    {
        var previous = ProviderEventProblemIdentityDdl.ReplaceAcceptance;
        var corrected = previous.Replace(
        """
            IF FOUND AND prior.accepted_at IS NOT NULL AND prior.fingerprint=fact_hash THEN
                RETURN QUERY SELECT prior.id,prior.disposition,true,prior.target_count; RETURN;
            END IF;
        """,
        """
            IF FOUND AND prior.fingerprint IS DISTINCT FROM fact_hash THEN
                INSERT INTO public.identity_provider_event_problems(problem_key,authority_id,realm_id,provider_event_id,
                    fingerprint,range_from,range_to,reason)
                VALUES('collision:event:'||p_authority::text||':'||p_realm||':'||p_event::text,p_authority,p_realm,p_event,
                    fact_hash,p_occurred,p_occurred,'source_collision')
                ON CONFLICT(problem_key) DO UPDATE SET last_seen_at=clock_timestamp(),attempts=identity_provider_event_problems.attempts+1;
                RETURN QUERY SELECT prior.id,'source_collision'::text,false,0; RETURN;
            END IF;
            IF FOUND AND prior.accepted_at IS NOT NULL THEN
                RETURN QUERY SELECT prior.id,prior.disposition,true,prior.target_count; RETURN;
            END IF;
        """, StringComparison.Ordinal);
        return corrected != previous ? corrected
            : throw new InvalidOperationException("Provider receipt collision migration source did not match.");
    }
}
