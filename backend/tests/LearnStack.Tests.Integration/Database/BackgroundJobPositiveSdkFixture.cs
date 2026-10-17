using System.Reflection;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Independent genuine SDK module. The original throwing-adapter fixture is never changed.</summary>
internal sealed partial class BackgroundJobPositiveSdkFixture
{
    private const string Prefix = "LearnStack.Modules.JobsPositiveSdkFixture";
    internal const string Key = "jobspositivesdkfixture.run.v1";
    internal const string Capacity = "jobspositivesdkfixture.resource";
    private static readonly Lazy<BackgroundJobPositiveSdkFixture> Instance = new(() => new());
    internal static BackgroundJobPositiveSdkFixture Shared => Instance.Value;
    private readonly Assembly _application;
    private readonly Type _parameters;
    private readonly BackgroundJobExternalAdapterRegistration _adapter;
    internal BackgroundJobDefinitionRegistry Registry { get; }

    private BackgroundJobPositiveSdkFixture()
    {
        var contracts = BackgroundJobRuntimeFixture.Compile(Prefix + ".Application.Contracts", ContractsSource);
        var application = BackgroundJobRuntimeFixture.Compile(Prefix + ".Application", ApplicationSource, contracts.Image);
        var infrastructure = BackgroundJobRuntimeFixture.Compile(Prefix + ".Infrastructure", InfrastructureSource, contracts.Image);
        _application = application.Assembly;
        _parameters = contracts.Assembly.GetType(Prefix + ".Application.Contracts.Parameters", true)!;
        var plan = contracts.Assembly.GetType(Prefix + ".Application.Contracts.Plan", true)!;
        var outcome = contracts.Assembly.GetType(Prefix + ".Application.Contracts.Outcome", true)!;
        var port = contracts.Assembly.GetType(Prefix + ".Application.Contracts.IOperation", true)!;
        var implementation = infrastructure.Assembly.GetType(Prefix + ".Infrastructure.Operation", true)!;
        BackgroundJobModuleRegistration[] modules =
        [
            new(contracts.Assembly, _application, infrastructure.Assembly),
            new(typeof(CreateOrganizationCommand).Assembly, typeof(LearnStack.Modules.Tenancy.Application.AssemblyMarker).Assembly,
                typeof(LearnStack.Modules.Tenancy.Infrastructure.AssemblyMarker).Assembly),
        ];
        _adapter = new(port, implementation);
        var topology = BackgroundJobInfrastructureRegistry.Create(BackgroundJobInfrastructureRegistry.CreateDefault().Pools.Values,
            [new(Capacity, RequestType("Declarations"), 1)], [_adapter], modules);
        Registry = BackgroundJobDefinitionRegistry.Create(
            [new(Key, RequestType("Declarations"), _parameters, BackgroundJobScope.TenantWide, BackgroundJobDefinitionReadiness.Active,
                "external", BackgroundJobProfileRegistration.External(RequestType("Prepare"), RequestType("Finalize"), plan, outcome, port),
                [typeof(CreateOrganizationCommand)], Capacity)],
            BackgroundJobDefinitionRegistry.CreateEmpty().All.Concat(new[]
            {
                new BackgroundJobRequestDescriptor(RequestType("Submit"), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.TenantWide),
                new BackgroundJobRequestDescriptor(RequestType("Prepare"), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.TenantWide),
                new BackgroundJobRequestDescriptor(RequestType("Finalize"), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.TenantWide),
                new BackgroundJobRequestDescriptor(typeof(CreateOrganizationCommand), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.TenantWide),
            }), modules, infrastructure: topology);
    }

    internal Type RequestType(string name) => _application.GetType(Prefix + ".Application." + name, true)!;
    internal object Parameters(Guid resource, OrganizationId prepared, OrganizationId finalized) =>
        Activator.CreateInstance(_parameters, resource, prepared, finalized)!;

    internal async Task<Result<BackgroundJobId>> SubmitAsync(IServiceProvider services, object parameters)
    {
        await using var scope = services.CreateAsyncScope();
        return (Result<BackgroundJobId>)(await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(Activator.CreateInstance(RequestType("Submit"), parameters)!))!;
    }

    internal WebApplicationFactory<Program> Host(DisposableSchemaDatabase database, BackgroundJobProviderSettings provider,
        BackgroundJobPositiveProviderConstructionProbe? construction = null, Action<IServiceCollection>? configure = null,
        int? applicationPoolSize = null, BackgroundJobPositiveReconcileProbe? reconcileProbe = null) =>
        new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Default", applicationPoolSize is { } maximum
                ? new NpgsqlConnectionStringBuilder(database.AppConnectionString) { Pooling = true, MaxPoolSize = maximum }.ConnectionString
                : database.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", database.OutboxConnectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<BackgroundJobDefinitionRegistry>();
                services.AddSingleton(Registry);
                services.AddSingleton<ITenantContextAccessor>(new BackgroundJobFixtureAccessor(new BackgroundJobFixtureContext()));
                services.AddSingleton<IAuditCatalogSource>(new WrapperAuditSource(this));
                services.AddSingleton(provider);
                services.AddSingleton(construction ?? new BackgroundJobPositiveProviderConstructionProbe());
                services.AddSingleton(reconcileProbe ?? new BackgroundJobPositiveReconcileProbe());
                services.AddTransient<BackgroundJobPositiveProviderClient>();
                services.AddTransient(_adapter.PortType, _adapter.ImplementationType);
                foreach (var type in _application.GetTypes())
                    foreach (var contract in type.GetInterfaces().Where(candidate => candidate.IsGenericType
                        && candidate.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
                        services.AddTransient(contract, type);
                configure?.Invoke(services);
            });
        });

    private sealed class WrapperAuditSource(BackgroundJobPositiveSdkFixture fixture) : IAuditCatalogSource
    {
        public string ModuleName => "jobspositivesdkfixture";
        public void Describe(IAuditCatalogBuilder builder)
        {
            foreach (var name in new[] { "Submit", "Prepare", "Finalize" })
                typeof(IAuditCatalogBuilder).GetMethod(nameof(IAuditCatalogBuilder.Off))!
                    .MakeGenericMethod(fixture.RequestType(name)).Invoke(builder, null);
        }
    }
}

/// <summary>Test adapter constructor barrier; its real blocked constructor remains task-owned until released.</summary>
public sealed class BackgroundJobPositiveProviderConstructionProbe
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls, _denial;
    public BackgroundJobPositiveProviderConstructionProbe(bool hold = false)
    {
        if (!hold) _release.SetResult();
    }
    public Task Entered => _entered.Task;
    public int Calls => Volatile.Read(ref _calls);
    public bool ExternalDenial => Volatile.Read(ref _denial) != 0;
    public void Enter()
    {
        Interlocked.Increment(ref _calls);
        if (JobApplicationSourceOwner.IsExternal && JobApplicationSourceOwner.Current is null) Volatile.Write(ref _denial, 1);
        _entered.TrySetResult();
        _release.Task.GetAwaiter().GetResult();
    }
    public void Release() => _release.TrySetResult();
}
