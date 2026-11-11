namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

internal static class ProviderEventWindowResolutionDdl
{
    internal static readonly string ReplaceAcceptance = ProviderEventWindowDdl.ReplaceAcceptance.Replace(
        "UPDATE public.identity_provider_event_problems SET resolved_at=clock_timestamp() WHERE problem_key=issue_key;",
        """
        UPDATE public.identity_provider_event_problems SET resolved_at=clock_timestamp()
            WHERE problem_key IN (issue_key,'expired:'||p_authority::text||':'||p_realm||':'||p_event::text)
                AND fingerprint=fact_hash AND NOT irrecoverable;
        """, StringComparison.Ordinal);
}
