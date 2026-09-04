using LearnStack.Api.Common;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LearnStack.Api.Tenancy;

/// <summary>
/// Compares the client's <c>X-Tenant-Id</c> / <c>X-Organization-Id</c>
/// assertions against what the API resolved, per
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § What the assertions do</see>. The headers can cause a request to be
/// rejected; they can never cause a tenant to be selected.
/// </summary>
/// <remarks>
/// <para>
/// Middleware rather than a MediatR behavior, because the assertion is a
/// property of the request binding: a non-MediatR endpoint must not be able to
/// bypass a tenant boundary, and the rejection must precede handler work.
/// </para>
/// <para>
/// In Packet 4 nothing resolved a tenant, so the mismatch path was unreachable in
/// traffic and was exercised by tests over a stubbed context. Packet 7's
/// <c>TenantResolverMiddleware</c> made it reachable: <c>ITenantContext.IsResolved</c>
/// is now true for any request arriving on a mapped host. The comparison shipped
/// before the binding it guards because a binding
/// whose rule arrives three packets later is a binding nobody wrote the rule
/// for.
/// </para>
/// </remarks>
public sealed class TenantAssertionMiddleware(RequestDelegate next)
{
    public const string TenantHeaderName = "X-Tenant-Id";
    public const string OrganizationHeaderName = "X-Organization-Id";

    /// <summary>
    /// The only prefix the comparison applies to. Registered globally, a
    /// malformed <c>X-Tenant-Id</c> 400s the orchestrator's health probe and the
    /// Hub's <c>/api/internal/*</c> surface — neither of which has a tenant
    /// assertion to compare, and the first of which failing takes the pod out.
    /// ADR-0036 scopes host classification to <c>/api/v1/*</c> for the same
    /// reason; the assertion is part of that surface's binding, not of the
    /// process's.
    /// </summary>
    public const string ScopedPrefix = "/api/v";

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenantContext,
        ITenantAssertionRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(recorder);

        if (!IsVersionedApiPath(context.Request.Path.Value))
        {
            await next(context);
            return;
        }

        // Evaluated separately, not short-circuited: `||` reports the tenant
        // dimension even when the organization header was the malformed one,
        // which is the one thing this counter exists to tell an operator.
        var tenantIsReadable =
            TryReadAssertion(context, TenantHeaderName, out var assertedTenant);
        var organizationIsReadable =
            TryReadAssertion(context, OrganizationHeaderName, out var assertedOrganization);

        if (!tenantIsReadable || !organizationIsReadable)
        {
            // Malformed or repeated. 400 rather than 404, because this is not
            // a claim about a tenant at all — it is a value that cannot be one.
            // Refused rather than resolved by first-or-last: a header present
            // twice is the classic confusion bug, and whichever end you pick,
            // some topology makes it the attacker's.
            //
            // Counted, not recorded: there is no resolved tenant to record it
            // under, and the value was never a tenant id in the first place.
            recorder.RecordUnresolved(tenantIsReadable
                ? TenantAssertionDimension.Organization
                : TenantAssertionDimension.Tenant);
            await WriteAsync(context, StatusCodes.Status400BadRequest);
            return;
        }

        if (assertedTenant is null && assertedOrganization is null)
        {
            await next(context);
            return;
        }

        if (!tenantContext.IsResolved)
        {
            // An assertion never fills a gap it cannot fill. The request fails
            // exactly as it would have without the header — which today is
            // TenantContextBehavior's job — and the occurrence is counted
            // rather than recorded, because there is no tenant to record it
            // under.
            if (assertedTenant is not null)
            {
                recorder.RecordUnresolved(TenantAssertionDimension.Tenant);
            }

            if (assertedOrganization is not null)
            {
                recorder.RecordUnresolved(TenantAssertionDimension.Organization);
            }

            await next(context);
            return;
        }

