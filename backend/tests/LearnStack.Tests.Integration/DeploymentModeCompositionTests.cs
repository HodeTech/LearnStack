using FluentAssertions;
using LearnStack.Infrastructure.Caching;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Observability;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The composition root branches on <see cref="DeploymentMode"/>, and the two
/// modes ADR-0020 calls wired end to end — <c>Development</c> and <c>SaaS</c> —
/// are booted here rather than described.
/// </summary>
/// <remarks>
/// <para>
/// Existing coverage stops at <i>reading</i> the mode: that it has no default,
/// that a numeric string is refused. Nothing started the host in a second mode,
/// so "branching is present and exercised" rested on the branch compiling. A
/// branch that compiles can still throw at startup, register the wrong
/// implementation, or fail to resolve.
/// </para>
/// <para>
/// The other three modes are prepared seams, not supported deployments, until
/// Phase 11 builds their adapters and integration suites
/// (<see href="../../../docs/decisions/0035-demand-gated-infrastructure.md">ADR-0035</see>),
/// which is why only two are booted.
/// </para>
/// </remarks>
public sealed class DeploymentModeCompositionTests
{
    /// <summary>
    /// A DSN-shaped value. <c>SaaS</c> refuses to start without one — the
    /// error-tracking composition treats a missing DSN as a configuration
    /// failure rather than degrading silently — so supplying it is part of
    /// booting that mode, not a way around the rule.
    /// </summary>
    private const string DevelopmentShapedDsn = "https://0123456789abcdef@example.invalid/1";

    [Theory]
    [InlineData(nameof(DeploymentMode.Development))]
    [InlineData(nameof(DeploymentMode.SaaS))]
    public void The_Foundation_Ports_Resolve_To_Their_Defaults_In_Every_Wired_Mode(string mode)
    {
        // ADR-0035's claim in one assertion: the ports ship now with working
        // defaults, and the vendor adapters land on a trigger — so every mode
        // resolves the same implementations today. When Phase 11 changes that,
        // this test is where the change becomes visible.
        using var factory = For(mode);
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        services.GetRequiredService<IEventBus>().Should().BeOfType<InProcessEventBus>();
        services.GetRequiredService<ICacheService>().Should().BeOfType<InMemoryCacheService>();
        services.GetRequiredService<ISecretProvider>()
            .Should().BeOfType<ConfigurationSecretProvider>();
    }

    [Fact]
    public void Error_Tracking_Is_The_One_Port_The_Mode_Actually_Changes()
    {
        // The branching has to be observable somewhere or it is not exercised at
        // all. Error tracking is the seam that differs today: Development must
        // not egress, SaaS reports to Sentry.
        using var development = For(nameof(DeploymentMode.Development));
        using var saas = For(nameof(DeploymentMode.SaaS));

        var inDevelopment = development.Services.GetRequiredService<IErrorTrackingProvider>();
        var inSaaS = saas.Services.GetRequiredService<IErrorTrackingProvider>();

        inDevelopment.GetType().Name.Should().Be("NoOpErrorTracker");
        inSaaS.GetType().Name.Should().Be("SentryErrorTracker");
    }

    [Fact]
    public void Tenant_Context_Resolution_Forwards_Each_Access_To_The_Accessor()
    {
        using var factory = For(nameof(DeploymentMode.Development));
        using var scope = factory.Services.CreateScope();
        var accessor = factory.Services.GetRequiredService<ITenantContextAccessor>();
        var previous = accessor.Current;

        try
        {
            var first = new ResolvedContext(
                Guid.Parse("018f4d40-0000-7000-8000-000000000001"));
            var second = new ResolvedContext(
                Guid.Parse("018f4d40-0000-7000-8000-000000000002"));

            accessor.Current = first;
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Should().BeSameAs(first);

            accessor.Current = second;
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Should().BeSameAs(second,
                "a scoped factory would cache the first value for the rest of the scope");
        }
        finally
        {
            accessor.Current = previous;
        }
    }

    /// <summary>
    /// Boots the host in one mode, overriding what
    /// <c>appsettings.Development.json</c> sets.
    /// </summary>
    /// <remarks>
    /// <c>UseSetting</c>, not <c>ConfigureAppConfiguration</c>, and the
    /// difference is not stylistic. Under minimal hosting the composition root
    /// reads <c>builder.Configuration</c> while the builder is still being
    /// assembled, which is before a deferred <c>ConfigureAppConfiguration</c>
    /// callback runs — measured, an in-memory source added that way had no
    /// effect at all, and the SaaS case silently resolved the Development
    /// branch while the test still passed everything it asserted about the
    /// ports. <c>UseSetting</c> writes into the host configuration the builder
    /// itself reads.
    /// </remarks>
    private static WebApplicationFactory<Program> For(string mode) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Deployment:Mode", mode);
            builder.UseSetting("ErrorTracking:Sentry:Dsn", DevelopmentShapedDsn);
        });

    private sealed class ResolvedContext(Guid tenantId) : ITenantContext
    {
        public bool IsResolved => true;
        public Guid TenantId { get; } = tenantId;
        public Guid? OrganizationId => null;
        public LearnStack.SharedKernel.Identifiers.UserId? UserId => null;
        public string? CorrelationId => null;
        public string? ModuleName => "integration-test";
    }
}
