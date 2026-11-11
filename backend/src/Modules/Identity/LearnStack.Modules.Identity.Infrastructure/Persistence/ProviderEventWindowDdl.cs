namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>A source position expiring after HTTP observation stays unaccepted without poisoning forward progress.</summary>
internal static class ProviderEventWindowDdl
{
    internal static readonly string ReplaceAcceptance = ProviderEventReceiptReplayDdl.ReplaceAcceptance.Replace(
        """
                RAISE EXCEPTION 'Provider source refused.' USING ERRCODE='23514';
            END IF;
            INSERT INTO public.identity_provider_event_receipts
        """,
        """
                INSERT INTO public.identity_provider_event_problems(problem_key,authority_id,realm_id,provider_event_id,
                    fingerprint,range_from,range_to,reason)
                VALUES('expired:'||p_authority::text||':'||p_realm||':'||p_event::text,p_authority,p_realm,p_event,
                    fact_hash,p_occurred,p_occurred,'out_of_window')
                ON CONFLICT(problem_key) DO UPDATE SET last_seen_at=clock_timestamp(),attempts=identity_provider_event_problems.attempts+1;
                RETURN QUERY SELECT COALESCE(prior.id,'00000000-0000-0000-0000-000000000000'::uuid),'out_of_window'::text,false,0;
                RETURN;
            END IF;
            INSERT INTO public.identity_provider_event_receipts
        """, StringComparison.Ordinal);
}