        var mismatch = Mismatch(tenantContext, assertedTenant, assertedOrganization);
        if (mismatch is not null)
        {
            recorder.RecordRejection(new TenantAssertionRejection(
                // Value, not the id: TenantAssertionRejection carries a Guid and
                // feeds it to a metric tag, so keeping the underlying value here
                // holds the exported dimension byte-identical across this
                // conversion. Safe unconditionally — the !IsResolved branch
                // above has already returned.
                tenantContext.TenantId.Value,
                mismatch.Value.Dimension,
                mismatch.Value.Asserted,
                context.User.Identity?.IsAuthenticated == true));

            // 404, not 403: saying "wrong tenant" confirms the other tenant
            // exists. From Phase 02b the code differs by caller —
            // `tenant_mismatch` for an authenticated one, `not_found` for an
            // anonymous one — so the header adds no bit an anonymous client
            // could not already get by retrying without it. Until then the
            // authenticated tier is dormant per ADR-0036 § Staging across
            // packets — there is no UseAuthentication to be ordered after and
            // the `authenticated` label is constant-false — so every caller
            // takes the anonymous branch, and Phase 02b's split needs this
            // middleware to write the authenticated code itself rather than
            // leaving the body to UseStatusCodePages.
            await WriteAsync(context, StatusCodes.Status404NotFound);
            return;
        }

        await next(context);
    }

    private static (TenantAssertionDimension Dimension, Guid Asserted)? Mismatch(
        ITenantContext tenantContext, Guid? assertedTenant, Guid? assertedOrganization)
    {
        if (assertedTenant is { } tenant && tenant != tenantContext.TenantId.Value)
        {
            return (TenantAssertionDimension.Tenant, tenant);
        }

        // An asserted organization against an unresolved one is a mismatch, not
        // a pass. The pre-conversion form was a lifted `Guid != Guid?`, which is
        // true when the right side is null; spelling it out keeps that, because
        // the alternative — treating "no organization resolved" as agreement —
        // would let a header widen the request's scope, which is the one thing
        // ADR-0036 says an assertion may never do.
        // IsInitialized() alongside the null check, and for the same reason: a
        // non-null OrganizationId? says a struct is there, not that anything
        // assigned it, and Value throws on one nothing did. That throw escapes
        // into UseExceptionHandler and answers 500 — replacing the clean
        // fail-closed 404 this middleware exists to produce with an uncontrolled
        // error, on a pre-auth path, for a request carrying an attacker-supplied
        // header. TenantId's two reads need no such clause: ITenantContext
        // documents IsResolved as implying an initialized TenantId, and the
        // nullable OrganizationId deliberately carries no equivalent promise.
        if (assertedOrganization is { } organization
            && (tenantContext.OrganizationId is not { } resolvedOrganization
                || !resolvedOrganization.IsInitialized()
                || organization != resolvedOrganization.Value))
        {
            return (TenantAssertionDimension.Organization, organization);
        }

        return null;
    }

    /// <summary>
    /// Reads one assertion. Absent is fine; malformed or repeated is not.
    /// </summary>
    /// <summary>
    /// <c>/api/v{N}</c> and nothing that merely starts the same way.
    /// </summary>
    /// <remarks>
    /// A bare <c>StartsWith("/api/v")</c> also matches <c>/api/validate</c>,
    /// <c>/api/vault</c> and <c>/api/verify</c>. The route convention makes
    /// those unreachable today — every controller is prefixed <c>api/v{N}</c>
    /// and only <c>api/internal</c> is exempt — so this is not a live bug. It is
    /// a predicate that says what it means, which is what keeps it true when the
    /// exemption list grows.
    /// </remarks>
    private static bool IsVersionedApiPath(string? path)
    {
        if (path is null || !path.StartsWith(ScopedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = path.AsSpan(ScopedPrefix.Length);
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        return digits > 0 && (digits == rest.Length || rest[digits] == '/');
    }

    private static bool TryReadAssertion(HttpContext context, string header, out Guid? value)
    {
        value = null;
        var raw = context.Request.Headers[header];

        if (raw.Count == 0)
        {
            return true;
        }

        if (raw.Count > 1 || !Guid.TryParse(raw[0], out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static Task WriteAsync(HttpContext context, int status)
    {
        context.Response.StatusCode = status;

        // Deliberately no body written here. UseStatusCodePages, registered
        // ahead of this middleware, gives an empty-bodied client error the one
        // Problem Details shape — so a rejected assertion answers exactly as a
        // routing 404 does, which is the property ADR-0036 depends on.
        return Task.CompletedTask;
    }
}

/// <summary>Registration for <see cref="TenantAssertionMiddleware"/>.</summary>
public static class TenantAssertionMiddlewareExtensions
{
    public static IApplicationBuilder UseLearnStackTenantAssertions(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantAssertionMiddleware>();
    }
}
