using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using OrganizationAggregate = LearnStack.Modules.Tenancy.Domain.Organization;

namespace LearnStack.Tests.Unit.Modules.Tenancy;

/// <summary>
/// What the two tenant-owned commands do when the context they trust is not resolved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unreachable through the pipeline, and tested anyway.</b> Neither command carries
/// <c>[AllowsUnresolvedTenantContext]</c>, so <c>TenantContextBehavior</c> refuses an
/// unresolved context three steps before either handler runs. The guards exist for the
/// wiring change that loses that behavior — and a guard no test kills is a comment, so
/// these drive the handlers directly, which is precisely the shape such a change takes.
/// </para>
/// <para>
/// <b>The assertion is the HTTP status, not the key.</b> The failure mode being prevented
/// is specific: <c>HttpStatusMap</c> is a closed table of cross-cutting codes, so a
/// module-specific key falls through it to <c>500</c> — and a fail-closed guard answering
/// <c>500</c> is the one answer it must not give. Measured: with the key changed to one of
/// this module's own, every other case in the solution still passed.
/// </para>
/// </remarks>
public sealed class TenancyCommandGuardTests
{
    private static readonly TenantId Tenant =
        TenantId.From(Guid.Parse("0199b000-0000-7000-8000-000000000001"));

    [Fact]
    public async Task Creating_an_organization_without_a_tenant_refuses_with_a_mapped_code()
    {
        var handler = Resolve<CreateOrganizationCommand, OrganizationDto>();

        var result = await handler.Handle(
            new CreateOrganizationCommand(
                OrganizationId.From(Guid.CreateVersion7()), "branch", "Branch"),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        HttpStatusMap.For(result.Error!.Code).Should().NotBe(
            StatusCodes.Status500InternalServerError,
            "a guard that fails closed must answer something the caller can read");
        result.Error.Code.Should().Be("tenant_mismatch",
            "which is what TenantContextBehavior itself returns for this condition");
    }

    [Fact]
    public async Task Mapping_a_host_without_a_tenant_refuses_with_a_mapped_code()
    {
        // The sharper of the two: this command writes the row that decides whose data an
        // anonymous request sees. A guard here that answered 500 would turn a wiring
        // mistake into an incident instead of a refusal.
        var handler = Resolve<MapHostToTenantCommand, HostMappingDto>();

        var result = await handler.Handle(
            new MapHostToTenantCommand("nowhere.example.com"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        HttpStatusMap.For(result.Error!.Code).Should().NotBe(
            StatusCodes.Status500InternalServerError);
        result.Error.Code.Should().Be("tenant_mismatch");
    }

    [Fact]
    public async Task A_host_the_deployment_reserved_is_refused_rather_than_written_inert()
    {
        // ADR-0036: a host on Tenancy:PlatformHosts classifies PlatformHost before the
        // resolver is called at all, so a platform_host_to_tenant row naming it is inert —
        // "never read, never logged, never counted". The precedence is correct; what is
        // wrong is that the losing row is SILENT, so the deployment that created one gets
        // no signal. ADR-0036 assigns the check to whichever packet builds the writer,
        // and Packet 7 is that packet.
        var handler = Resolve<MapHostToTenantCommand, HostMappingDto>(
            Tenant, reserved: "app.learnstack.dev");

        var result = await handler.Handle(
            new MapHostToTenantCommand("APP.learnstack.dev.", IsActive: true),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue(
            "the row would exist and do nothing, which is worse than a refusal");
        result.Error!.Details.Should().ContainKey(nameof(MapHostToTenantCommand.Host))
            .WhoseValue.Should().ContainSingle()
            .Which.Key.Should().Be("lockey_host_reserved");

        // The comparison is against normalized hosts, which is why the check runs after
        // the aggregate normalizes rather than on the raw request value. An uppercase,
        // trailing-dot spelling of a reserved host is the same host.
    }

    [Fact]
    public async Task A_mapped_host_stops_being_negative_cached()
    {
        // The negative cache remembers hosts that resolved to nothing. Until this packet
        // no writer of platform_host_to_tenant existed, so the TTL was the whole
        // mechanism and a host loaded once before it existed kept its 404 for the rest of
        // that window — which is precisely what a developer meets after seeding.
        var invalidated = new RecordingInvalidator();
        var handler = Resolve<MapHostToTenantCommand, HostMappingDto>(
            Tenant, invalidator: invalidated);

        var result = await handler.Handle(
            new MapHostToTenantCommand("TÜRKÇE.example.com", IsActive: true, IsPubliclyLive: true),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        invalidated.Hosts.Should().ContainSingle()
            .Which.Should().Be("xn--trke-2oa7j.example.com",
                "the cache is keyed on what a request's Host header normalizes to, so "
                + "forgetting any other spelling forgets nothing");
    }

    /// <summary>
    /// The handler alone, with no pipeline in front of it.
    /// </summary>
    /// <remarks>
    /// Resolved rather than constructed: both handlers are <c>internal</c>, and widening
    /// that to reach them from a test would be a visibility change made for the test's
    /// convenience. Resolution also proves they are discoverable by the assembly scan the
    /// composition root relies on.
    /// </remarks>
    private static IRequestHandler<TCommand, Result<TResponse>> Resolve<TCommand, TResponse>(
        TenantId? tenant = null,
        string? reserved = null,
        IHostResolutionInvalidator? invalidator = null)
        where TCommand : IRequest<Result<TResponse>>
    {
        var services = new ServiceCollection();
        services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(
            typeof(ITenantWriteStore).Assembly));
        services.AddSingleton<IClock>(new FixedClock(
            new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<ITenantContext>(
            tenant is { } resolved ? new ResolvedContext(resolved) : UnresolvedTenantContext.Instance);
        services.AddSingleton<IOrganizationWriteStore, RefusingStore>();
        services.AddSingleton<IOrganizationScopeValidator, RefusingStore>();
        services.AddSingleton<IPlatformHostMappingStore>(new AcceptingHostStore());
        services.AddSingleton<IReservedHostRegistry>(
            reserved is null ? NoReservedHosts.Instance : new OneReservedHost(reserved));
        services.AddSingleton(invalidator ?? NullHostResolutionInvalidator.Instance);

        return services.BuildServiceProvider()
            .GetRequiredService<IRequestHandler<TCommand, Result<TResponse>>>();
    }

    /// <summary>
    /// Every collaborator a handler could reach past its guard, each of which throws.
    /// </summary>
    /// <remarks>
    /// Throwing rather than recording: the property under test is that the guard returns
    /// before any of them is touched, and a no-op fake would let a handler that skipped
    /// the guard still produce a plausible-looking failure further down.
    /// </remarks>
    private sealed class RefusingStore
        : IOrganizationWriteStore, IPlatformHostMappingStore, IOrganizationScopeValidator
    {
        public Task AddAsync(OrganizationAggregate aggregate, CancellationToken ct = default) =>
            throw new InvalidOperationException("the guard should have returned first");

        public Task UpdateAsync(OrganizationAggregate aggregate, CancellationToken ct = default) =>
            throw new InvalidOperationException("the guard should have returned first");

        public Task AddAsync(PlatformHostMapping mapping, CancellationToken ct = default) =>
            throw new InvalidOperationException("the guard should have returned first");

        public Task<bool> BelongsToTenantAsync(
            TenantId tenantId, OrganizationId organizationId, CancellationToken ct = default) =>
            throw new InvalidOperationException("the guard should have returned first");
    }

    /// <summary>Accepts the write, so the cases past the guards can reach their subject.</summary>
    private sealed class AcceptingHostStore : IPlatformHostMappingStore
    {
        public Task AddAsync(PlatformHostMapping mapping, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class OneReservedHost(string host) : IReservedHostRegistry
    {
        public bool IsReserved(string normalizedHost) =>
            string.Equals(normalizedHost, host, StringComparison.Ordinal);
    }

    private sealed class RecordingInvalidator : IHostResolutionInvalidator
    {
        public List<string> Hosts { get; } = [];

        public Task InvalidateAsync(
            string normalizedHost, CancellationToken cancellationToken = default)
        {
            Hosts.Add(normalizedHost);
            return Task.CompletedTask;
        }
    }

    private sealed class ResolvedContext(TenantId tenantId) : ITenantContext
    {
        public bool IsResolved => true;

        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;

        public TenantId TenantId => tenantId;

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => null;

        public string? CorrelationId => null;

        public string? ModuleName => "tenancy";
    }
}
