using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// MediatR pipeline behavior — step 4 of the canonical 8-step order
/// (ADR-0032 § Sub-decision 2). Asserts that the upstream resolution stage
/// populated <see cref="ITenantContext"/>; when it has not, short-circuits
/// the request with <c>Result.Fail(tenant_mismatch)</c>. The PostgreSQL RLS
/// session-variable wiring (<c>app.tenant_id</c> / <c>app.organization_id</c>
/// via a <c>DbConnectionInterceptor</c>) lights up in Packet 7 when the
/// resolver middleware lands.
/// </summary>
/// <remarks>
/// Phase 02a Packet 3 ships the <strong>assertion shell</strong>. Until
/// Packet 7 lands the resolver middleware every request runs against
/// <see cref="UnresolvedTenantContext"/>; this behavior surfaces the fact
/// loudly so no handler reads an unresolved context by accident. Packet 7
/// flips the default registration to the real resolver and adds the RLS
/// interceptor line below the assertion.
/// </remarks>
public sealed class TenantContextBehavior<TRequest, TResponse>(
    ITenantContext tenantContext)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResultBase
{
    private static readonly Error TenantMismatchError = new(
        new LocalizedMessage("lockey_tenant_mismatch"));

    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (!tenantContext.IsResolved && !AllowsUnresolvedContext(typeof(TRequest)))
        {
            return Task.FromResult(Result.FailFor<TResponse>(TenantMismatchError));
        }

        // TODO(2026-05-21, @platform, phase-02a-packet-7): set the PostgreSQL
        // RLS GUCs via a DbConnectionInterceptor (transaction-local
        // set_config('app.tenant_id', ..., true) /
        // set_config('app.organization_id', ..., true)). The interceptor
        // lands together with the tenant-owned schema + RLS policies.

        return next();
    }

    /// <summary>
    /// Opt-in escape hatch for commands that are explicitly platform-wide
    /// (e.g. tenant provisioning). The default is "no exceptions"; opt-in
    /// arrives in Packet 7 alongside the <c>EnterPlatformAdminScope(reason)</c>
    /// surface. Until then the predicate is a stub returning <c>false</c> —
    /// every request needs a resolved context to proceed.
    /// </summary>
    private static bool AllowsUnresolvedContext(Type requestType) => false;
}
