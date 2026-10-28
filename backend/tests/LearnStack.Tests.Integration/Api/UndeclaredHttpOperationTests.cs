using System.Net;
using System.Reflection;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Api;

/// <summary>The actual Program refuses a matched undeclared anonymous action before executing its handler.</summary>
[Collection(TenantAuthenticationDiagnosticsTestGroup.Name)]
public sealed class UndeclaredHttpOperationTests
{
    [Fact]
    public async Task Actual_Program_refuses_an_undeclared_controller_for_anonymous_and_normalized_signed_requests()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var factory = new UndeclaredFactory(issuer);
        using var client = factory.CreateClient();
        var endpoint = Assert.Single(factory.Services.GetRequiredService<EndpointDataSource>().Endpoints,
            candidate => candidate.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType() == typeof(UndeclaredHttpProbeController));
        Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.Null(endpoint.Metadata.GetMetadata<OperationPolicyAttribute>());
        UndeclaredHttpProbeController.Reset();

        using var anonymous = await TenantAuthenticationHttpRequest.Send(client,
            TenantAuthenticationReconciliationHost.TenantHost, UndeclaredHttpProbeController.Path);
        await TenantAuthenticationHttpTests.Problem(anonymous, HttpStatusCode.NotFound, "not_found");
        using var signed = await TenantAuthenticationHttpRequest.Send(client,
            TenantAuthenticationReconciliationHost.TenantHost, UndeclaredHttpProbeController.Path,
            token: issuer.Token(issuer.Without("organization_id")));
        await TenantAuthenticationHttpTests.Problem(signed, HttpStatusCode.Forbidden, "forbidden");

        Assert.Equal(0, UndeclaredHttpProbeController.Dispatches);
        Assert.Empty(factory.Ports.Memberships);
        Assert.False(factory.Services.GetRequiredService<Lazy<NpgsqlDataSource>>().IsValueCreated);
    }

    private sealed class UndeclaredFactory(TenantAuthenticationIssuer issuer) : ConstructionOnlyApiFactory
    {
        internal ReconciliationPorts Ports { get; } = new();

        protected override void ConfigureTestHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting(TenantAuthenticationOptions.IssuerConfigurationKey, issuer.Issuer);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ITenantIdentityBindingResolver>(new FixtureTenantIdentityBindingResolver(
                    issuer.Issuer, ["opaque-keycloak-subject"]));
                services.AddSingleton<IHostToTenantResolver>(Ports);
                services.AddSingleton<ITenantMembershipReader>(Ports);
                services.AddSingleton<IOrganizationScopeValidator>(Ports);
                services.AddSingleton<ITenantAssertionRecorder>(provider => provider.GetRequiredService<LoggingTenantAssertionRecorder>());
                services.AddControllers().ConfigureApplicationPartManager(manager => manager.ApplicationParts.Add(new ProbePart()));
            });
        }
    }

    private sealed class ProbePart : ApplicationPart, IApplicationPartTypeProvider
    {
        public override string Name => nameof(ProbePart);
        public IEnumerable<TypeInfo> Types => [typeof(UndeclaredHttpProbeController).GetTypeInfo()];
    }
}

[ApiController]
[AllowAnonymous]
[Route("undeclared-operation-proof")]
public sealed class UndeclaredHttpProbeController : ControllerBase
{
    internal const string Path = "/api/v1/undeclared-operation-proof";
    private static int _dispatches;
    internal static int Dispatches => Volatile.Read(ref _dispatches);
    internal static void Reset() => Interlocked.Exchange(ref _dispatches, 0);

    [HttpGet]
    public IActionResult Get()
    {
        Interlocked.Increment(ref _dispatches);
        return Ok("undeclared-handler-executed");
    }
}
