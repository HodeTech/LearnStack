using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// MediatR pipeline behavior — step 4 of the canonical 8-step order
/// (ADR-0032 § Sub-decision 2). Asserts that the upstream resolution stage
/// populated <see cref="ITenantContext"/>; when it has not, short-circuits
/// the request with <c>Result.Fail(tenant_mismatch)</c>. It does <strong>not</strong>
/// set the PostgreSQL RLS session variables: <c>SET LOCAL</c> is transaction-local and
/// this behavior runs at step 4, before any transaction exists. They are issued by
/// <c>TransactionBehavior</c> as the first statement inside the transaction at step 6
/// — see Security Standards § Tenant Context, the single authority for this
/// placement. Two packets, two things: Packet 6 opens the transaction
/// (<c>TransactionBehavior</c>'s unit-of-work shell), and Packet 7 issues the
/// <c>SET LOCAL</c> inside it, together with the resolver middleware that gives it
/// a tenant to write.
/// </summary>
/// <remarks>
/// Phase 02a Packet 3 ships the <strong>assertion shell</strong>. Until
/// Packet 7 lands the resolver middleware every request runs against
/// <see cref="UnresolvedTenantContext"/>; this behavior surfaces the fact
/// loudly so no handler reads an unresolved context by accident. Packet 7
/// flips the default registration to the real resolver. It adds nothing here:
/// the RLS session variables are issued by <c>TransactionBehavior</c> inside
/// the transaction at step 6, never from this behavior.
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

        // Nothing to do here for RLS, and nothing anywhere else yet: no code
        // in this repository issues set_config today. Packet 7 adds it to
        // TransactionBehavior, and until then RLS is not enforced at runtime.
        //
        // Why it belongs there and not here: the GUCs are transaction-local
        // (set_config('app.tenant_id', ..., true)) and this behavior runs at
        // step 4 with no transaction open, so the value would be discarded
        // before the query it protects ever runs. TransactionBehavior at step 6
        // issues them as the first statement inside the transaction — Security
        // Standards § Tenant Context. A DbConnectionInterceptor cannot do it
        // either: it fires at connection open, which precedes BEGIN.

        return next();
    }

    /// <summary>
    /// Opt-in escape hatch for commands that are explicitly platform-wide
    /// (e.g. tenant provisioning). The default is "no exceptions"; opt-in
    /// arrives in Packet 7 alongside the <c>EnterPlatformAdminScope(reason)</c>
    /// surface. Until then the predicate is a stub returning <c>false</c> —
    /// every request needs a resolved context to proceed.
    /// </summary>
    /// <remarks>
    /// TODO(2026-05-21, @platform): Phase 02a Packet 7 — replace the stub
    /// with a real discriminator. The intended seam is a marker attribute
    /// (<c>[AllowsUnresolvedTenantContext]</c>) the predicate scans for
    /// via reflection, paired with an architecture test that asserts the
    /// attribute lives only on the narrow command-set that legitimately
    /// runs before any tenant is resolved (e.g. <c>ProvisionTenantCommand</c>,
    /// <c>EnterPlatformAdminScopeCommand</c>). Documenting the seam now
    /// so Packet 7 doesn't reinvent it.
    /// </remarks>
    private static bool AllowsUnresolvedContext(Type requestType) => false;
}
