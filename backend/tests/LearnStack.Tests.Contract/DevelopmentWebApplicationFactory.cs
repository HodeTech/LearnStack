using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace LearnStack.Tests.Contract;

/// <summary>
/// Pins the environment to <c>Development</c>. <c>WebApplicationFactory</c>
/// inherits <c>ASPNETCORE_ENVIRONMENT</c> from the test host process, which
/// defaults to <c>Production</c> under <c>dotnet test</c>
/// (<c>launchSettings.json</c> is only read by <c>dotnet run</c>).
/// </summary>
/// <remarks>
/// This used to be load-bearing for a different reason: <c>Program.cs</c>
/// gated <c>MapOpenApi()</c> on <c>IsDevelopment()</c>, so the document 404'd
/// in CI without it. Packet 4 removed that gate — the document is the contract
/// the SDK generates from and CI diffs, so it is served in every environment —
/// and the override now exists only to keep the contract suite on one known
/// environment rather than on whatever the runner happens to set.
/// </remarks>
public sealed class DevelopmentWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
    }
}
