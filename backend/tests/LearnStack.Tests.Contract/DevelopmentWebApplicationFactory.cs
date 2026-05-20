using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace LearnStack.Tests.Contract;

/// <summary>
/// WebApplicationFactory inherits <c>ASPNETCORE_ENVIRONMENT</c> from the
/// test host process, which defaults to <c>Production</c> under
/// <c>dotnet test</c> (launchSettings.json is only read by
/// <c>dotnet run</c>). <c>Program.cs</c> gates <c>MapOpenApi()</c> on
/// <c>IsDevelopment()</c>, so without this override the endpoint would
/// 404 in CI.
/// </summary>
internal sealed class DevelopmentWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
    }
}
