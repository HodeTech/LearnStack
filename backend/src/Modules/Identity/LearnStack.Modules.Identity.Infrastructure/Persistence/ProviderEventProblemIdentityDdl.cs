namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>One source identity serializes accepted receipts and earlier minimized window refusals.</summary>
internal static class ProviderEventProblemIdentityDdl
{
    internal static readonly string ReplaceAcceptance = ProviderEventWindowResolutionDdl.ReplaceAcceptance
        .Replace("fact_hash := sha256(", """
            PERFORM pg_advisory_xact_lock(hashtextextended('learnstack.identity.provider-event.v1:'||p_authority::text||':'||p_realm||':'||p_event::text,0));
            fact_hash := sha256(
            """, StringComparison.Ordinal)
        .Replace("IF p_occurred<cfg.activation_lower", """
            IF EXISTS (SELECT FROM public.identity_provider_event_problems p WHERE p.authority_id=p_authority
                AND p.realm_id=p_realm AND p.provider_event_id=p_event AND p.reason='out_of_window' AND p.fingerprint<>fact_hash) THEN
                INSERT INTO public.identity_provider_event_problems(problem_key,authority_id,realm_id,provider_event_id,
                    fingerprint,range_from,range_to,reason)
                VALUES('collision:event:'||p_authority::text||':'||p_realm||':'||p_event::text,p_authority,p_realm,p_event,
                    fact_hash,p_occurred,p_occurred,'source_collision')
                ON CONFLICT(problem_key) DO UPDATE SET last_seen_at=clock_timestamp(),attempts=identity_provider_event_problems.attempts+1;
                RETURN QUERY SELECT COALESCE(prior.id,'00000000-0000-0000-0000-000000000000'::uuid),'source_collision'::text,false,0;
                RETURN;
            END IF;
            IF p_occurred<cfg.activation_lower
            """, StringComparison.Ordinal);
}
