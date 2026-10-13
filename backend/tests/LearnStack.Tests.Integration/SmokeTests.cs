using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed class SmokeTests : IClassFixture<ConstructionOnlyApiFactory>
{
    private static readonly Uri HealthzPath = new("/healthz", UriKind.Relative);

    private readonly WebApplicationFactory<Program> _factory;

    public SmokeTests(ConstructionOnlyApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Healthz_WhenCalled_ReturnsOk()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(HealthzPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
