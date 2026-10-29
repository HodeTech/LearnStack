using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Account;
using LearnStack.Api.Jobs;
using LearnStack.Api.Identity;
using LearnStack.Api.Messaging;
using LearnStack.Api.PublicReads;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Hosting;
using LearnStack.Tests.Integration.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>Program's actual provider and served routes complement the disk-wide declaration census.</summary>
public sealed class BackgroundJobProductionManifestTests
{
    [Fact]
    public async Task BackgroundJob_Runtime_Has_No_Production_Definitions_Or_Dashboard()
    {
        // ASPNETCORE_ENVIRONMENT remains Development to avoid production adapters;
        // the independently selected Deployment:Mode still reaches Program's early
        // composition branch. Route absence must hold for every declared mode.
        foreach (var mode in Enum.GetNames<DeploymentMode>())
        {
            await using var modeFactory = new ManifestFactory(mode: mode);
            var modeServices = modeFactory.Services;
            try
            {
                modeServices.GetRequiredService<IConfiguration>()["Deployment:Mode"].Should().Be(mode);
                AssertRoot(modeServices, modeFactory.ProductionDescriptors);
                ManifestOffenders(modeServices).Should().BeEmpty();
                AssertRoutes(modeServices);
                using var client = modeFactory.CreateClient();
                using var response = await client.GetAsync("/admin/hangfire");
                response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
            }
            finally { await CompleteUnstartedOwnersAsync(modeServices); }
        }

        await using var factory = new ManifestFactory();
        var services = factory.Services;
        try
        {
            AssertRoot(services, factory.ProductionDescriptors);
            ManifestOffenders(services).Should().BeEmpty();
            AssertRoutes(services);

            // Removing a registration must fail even if the already-built provider
            // would still resolve its cached value. Duplicates cannot hide behind last-wins DI.
            foreach (var type in RequiredServices)
            {
                var missing = factory.ProductionDescriptors.Where(descriptor => descriptor.ServiceType != type).ToArray();
                Action check = () => AssertRoot(services, missing);
                check.Should().Throw<Xunit.Sdk.XunitException>();
            }
            var hiddenDuplicate = factory.ProductionDescriptors.Append(
                ServiceDescriptor.Singleton(services.GetRequiredService<BackgroundJobDefinitionRegistry>())).ToArray();
            Action duplicate = () => AssertRoot(services, hiddenDuplicate);
            duplicate.Should().Throw<Xunit.Sdk.XunitException>();

            foreach (var owner in new[] { typeof(OutboxProcessor), typeof(DeliveryProcessor), typeof(BackgroundJobHostedService) })
            {
                var missing = factory.ProductionDescriptors.Where(descriptor => descriptor.ServiceType != typeof(IHostedService)
                    || descriptor.ImplementationFactory?.Method.ReturnType != owner).ToArray();
                Action check = () => AssertRoot(services, missing);
                check.Should().Throw<Xunit.Sdk.XunitException>();
            }
        }
        finally { await CompleteUnstartedOwnersAsync(services); }

        // Valid nonempty fixture declarations distinguish forbidden production
        // contents from invalid registry metadata; both are resolved by Program's provider.
        var fixture = BackgroundJobRecurrenceFixture.Shared;
        var definitions = fixture.Definitions();
        var recurrences = fixture.Recurrences(fixture.Manifest(
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero)));
        definitions.DefinitionKeys.Should().NotBeEmpty();
        recurrences.Keys.Should().ContainSingle();
        foreach (var includeRecurrence in new[] { false, true })
        {
            await using var dirty = new ManifestFactory(collection =>
            {
                collection.RemoveAll<BackgroundJobDefinitionRegistry>();
                collection.AddSingleton(definitions);
                if (includeRecurrence)
                {
                    collection.RemoveAll<BackgroundJobRecurrenceRegistry>();
                    collection.AddSingleton(recurrences);
                }
            });
            var dirtyServices = dirty.Services;
            try
            {
                AssertRoot(dirtyServices, dirty.ProductionDescriptors);
                var offenders = ManifestOffenders(dirtyServices);
                offenders.Should().Contain(value => value.StartsWith("definition:", StringComparison.Ordinal));
                if (includeRecurrence)
                    offenders.Should().Contain("recurrence:jobsrecurrencefixture.scan",
                        "the recurrence predicate runs independently of the already nonempty definition predicate");
            }
            finally { await CompleteUnstartedOwnersAsync(dirtyServices); }
        }

