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
/// In Packet 4 nothing resolves a tenant — <c>ITenantContext.IsResolved</c> is
/// false until Packet 7's <c>TenantResolverMiddleware</c> — so the mismatch
/// path is unreachable in traffic and is exercised by tests over a stubbed
/// context. The comparison ships now because the binding does, and a binding
/// whose rule arrives three packets later is a binding nobody wrote the rule
/// for.
/// </para>
/// </remarks>
public sealed class TenantAssertionMiddleware(RequestDelegate next)
{
    public const string TenantHeaderName = "X-Tenant-Id";
    public const string OrganizationHeaderName = "X-Organization-Id";

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenantContext,
        ITenantAssertionRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(recorder);

        if (!TryReadAssertion(context, TenantHeaderName, out var assertedTenant)
            || !TryReadAssertion(context, OrganizationHeaderName, out var assertedOrganization))
        {
            // Malformed or repeated. 400 rather than 404, because this is not
            // a claim about a tenant at all — it is a value that cannot be one.
            // Refused rather than resolved by first-or-last: a header present
            // twice is the classic confusion bug, and whichever end you pick,
            // some topology makes it the attacker's.
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
                tenantContext.TenantId,
                mismatch.Value.Dimension,
                mismatch.Value.Asserted,
                context.User.Identity?.IsAuthenticated == true));

            // 404, not 403: saying "wrong tenant" confirms the other tenant
            // exists. The code differs by caller — `tenant_mismatch` for an
            // authenticated one, `not_found` for an anonymous one — so the
            // header adds no bit an anonymous client could not already get by
            // retrying without it.
            await WriteAsync(context, StatusCodes.Status404NotFound);
            return;
        }

        await next(context);
    }

    private static (TenantAssertionDimension Dimension, Guid Asserted)? Mismatch(
        ITenantContext tenantContext, Guid? assertedTenant, Guid? assertedOrganization)
    {
        if (assertedTenant is { } tenant && tenant != tenantContext.TenantId)
        {
            return (TenantAssertionDimension.Tenant, tenant);
        }

        if (assertedOrganization is { } organization
            && organization != tenantContext.OrganizationId)
        {
            return (TenantAssertionDimension.Organization, organization);
        }

        return null;
    }

    /// <summary>
    /// Reads one assertion. Absent is fine; malformed or repeated is not.
    /// </summary>
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
