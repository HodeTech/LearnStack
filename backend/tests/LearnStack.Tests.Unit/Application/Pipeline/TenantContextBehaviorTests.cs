using FluentAssertions;
using LearnStack.Application.Pipeline;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Pipeline;

/// <summary>
/// Pipeline step 4's two gates: is there a tenant context, and does it reach this
/// request type.
/// </summary>
/// <remarks>
/// <para>
/// The second gate is
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>'s
/// authority ceiling, and it is the control that makes a forged <c>Host</c> harmless:
/// with it, a forged host reaches exactly the pages that hostname already serves to
/// anyone who types it. Everything below is about the shape of those two gates,
/// because three plausible shapes are wrong and each is wrong in a different
/// direction.
/// </para>
/// <para>
/// Driven against the behavior directly. The gates are a function of the injected
/// context and the request type, both of which a unit test controls exactly; routing
/// them through a host would add a resolver and a database to observe a decision that
/// touches neither.
/// </para>
/// </remarks>
public sealed class TenantContextBehaviorTests
{
    public sealed record DummyCommand : IRequest<Result<string>>;

    [AllowsUnresolvedTenantContext]
    public sealed record ProvisioningShapedCommand : IRequest<Result<string>>;

    [PublicSurface]
    public sealed record AnonymousReadShapedQuery : IRequest<Result<string>>;

    // ---- gate 1: is there a context at all -----------------------------------

    [Fact]
    public async Task Short_Circuits_When_Context_Unresolved()
    {
        var (result, called) = await RunAsync<DummyCommand>(UnresolvedTenantContext.Instance);

        called.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("tenant_mismatch");
    }

    [Fact]
    public async Task Unresolved_Context_Runs_A_Marked_Request()
    {
        // Rows 13 and 15: a platform host resolves no tenant, and the narrow set of
        // provisioning and platform-admin commands is what may still run there.
        var (result, called) = await RunAsync<ProvisioningShapedCommand>(
            UnresolvedTenantContext.Instance);

        called.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task PublicSurface_Is_Not_A_Permit_To_Run_Unresolved()
    {
        // The two markers answer different questions and neither implies the other.
        // [PublicSurface] says which origins may reach a type; it says nothing about
        // running with no tenant at all.
        var (result, called) = await RunAsync<AnonymousReadShapedQuery>(
            UnresolvedTenantContext.Instance);

        called.Should().BeFalse();
        result.Error!.Code.Should().Be("tenant_mismatch");
    }

    // ---- gate 2: does the context reach this request -------------------------

    [Fact]
    public async Task HostOnly_Reaches_A_PublicSurface_Request()
    {
        var (result, called) = await RunAsync<AnonymousReadShapedQuery>(
            Resolved(TenantContextOrigin.HostOnly));

        called.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HostOnly_Is_Refused_On_An_Unmarked_Request()
    {
        // The ceiling itself. An anonymous page load carries a real, resolved tenant
        // context — the host named it — and that context must reach only the pages the
        // hostname already serves.
        var (result, called) = await RunAsync<DummyCommand>(
            Resolved(TenantContextOrigin.HostOnly));

        called.Should().BeFalse();
        result.Error!.Code.Should().Be("not_found",
            "the refusal must be indistinguishable from an unresolvable host, and "
            + "tenant_mismatch is the authenticated code");
        result.Error.Should().BeSameAs(TenantContextFactory.Refused,
            "one Error for one wire-visible refusal makes the parity a compile-time "
            + "fact rather than two tests agreeing by coincidence");
    }

    [Fact]
    public async Task AllowsUnresolved_Is_Not_An_Exemption_From_The_Ceiling()
    {
        // The wrong shape this kills: a single fused gate where the marker skips both
        // checks. A provisioning command addressed to a live tenant's own hostname
        // resolves HostOnly, and under the fused shape an anonymous caller who typed
        // that hostname reaches provisioning.
        var (result, called) = await RunAsync<ProvisioningShapedCommand>(
            Resolved(TenantContextOrigin.HostOnly));

        called.Should().BeFalse();
        result.Error!.Code.Should().Be("not_found");
    }

    [Theory]
    [InlineData(TenantContextOrigin.HostAndClaim)]
    [InlineData(TenantContextOrigin.ClaimAndMembership)]
    [InlineData(TenantContextOrigin.Ambient)]
    public async Task Authenticated_And_Ambient_Origins_Reach_An_Unmarked_Request(
        TenantContextOrigin origin)
    {
        // ADR-0036 narrows exactly one origin. What narrows an authenticated caller
        // further is authorization at step 5, not this gate — and Ambient is not a
        // judgement call at all: EventTenantContext resolves with exactly that origin,
        // so omitting it stops every integration-event consumer.
        var (result, called) = await RunAsync<DummyCommand>(Resolved(origin));

        called.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_Resolved_Context_That_States_No_Origin_Reaches_Nothing()
    {
        // The case that separates the allow-list from the negation, and the only one
        // that can: Origin is a nullable default interface member, so `Origin !=
        // HostOnly` is true for null and every other test here would still pass.
        var (result, called) = await RunAsync<DummyCommand>(new OriginlessContext());

        called.Should().BeFalse();
        result.Error!.Code.Should().Be("not_found");
    }

    [Fact]
    public async Task An_Unstated_Origin_Is_Refused_Even_On_A_Marked_Request()
    {
        // Neither marker rescues it. [AllowsUnresolvedTenantContext] governs gate 1,
        // which this context passes — it claims to be resolved — and [PublicSurface]
        // admits HostOnly, which is not what null is.
        (await RunAsync<ProvisioningShapedCommand>(new OriginlessContext())).Called
            .Should().BeFalse();
        (await RunAsync<AnonymousReadShapedQuery>(new OriginlessContext())).Called
            .Should().BeFalse();
    }

    private static async Task<(Result<string> Result, bool Called)> RunAsync<TRequest>(
        ITenantContext context)
        where TRequest : IRequest<Result<string>>, new()
    {
        var behavior = new TenantContextBehavior<TRequest, Result<string>>(context);
        var called = false;

        RequestHandlerDelegate<Result<string>> next = () =>
        {
            called = true;
            return Task.FromResult(Result.Ok("ran"));
        };

        var result = await behavior.Handle(new TRequest(), next, CancellationToken.None);
        return (result, called);
    }

    private static StatedOriginContext Resolved(TenantContextOrigin origin) => new(origin);

    private sealed class StatedOriginContext(TenantContextOrigin origin) : ITenantContext
    {
        public bool IsResolved => true;

        public TenantId TenantId { get; } =
            TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-00000000a001"));

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => null;

        public TenantContextOrigin? Origin => origin;

        public string? CorrelationId => null;

        public string? ModuleName => null;
    }

    /// <summary>A resolved context that never restated <c>Origin</c>.</summary>
    private sealed class OriginlessContext : ITenantContext
    {
        public bool IsResolved => true;

        public TenantId TenantId { get; } =
            TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-00000000a001"));

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => null;

        public string? CorrelationId => null;

        public string? ModuleName => null;
    }
}