        // Real served endpoints, including a neutral name: absence is an exact
        // route/handler census, never a job/dashboard/recovery substring blacklist.
        foreach (var path in new[] { "/dashboard", "/api/v1/job-recovery", "/support" })
        {
            await using var dirty = new ManifestFactory(collection =>
                collection.AddSingleton<IStartupFilter>(new ExtraEndpoint(path)));
            var dirtyServices = dirty.Services;
            try
            {
                Action check = () => AssertRoutes(dirtyServices);
                check.Should().Throw<Xunit.Sdk.XunitException>();
            }
            finally { await CompleteUnstartedOwnersAsync(dirtyServices); }
        }
        await using var controller = new ProbeHostFixture(typeof(JobRecoveryManifestProbeController));
        Action controllerCheck = () => AssertRoutes(controller.Services);
        controllerCheck.Should().Throw<Xunit.Sdk.XunitException>();
        // Restored evaluation uses the original unchanged production host.
        ManifestOffenders(services).Should().BeEmpty();
        AssertRoutes(services);
    }

    private static readonly Type[] RequiredServices =
    [
        typeof(BackgroundJobDefinitionRegistry), typeof(BackgroundJobRecurrenceRegistry),
        typeof(IBackgroundJobDefinitionCatalog), typeof(IBackgroundJobRequestCatalog),
        typeof(BackgroundJobRuntime), typeof(BackgroundJobHostedService), typeof(BackgroundJobHealthCheck),
    ];

    private static void AssertRoot(IServiceProvider services, IReadOnlyList<ServiceDescriptor> descriptors)
    {
        foreach (var type in RequiredServices)
            descriptors.Count(descriptor => descriptor.ServiceType == type).Should().Be(1,
                "the actual production root must register exactly one {0}", type.FullName);
        var aliases = descriptors.Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType ?? descriptor.ImplementationFactory?.Method.ReturnType).ToArray();
        foreach (var owner in new[] { typeof(OutboxProcessor), typeof(DeliveryProcessor), typeof(BackgroundJobHostedService) })
            aliases.Count(type => type == owner).Should().Be(1, "the production owner alias is captured before test removal");

        var definitions = services.GetRequiredService<BackgroundJobDefinitionRegistry>();
        services.GetRequiredService<IBackgroundJobDefinitionCatalog>().Should().BeSameAs(definitions);
        services.GetRequiredService<IBackgroundJobRequestCatalog>().Should().BeSameAs(definitions);
        var framework = definitions.All.Where(item => item.Classification is BackgroundJobRequestClassification.FrameworkReadOnly
            or BackgroundJobRequestClassification.FrameworkReadWrite).ToArray();
        framework.Select(item => (item.RequestType, item.Classification, item.Scope)).Should().BeEquivalentTo(
            BackgroundJobFrameworkRequests.All.Select(item => (item.RequestType, item.Classification, item.Scope)));
        framework.Should().HaveCount(7, "the independently structural companion also pins each concrete framework identity");
        definitions.Infrastructure.Pools.Values.Select(pool => (pool.Name, pool.Workers, string.Join(",", pool.Queues)))
            .Should().BeEquivalentTo(new[] { ("transactional", 1, "transactional"), ("external", 2, "external"), ("control", 1, "control") });
        services.GetRequiredService<BackgroundJobRuntime>().Should().NotBeNull();
        services.GetRequiredService<BackgroundJobHealthCheck>().Should().NotBeNull();
        services.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key).IsValueCreated.Should().BeFalse();
        services.GetRequiredService<ApplicationDataSourceLifetime>().Source.IsValueCreated.Should().BeFalse();
    }

    private static List<string> ManifestOffenders(IServiceProvider services)
    {
        var definitions = services.GetRequiredService<BackgroundJobDefinitionRegistry>();
        var recurrences = services.GetRequiredService<BackgroundJobRecurrenceRegistry>();
        return definitions.DefinitionKeys.Select(key => "definition:" + key)
            .Concat(definitions.RetiredKeys.Select(key => "retired:" + key))
            .Concat(definitions.All.Where(item => item.Classification == BackgroundJobRequestClassification.Ordinary)
                .Select(item => "ordinary-request:" + item.RequestType.FullName))
            .Concat(recurrences.Keys.Select(key => "recurrence:" + key)).ToList();
    }

    private static void AssertRoutes(IServiceProvider services)
    {
        var actual = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(endpoint =>
        {
            var action = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
            var method = action?.MethodInfo ?? endpoint.Metadata.GetMetadata<MethodInfo>();
            return (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/') + "|"
                + string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []) + "|"
                + (method is null ? endpoint.DisplayName : method.DeclaringType?.FullName + "." + method.Name);
        }).Order(StringComparer.Ordinal).ToArray();
        var applicationRoutes = new[]
        {
            // The two authenticated reads and exact native admission add no job operator route.
            "account/site|GET|" + typeof(AccountController).FullName + ".GetSite",
            "identity/memberships|GET|" + typeof(MembershipsController).FullName + ".List",
            "identity/session-scope/admissions|POST|" + typeof(NativeSessionAdmissionController).FullName + ".Admit",
            "public/site|GET|" + typeof(PublicSiteController).FullName + ".Get",
            "public/site|HEAD|" + typeof(PublicSiteController).FullName + ".Head",
            "public/courses|GET|" + typeof(PublicEducationController).FullName + ".Catalog",
            "public/courses|HEAD|" + typeof(PublicEducationController).FullName + ".HeadCatalog",
            "public/courses/{slug}|GET|" + typeof(PublicEducationController).FullName + ".Course",
            "public/courses/{slug}|HEAD|" + typeof(PublicEducationController).FullName + ".HeadCourse",
            "public/courses/{slug}/lessons/{lessonSlug}|GET|" + typeof(PublicEducationController).FullName + ".Lesson",
            "public/courses/{slug}/lessons/{lessonSlug}|HEAD|" + typeof(PublicEducationController).FullName + ".HeadLesson",
        }.Select(route => "api/v1/" + route);
        var infrastructure = new[]
        {
            "healthz|GET|Program+<>c.<<Main>$>b__0_1",
            "openapi/{documentName:regex(^(v1)$)}.json|GET|Microsoft.AspNetCore.Builder.OpenApiEndpointRouteBuilderExtensions+<>c__DisplayClass0_0.<MapOpenApi>b__0",
            "docs/{documentName?}|GET|Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions+<>c__DisplayClass9_0.<MapScalarApiReference>b__0",
            "docs/scalar.js|GET|Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions+<>c.<MapStaticAssetsEndpoints>b__10_0",
            "docs/scalar.aspnetcore.js|GET|Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions+<>c.<MapStaticAssetsEndpoints>b__10_1",
            "docs/favicon.svg|GET|Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions+<>c.<MapStaticAssetsEndpoints>b__10_2",
        };
        actual.Should().Equal(applicationRoutes.Concat(infrastructure).Order(StringComparer.Ordinal),
            "only the approved production route/method/handler multiset is served; actual: {0}", string.Join("; ", actual));
    }

    private static async Task CompleteUnstartedOwnersAsync(IServiceProvider services)
    {
        services.GetRequiredService<BackgroundJobHostedService>().Dispose();
        services.GetRequiredService<OutboxProcessor>().Dispose();
        services.GetRequiredService<DeliveryProcessor>().Dispose();
        await services.GetRequiredService<BackgroundJobRuntime>().Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ManifestFactory(Action<IServiceCollection>? configure = null,
        string mode = nameof(DeploymentMode.Development)) : ConstructionOnlyApiFactory
    {
        private readonly string _errorDirectory = Path.Combine(Path.GetTempPath(), "learnstack-manifest-" + Guid.NewGuid().ToString("N"));
        internal ServiceDescriptor[] ProductionDescriptors { get; private set; } = [];
        protected override void ConfigureTestHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            // UseSetting is required before Program reads the minimal-host builder;
            // deferred ConfigureAppConfiguration is too late for deployment selection.
            builder.UseSetting("Deployment:Mode", mode);
            builder.UseSetting("ErrorTracking:Sentry:Dsn", "https://0123456789abcdef@example.invalid/1");
            builder.UseSetting("Authentication:Tenant:Issuer", "https://issuer.invalid/realms/learnstack");
            builder.UseSetting("ErrorTracking:LocalFile:Directory", _errorDirectory);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=localhost;Port=1;Database=learnstack;Username=learnstack_app;Maximum Pool Size=32",
                ["ConnectionStrings:OutboxDispatcher"] = "Host=localhost;Port=1;Database=learnstack;Username=learnstack_outbox_admin;Maximum Pool Size=52",
            }));
            builder.ConfigureTestServices(collection =>
            {
                configure?.Invoke(collection);
                ProductionDescriptors = collection.ToArray();
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(_errorDirectory)) Directory.Delete(_errorDirectory, recursive: true);
        }
    }

    private sealed class ExtraEndpoint(string path) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseRouting();
            app.UseEndpoints(endpoints => endpoints.MapGet(path, () => "manifest control"));
            next(app);
        };
    }
}

[Route("manifest-recovery-probe")]
public sealed class JobRecoveryManifestProbeController : LearnStack.Api.Common.ApiControllerBase, ITestOnlyController
{
    [HttpPost]
    public IActionResult Recover() => Ok();
}
