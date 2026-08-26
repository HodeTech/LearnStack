using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// Request-scoped tenant + organization + user context handed to MediatR
/// handlers, EF interceptors, and the audit pipeline. Populated at scope
/// start by <c>TenantResolverMiddleware</c> (HTTP), <c>HubCorrelationMiddleware</c>
/// (<c>/api/internal/*</c>), the Hangfire <c>JobActivator</c> (background jobs),
/// and the outbox / inbox handler scope (integration events). Modules never
/// write to this contract — they read it through DI.
/// </summary>
/// <remarks>
/// Phase 02a Packet 3 ships the contract. The real population sites land in
/// Packet 7 (<c>TenantResolverMiddleware</c>) and Phase 02b (Hangfire + outbox
/// handler scope). Pre-population, the ambient context resolves to a
/// composition-root-provided <c>UnresolvedTenantContext</c> singleton whose
/// <see cref="IsResolved"/> is <c>false</c>.
/// </remarks>
public interface ITenantContext
{
    /// <summary>
    /// <c>true</c> once the resolution pipeline has populated tenant + (where
    /// applicable) organization. <c>TenantContextBehavior</c> short-circuits
    /// the request with <c>Result.Fail(tenant_mismatch)</c> when this is
    /// <c>false</c>.
    /// </summary>
    bool IsResolved { get; }

    /// <summary>
    /// The resolved tenant. Reading on an unresolved context throws
    /// <see cref="System.InvalidOperationException"/> — callers gate on
    /// <see cref="IsResolved"/> first.
    /// </summary>
    Guid TenantId { get; }

    /// <summary>
    /// The resolved organization within the tenant, when the request targets
    /// an <c>[OrganizationScoped]</c> resource. <c>null</c> for tenant-wide
    /// requests.
    /// </summary>
    Guid? OrganizationId { get; }

    /// <summary>
    /// The effective actor. Authenticated requests carry their user, anonymous
    /// requests may carry <c>null</c>, and asynchronous consumers use the fixed
    /// <see cref="UserId.SystemActor"/> principal.
    /// </summary>
    UserId? UserId { get; }

    /// <summary>
    /// The human actor that causally initiated asynchronous work, when known.
    /// The effective <see cref="UserId"/> remains the system actor for an
    /// integration-event consumer.
    /// </summary>
    UserId? CausalActorUserId => null;

    /// <summary>
    /// W3C <c>traceparent</c> string ("00-&lt;trace&gt;-&lt;span&gt;-&lt;flags&gt;")
    /// that threads through HTTP / outbox / Hangfire / Hub envelopes. The
    /// observability stack reads this from the singleton accessor; modules
    /// never set it directly.
    /// </summary>
    string? CorrelationId { get; }

    /// <summary>
    /// Logical module that owns the current request (e.g. <c>"education"</c>,
    /// <c>"classroom"</c>). Tagged onto spans by the
    /// <c>TenantContextSpanProcessor</c>; helpful when filtering Tempo /
    /// Grafana queries.
    /// </summary>
    string? ModuleName { get; }
}
