namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// Singleton, <c>AsyncLocal&lt;ITenantContext?&gt;</c>-backed accessor that
/// gives cross-cutting infrastructure (OTel span processor, Serilog enricher,
/// Sentry enricher) a way to read the current tenant context without
/// injecting <see cref="ITenantContext"/> itself, whose production registration
/// is transient and resolves from this accessor on every access. A singleton
/// that captured it would pass DI validation silently and then pin one
/// request's value for the process lifetime — nothing fails at startup.
/// </summary>
/// <remarks>
/// <para>
/// Modules <strong>never</strong> write to this accessor. Population is
/// owned by the resolution sites listed in ADR-0032 § Sub-decision 10:
/// <c>TenantResolverMiddleware</c> (HTTP), <c>HubCorrelationMiddleware</c>
/// (<c>/api/internal/*</c>), Hangfire <c>JobActivator</c> (background jobs),
/// outbox / inbox handler scope (integration events).
/// </para>
/// <para>
/// <see cref="Current"/> is <c>null</c> outside any resolved scope (warm-up
/// activities created during startup, background tasks before any handler
/// scope opened) — readers must handle the <c>null</c> case rather than
/// throw. The <c>TenantContextSpanProcessor_DoesNotThrow_When_Context_Missing</c>
/// unit test guards the OTel processor's behaviour.
/// </para>
/// </remarks>
public interface ITenantContextAccessor
{
    ITenantContext? Current { get; set; }
}
