using System.Net;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Contract;

public sealed class OpenApiContractTests : IClassFixture<DevelopmentWebApplicationFactory>
{
    private static readonly Uri OpenApiDocumentPath = new("/openapi/v1.json", UriKind.Relative);

    private readonly DevelopmentWebApplicationFactory _factory;

    public OpenApiContractTests(DevelopmentWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OpenApi_WhenRequested_ReturnsDocument()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(OpenApiDocumentPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
