using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Tenancy;

/// <summary>
/// What <see cref="TenantResolverMiddleware"/> writes, what it restores, and what an
/// anonymous request costs.
/// </summary>
/// <remarks>
/// Driven against the middleware directly. The accessor's containment is the thing
/// under test, and a host test observes it from a different execution context —
/// where an <c>AsyncLocal</c> written inside the request is invisible whether or not
/// it was restored, which would make the assertion pass for the wrong reason.
/// </remarks>
public sealed class TenantResolverMiddlewareTests
{
    private static readonly TenantId Tenant =
        TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-00000000a001"));

    private static readonly OrganizationId Organization =
        OrganizationId.From(Guid.Parse("018f4d40-0000-7000-8000-0000000000a1"));

    [Fact]
    public async Task A_Tenant_Host_Resolves_Under_The_Host_Only_Ceiling()
    {
        var accessor = new StaticTenantContextAccessor(null);
        ITenantContext? seenByTheHandler = null;

        await Invoke(accessor, HostClassification.ForResolution(
            "school.example.com", new HostResolution(Tenant, null)),
            onNext: () => seenByTheHandler = accessor.Current);

        seenByTheHandler.Should().NotBeNull();
        seenByTheHandler!.IsResolved.Should().BeTrue();
        seenByTheHandler.TenantId.Should().Be(Tenant);
        seenByTheHandler.Origin.Should().Be(TenantContextOrigin.HostOnly);
    }

    [Fact]
    public async Task A_Platform_Host_Runs_On_The_Unresolved_Context_And_Is_Not_Refused()
    {
        // Matrix rows 13 and 15. "No tenant" and "refused" are different outcomes and
        // only the second is an error — the pipeline decides what may run without
        // one, which is where [AllowsUnresolvedTenantContext] lives.
        var accessor = new StaticTenantContextAccessor(null);
        ITenantContext? seenByTheHandler = null;
        var reached = false;

        var context = await Invoke(accessor, HostClassification.Platform("app.learnstack.dev"),
            onNext: () =>
            {
                reached = true;
                seenByTheHandler = accessor.Current;
            });

        reached.Should().BeTrue("a platform host is served, not refused");
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        seenByTheHandler.Should().BeSameAs(UnresolvedTenantContext.Instance,
            "written explicitly — 'nothing wrote to it' is the assumption a "
            + "save-and-restore protocol exists to stop relying on");
    }

    [Fact]
    public async Task The_Accessor_Is_Restored_On_The_Way_Out()
    {
        // AsyncLocal flows forward. A value left behind here reaches whatever
        // continues on this execution context — with a tenant that no longer has a
        // request behind it.
        var sentinel = new StubContext();
        var accessor = new StaticTenantContextAccessor(sentinel);

        await Invoke(accessor, HostClassification.ForResolution(
            "school.example.com", new HostResolution(Tenant, Organization)),
            onNext: () => accessor.Current.Should().NotBeSameAs(sentinel));

        accessor.Current.Should().BeSameAs(sentinel);
    }

    [Fact]
    public async Task The_Accessor_Is_Restored_Even_When_The_Handler_Throws()
    {
        // The restore is in a finally and this is what proves it. Without one, an
        // exception on any request leaves that request's tenant on the accessor for
        // the next thing to read.
        var sentinel = new StubContext();
        var accessor = new StaticTenantContextAccessor(sentinel);

        var act = async () => await Invoke(
            accessor,
            HostClassification.ForResolution("school.example.com", new HostResolution(Tenant, null)),
            onNext: () => throw new InvalidOperationException("the handler failed"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        accessor.Current.Should().BeSameAs(sentinel);
    }

    [Fact]
    public async Task An_Unclassified_Request_Is_Passed_Through_Untouched()
    {
        // /healthz, /openapi and the Hub's /api/internal/* surface, whose tenant
        // comes from the envelope's path segment rather than from a host. Keyed off
        // the feature and not off a second path predicate: inventing a host signal
        // for a request classification declined to classify would be a second
        // resolution authority.
        var sentinel = new StubContext();
        var accessor = new StaticTenantContextAccessor(sentinel);
        var reached = false;

        await Invoke(accessor, classification: null, onNext: () =>
        {
            reached = true;
            accessor.Current.Should().BeSameAs(sentinel, "nothing here has a host to resolve");
        });

        reached.Should().BeTrue();
    }

    [Fact]
    public async Task An_Anonymous_Request_Consults_Neither_Port()
    {
        // Rows 2 and 3 are decided by the host alone. Both ports cost a PostgreSQL
        // transaction each, on the pre-authentication path, so calling either
        // unconditionally would put two round trips on every anonymous page load.
        //
        // The ORDER of the two — membership first, so a row-14 attempt never
        // announces a caller-supplied, unconfirmed tenant id through
        // set_config('app.tenant_id', …) — is not observable here and is not
        // asserted here: it needs a claim, and there is no UseAuthentication until
        // Phase 02b. Saying so is better than a case that passes because nothing
        // reached the branch.
        var validator = new CountingValidator();
        var memberships = new CountingMemberships();

        await Invoke(
            new StaticTenantContextAccessor(null),
            HostClassification.ForResolution("branch.example.com", new HostResolution(Tenant, Organization)),
            onNext: () => { },
            validator,
            memberships);

        validator.Calls.Should().Be(0);
        memberships.Calls.Should().Be(0);
    }

    private static async Task<DefaultHttpContext> Invoke(
        ITenantContextAccessor accessor,
        HostClassification? classification,
        Action onNext,
        IOrganizationScopeValidator? validator = null,
        ITenantMembershipReader? memberships = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/anything";

        if (classification is not null)
        {
            context.Features.Set(classification);
        }

        var middleware = new TenantResolverMiddleware(
            _ =>
            {
                onNext();
                return Task.CompletedTask;
            },
            accessor,
            NullLogger<TenantResolverMiddleware>.Instance);

        await middleware.InvokeAsync(
            context,
            validator ?? new CountingValidator(),
            memberships ?? new CountingMemberships());

        return context;
    }

    private sealed class CountingValidator : IOrganizationScopeValidator
    {
        public int Calls { get; private set; }

        public Task<bool> BelongsToTenantAsync(
            TenantId tenantId, OrganizationId organizationId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    private sealed class CountingMemberships : ITenantMembershipReader
    {
        public int Calls { get; private set; }

        public Task<bool> CoversAsync(
            UserId userId, TenantId tenantId, OrganizationId? organizationId = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    private sealed class StubContext : ITenantContext
    {
        public bool IsResolved => true;

        public TenantId TenantId => Tenant;

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => null;

        public string? CorrelationId => null;

        public string? ModuleName => null;
    }
}
