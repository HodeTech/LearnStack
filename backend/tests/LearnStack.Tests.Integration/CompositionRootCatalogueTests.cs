using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Customization.Application.IntegrationEvents;
using LearnStack.SharedKernel.Audit;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The audit catalogue each composition root actually builds is the one the architecture
/// rules check.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The coverage rules in <c>LearnStack.Tests.Architecture</c> construct
/// every <see cref="IAuditCatalogSource"/> directly, by reflection — which is what lets them
/// see a source nobody listed, and also what makes them blind to the registrations. A root
/// that forgot one source's line built a catalogue missing that module, and the running system
/// refused every one of its requests as unclassified: measured by the second review of Packet 9,
/// deleting the API's <c>TenancyAuditCatalogSource</c> registration left all 1,935 cases green.
/// </para>
/// <para>
/// So each root's own <see cref="IAuditCatalog"/> is resolved from its own container and held
/// to two things: every request and concrete integration handler is registered in it,
/// including explicit Off coordinators, and its entries are exactly the entries the
/// discovered sources declare — no source missing, none extra.
/// </para>
/// </remarks>
public sealed class CompositionRootCatalogueTests : IClassFixture<ConstructionOnlyApiFactory>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CompositionRootCatalogueTests(ConstructionOnlyApiFactory factory) => _factory = factory;

    [Fact]
    public void The_API_root_builds_the_discovered_catalogue()
    {
        AssertIsTheDiscoveredCatalogue(_factory.Services.GetRequiredService<IAuditCatalog>(), "the API");
    }

    [Fact]
    public async Task The_seeder_root_builds_the_discovered_catalogue()
    {
        // Never opened: resolving the catalogue builds no connection.
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=learnstack;Username=learnstack_app");
        await using var provider = SeedComposition.Build(dataSource, null, NullLoggerFactory.Instance);

        AssertIsTheDiscoveredCatalogue(provider.GetRequiredService<IAuditCatalog>(), "the seeder");
    }

    /// <summary>
    /// <see href="../../../docs/decisions/0057-transactional-event-production-and-completion.md">ADR-0057</see>
    /// classifies each concrete handler even when Off contributes no operation entries.
    /// </summary>
    [Fact]
    public void The_root_catalogue_sweep_rejects_one_missing_Off_handler_with_unchanged_operation_entries()
    {
        var built = _factory.Services.GetRequiredService<IAuditCatalog>();
        var filtered = new MissingHandlerCatalogue(built, typeof(InitializeContentTypeDefaultHandler));

        filtered.All.Should().BeSameAs(built.All,
            "removing one Off registration leaves the entire operation catalogue unchanged; Fix: check handler identities as well as operation entries");
        BackendDiscovery.ShippedAuditSubjects().Where(subject => !filtered.TryGet(subject, out _))
            .Should().Equal([typeof(InitializeContentTypeDefaultHandler)],
                "the other consumer of TenantCreated remains registered; Fix: distinguish each concrete handler sharing an event");

        Action check = () => AssertIsTheDiscoveredCatalogue(filtered, "the missing-Off control");
        check.Should().Throw<Xunit.Sdk.XunitException>()
            .WithMessage("*InitializeContentTypeDefaultHandler*",
                "the same assertion used for both actual roots must reject the missing Off handler; Fix: retain the handler registration census");
    }

    private static void AssertIsTheDiscoveredCatalogue(IAuditCatalog built, string root)
    {
        var discovered = new AuditCatalog(BackendDiscovery.CatalogueSources());
        var shipped = BackendDiscovery.ShippedAuditSubjects();

        discovered.All.Should().NotBeEmpty("a comparison against an empty catalogue proves nothing");

        shipped.Should().Contain([typeof(InitializeContentTypeDefaultHandler), typeof(InitializeTaxonomyDefaultHandler)],
            "both useful production coordinators must be in the population; Fix: discover concrete integration handlers across every production assembly");

        shipped.Where(subject => !built.TryGet(subject, out _)).Select(subject => subject.Name)
            .Should().BeEmpty(
                "{0} refuses requests and integration handlers its catalogue does not register. Fix: register each concrete handler, including Off coordinators", root);

        foreach (var handler in new[] { typeof(InitializeContentTypeDefaultHandler), typeof(InitializeTaxonomyDefaultHandler) })
        {
            built.TryGet(handler, out var registration).Should().BeTrue(
                "{0} must classify {1} before construction; Fix: register the concrete coordinator in the actual root", root, handler.Name);
            registration.WritesNoRow.Should().BeTrue(
                "{0} classifies {1} as Off while its nested command supplies MUST intents; Fix: preserve the coordinator's explicit Off registration", root, handler.Name);
        }

        built.All.Should().BeEquivalentTo(discovered.All,
            "{0} registers every audit source the backend ships, and nothing else", root);
    }

    private sealed class MissingHandlerCatalogue(IAuditCatalog inner, Type missingHandler) : IAuditCatalog
    {
        public IReadOnlyCollection<AuditCatalogEntry> All => inner.All;

        public bool TryGet(Type requestType, out AuditRegistration registration)
        {
            if (requestType != missingHandler) return inner.TryGet(requestType, out registration);
            registration = null!;
            return false;
        }

        public bool TryGetOffPath(string operation, out AuditCatalogEntry entry) => inner.TryGetOffPath(operation, out entry);
    }
}
