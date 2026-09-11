using FluentAssertions;
using LearnStack.Infrastructure.Audit;
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
/// to two things: every request type with a handler is registered in it, and its entries are
/// exactly the entries the discovered sources declare — no source missing, none extra.
/// </para>
/// </remarks>
public sealed class CompositionRootCatalogueTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CompositionRootCatalogueTests(WebApplicationFactory<Program> factory) => _factory = factory;

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

    private static void AssertIsTheDiscoveredCatalogue(IAuditCatalog built, string root)
    {
        var discovered = new AuditCatalog(BackendDiscovery.CatalogueSources());
        var shipped = BackendDiscovery.ShippedRequestTypes();

        discovered.All.Should().NotBeEmpty("a comparison against an empty catalogue proves nothing");

        shipped.Where(request => !built.TryGet(request, out _)).Select(request => request.Name)
            .Should().BeEmpty(
                "{0} refuses every request its catalogue does not register, as unclassified", root);

        built.All.Should().BeEquivalentTo(discovered.All,
            "{0} registers every audit source the backend ships, and nothing else", root);
    }
}
