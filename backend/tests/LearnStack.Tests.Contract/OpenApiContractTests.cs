using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Contract;

public sealed class OpenApiContractTests : IClassFixture<OpenApiContractTests.Factory>
{
    private readonly Factory _factory;

    public OpenApiContractTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OpenApi_WhenRequested_ReturnsDocument()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// WebApplicationFactory inherits `ASPNETCORE_ENVIRONMENT` from the test
    /// host process, which defaults to `Production` under `dotnet test`
    /// (launchSettings.json is only read by `dotnet run`). `Program.cs` gates
    /// `MapOpenApi()` on `IsDevelopment()`, so without this override the
    /// endpoint would 404 in CI.
    /// </summary>
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
        }
    }
}
