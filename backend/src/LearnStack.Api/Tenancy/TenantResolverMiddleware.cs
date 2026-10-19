using System.Diagnostics;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LearnStack.Api.Tenancy;

/// <summary>
/// Turns the host classification — and, from Phase 02b, the validated claims — into
/// the tenant context every layer below reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>One of exactly four writers of <c>ITenantContextAccessor.Current</c></b>
/// (ADR-0036 § Rules): this for HTTP, <c>HubCorrelationMiddleware</c> for
/// <c>/api/internal/*</c>, the Hangfire <c>JobActivator</c> for jobs, and the outbox
/// / inbox handler scope for integration events. It is the <b>second</b> of the four
/// to exist — <c>InProcessEventBus</c> has written the accessor for the handler scope
/// since Packet 5 — and the first on an HTTP request path.
/// <c>SetTenant_Callers_Are_The_Enumerated_Four</c> holds the line for both.
/// </para>
/// <para>
/// <b>Where it sits, and why not one step earlier.</b> Host classification runs
/// before authentication because it must — an unknown host is refused before any
/// token is validated, which keeps the cheap rejection cheap. Context construction
/// runs after, so
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Rules</see>'s single call to <c>TenantContextFactory.Create</c> happens with
/// both signals in hand rather than twice with one each. Phase 02b inserts
/// <c>UseAuthentication</c> between the two, and <c>UseAuthorization</c> after the
/// assertion comparison — two insertions, not one block.
/// </para>
/// <para>
/// <b>The accessor is restored, not merely overwritten.</b> It is
/// <c>AsyncLocal</c>-backed, so a value written here would otherwise flow into
/// whatever continues on this execution context. The restore is in a
/// <c>finally</c> so it also covers the refusal path and a cancelled request —
/// leaving a resolved context behind on either would hand the next thing that reads
/// the accessor a tenant that no longer has a request.
/// </para>
/// <para>
/// Anonymous and membership refusals retain the masked <c>not_found</c> 404.
/// Authenticated claim/host disagreements use the same Problem Details factory
/// with <c>tenant_mismatch</c>; the pure factory's refusal stays unchanged.
/// </para>
/// </remarks>
public sealed class TenantResolverMiddleware(
    RequestDelegate next,
    ITenantContextAccessor accessor,
    ILogger<TenantResolverMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        IOrganizationScopeValidator organizationScopes,
        ITenantMembershipReader memberships,
        ITenantAssertionRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(organizationScopes);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(recorder);

        // Keyed off the feature, not off a second path predicate. A request host
        // classification did not classify — /healthz, /openapi, the Hub's
        // /api/internal/* surface, whose tenant comes from the envelope's path
        // segment rather than from a host — has no host signal at all, and inventing
        // one here would be a second resolution authority.
        var classification = context.Features.Get<HostClassification>();

        if (classification is null)
        {
            await next(context);
            return;
        }

        var identity = context.Features.Get<TenantCredentialFeature>()?.Identity;
        if (identity is { TenantId: null }) LogClaimless(logger, null);
        var attempt = await BuildAttemptAsync(context, classification, organizationScopes, memberships, identity);

        // Rows 13 and 15. A platform host legitimately resolves no tenant, and that
        // is not a refusal: the request proceeds on the unresolved context and the
        // pipeline decides, which is where [AllowsUnresolvedTenantContext] lives.
        // Written explicitly rather than left alone — "nothing wrote to it" is an
        // assumption a save-and-restore protocol exists precisely to stop relying on.
        var previous = accessor.Current;

        try
        {
            if (attempt.NamesNoTenant)
            {
                accessor.Current = UnresolvedTenantContext.Instance;
                await next(context);
                return;
            }

            var resolution = TenantContextFactory.Create(attempt);

            if (!resolution.IsSuccess)
            {
                LogRefused(logger, classification.Class, null);
                await RecordRefusalAsync(attempt, classification, identity, recorder);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                if (identity is not null && !attempt.ClaimAgreesWithHost)
                {
                    await context.Response.WriteAsJsonAsync(
                        ProblemDetailsFactory.For(TenantMismatch, context),
                        options: null, contentType: ProblemDetailsMediaType.Value,
                        cancellationToken: context.RequestAborted);
                }
                return;
            }

            accessor.Current = resolution.Value;
            await next(context);
        }
        finally
        {
            accessor.Current = previous;
        }
    }

    /// <summary>
    /// Gathers the signals, asking the two ports only on the rows that need them.
    /// </summary>
    /// <remarks>
    /// <b>Membership is asked first, and the order is load-bearing.</b> On row 14 the
    /// tenant is named by the claim alone, with no host to vouch for it. Asking the
    /// organization validator first would open a transaction and announce that
    /// caller-supplied, unconfirmed tenant id to PostgreSQL through
    /// <c>set_config('app.tenant_id', …)</c> before anything had confirmed it.
    /// Membership first means a denied claim costs no database work at all — which,
    /// while <c>DenyAllTenantMembershipReader</c> is registered, is every claim.
    /// </remarks>
    private static async Task<TenantResolutionAttempt> BuildAttemptAsync(
        HttpContext context,
        HostClassification classification,
        IOrganizationScopeValidator organizationScopes,
        ITenantMembershipReader memberships,
        ValidatedTenantIdentity? identity)
    {
        // Only the cryptographically validated typed projection supplies signal J.
        // A raw principal/claim is not a second identity or resolution authority.
        //
        // No module name, and NOT because routing has not run — measured, it has:
        // minimal hosting inserts UseRouting ahead of every user middleware, so
        // context.GetEndpoint() is already non-null here. The reason is a design
        // constraint. Resolution must not vary by route; admitting the matched
        // endpoint into the attempt would make the matrix a function of which
        // endpoint matched, which is a second resolution authority.
        var attempt = new TenantResolutionAttempt
        {
            HostTenantId = classification.TenantId,
            HostOrganizationId = classification.OrganizationId,
            HasValidatedPrincipal = identity is not null,
            ClaimTenantId = identity?.TenantId,
            ClaimOrganizationId = identity?.OrganizationId,
            UserId = identity?.ActorUserId,
            // Activity.Current.Id, not TraceIdentifier — the same expression
            // CorrelationHeaderMiddleware, ProblemDetailsFactory and the L1 handler
            // already use, and for the reason CorrelationHeaderMiddleware states by
            // name: TraceIdentifier is a per-connection Kestrel string that appears in
            // no response header and no error body. ITenantContext.CorrelationId is
            // contractually the W3C traceparent, and this is the first writer of the
            // accessor on an HTTP path — so the wrong value here is what every span,
            // every Serilog line and every Sentry scope on the two live matrix rows
            // would carry, correlating with nothing the caller was given. It also
            // arms an ArgumentException at the first outbox enqueue, where
            // IntegrationEventEnvelope validates with ActivityContext.TryParse —
            // measured false for a TraceIdentifier.
            CorrelationId = Activity.Current?.Id ?? context.TraceIdentifier,
        };

        if (attempt.RequiresMembershipCheck)
        {
            attempt = attempt with
            {
                MembershipCovers = await memberships.CoversAsync(
                    attempt.UserId!.Value,
                    attempt.ClaimTenantId!.Value,
                    attempt.MembershipQuestionOrganizationId,
                    context.RequestAborted),
            };

            // Short-circuit on a denial: the validator's read is only meaningful for
            // a claim that got this far, and skipping it is what keeps the denied
            // path free of a second transaction.
            if (attempt.MembershipCovers is not true)
            {
                return attempt;
            }
        }

        if (attempt.RequiresOrganizationScopeCheck)
        {
            attempt = attempt with
            {
                ClaimedOrganizationBelongsToTenant = await organizationScopes.BelongsToTenantAsync(
                    attempt.ClaimTenantId!.Value,
                    attempt.ClaimOrganizationId!.Value,
                    context.RequestAborted),
            };
        }

        return attempt;
    }

    private static Task RecordRefusalAsync(TenantResolutionAttempt attempt,
        HostClassification host, ValidatedTenantIdentity? identity, ITenantAssertionRecorder recorder)
    {
        if (identity is null) return Task.CompletedTask;
        if (host.TenantId is null)
        {
            recorder.RecordNoTenantRefusal(new TenantNoTenantRefusal(
                TenantAssertionSource.JwtClaim, TenantAssertionDimension.Tenant,
                identity.ActorUserId, attempt.CorrelationId));
            return Task.CompletedTask;
        }

        // Rows 8/11/12 defend the independently classified host tenant. Row 7 also
        // records its rejected organization. Row 10 has no rejected claim scope
        // and the accepted matrix deliberately leaves it unaudited.
        var tenantDisagrees = attempt.ClaimTenantId is { } claimedTenant
            && claimedTenant != host.TenantId;
        if (!attempt.ClaimAgreesWithHost || attempt.RequiresOrganizationScopeCheck)
        {
            var dimension = tenantDisagrees ? TenantAssertionDimension.Tenant
                : TenantAssertionDimension.Organization;
            var asserted = tenantDisagrees ? attempt.ClaimTenantId!.Value.Value
                : attempt.ClaimOrganizationId!.Value.Value;
            return recorder.RecordRejectionAsync(new TenantAssertionRejection(
                host.TenantId.Value.Value, dimension, asserted, IsAuthenticated: true,
                TenantAssertionSource.JwtClaim, identity.ActorUserId,
                attempt.CorrelationId, host.OrganizationId));
        }
        return Task.CompletedTask;
    }

    private static readonly Error TenantMismatch = new(new LocalizedMessage("lockey_tenant_mismatch"));

    private static readonly Action<ILogger, Exception?> LogClaimless = LoggerMessage.Define(
        LogLevel.Debug, new EventId(2, "TenantClaimAbsent"),
        "Tenant authentication outcome tenant_claim_absent.");

    // The host class and nothing else. Which host was addressed is attacker-authored
    // on every anonymous request; host classification already keeps it at Debug for
    // that reason, and a refusal here would otherwise re-emit it one step later at a
    // level an operator forwards.
    private static readonly Action<ILogger, HostClass, Exception?> LogRefused =
        LoggerMessage.Define<HostClass>(
            LogLevel.Debug,
            new EventId(1, nameof(LogRefused)),
            "Tenant resolution refused a {HostClass} request: the signals did not agree.");
}

/// <summary>Registration for <see cref="TenantResolverMiddleware"/>.</summary>
public static class TenantResolverMiddlewareExtensions
{
    public static IApplicationBuilder UseLearnStackTenantResolution(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantResolverMiddleware>();
    }
}
