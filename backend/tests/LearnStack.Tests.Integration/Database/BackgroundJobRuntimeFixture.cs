using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Test-only observation invoked by handlers in genuine separate fixture Application assemblies.</summary>
public interface IBackgroundJobFixtureProbe
{
    void Constructed(string request);
    Task<Result<None>> ExecuteAsync(string request, IServiceProvider services, CancellationToken cancellationToken);
}

public sealed class BackgroundJobFixtureProbe : IBackgroundJobFixtureProbe
{
    private readonly List<string> _constructions = [];
    public IReadOnlyList<string> Constructions => _constructions;
    public Func<string, IServiceProvider, CancellationToken, Task<Result<None>>> Body { get; set; } =
        (_, _, _) => Task.FromResult(Result.Ok(None.Value));
    public void Constructed(string request) => _constructions.Add(request);
    public Task<Result<None>> ExecuteAsync(string request, IServiceProvider services, CancellationToken cancellationToken) =>
        Body(request, services, cancellationToken);
}

/// <summary>
/// Real, code-owned module identities with no production definitions. The host retains
/// the production API pipeline, transaction owner, application credential and audit store.
/// </summary>
internal sealed class BackgroundJobRuntimeFixture
{
    private const string Prefix = "LearnStack.Modules.JobsSubmissionFixture";
    private static readonly Lazy<BackgroundJobRuntimeFixture> Instance = new(() => new());
    private static readonly string[] RootRequests = ["TenantRoot", "OrganizationRoot", "PendingRoot", "CapacityRoot"];
    internal static BackgroundJobRuntimeFixture Shared => Instance.Value;
    internal const string TenantKey = "jobssubmissionfixture.run.v1";
    internal const string OrganizationKey = "jobssubmissionfixture.organization.v1";
    internal const string PendingKey = "jobssubmissionfixture.pending.v1";
    internal const string CapacityKey = "jobssubmissionfixture.capacity.v1";
    internal const string ExternalKey = "jobssubmissionfixture.external.v1";
    internal const string CapacityGroup = "jobssubmissionfixture.resource";
    internal const string Traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
    internal static readonly Guid Human = new("12345678-1234-7123-8123-123456789abc");

    private readonly Assembly _application;
    private readonly Assembly _foreignApplication;
    private readonly BackgroundJobModuleRegistration[] _modules;
    internal Type ParameterType { get; }
    internal Type OtherParameterType { get; }
    internal BackgroundJobDefinitionRegistry Registry { get; }

    private BackgroundJobRuntimeFixture()
    {
        var contracts = Compile(Prefix + ".Application.Contracts", """
            using System;
            using LearnStack.SharedKernel.Jobs;
            namespace LearnStack.Modules.JobsSubmissionFixture.Application.Contracts;
            public sealed record Parameters : IBackgroundJobParameters
            {
                public static Action? OnConstruction;
                public Guid ResourceId { get; init; }
                public Parameters(Guid resourceId) { ResourceId = resourceId; OnConstruction?.Invoke(); }
            }
            public sealed record OtherParameters(Guid ResourceId) : IBackgroundJobParameters;
            public sealed record Plan(Guid ArtifactId) : IBackgroundJobPlan;
            public sealed record Outcome(Guid ArtifactId) : IBackgroundJobOutcome;
            public interface IOperation : IBackgroundJobExternalOperation<Plan, Outcome> { }
            """);
        ParameterType = contracts.Assembly.GetType(Prefix + ".Application.Contracts.Parameters", true)!;
        OtherParameterType = contracts.Assembly.GetType(Prefix + ".Application.Contracts.OtherParameters", true)!;
        var application = Compile(Prefix + ".Application", ApplicationSource(), contracts.Image);
        _application = application.Assembly;
        var infrastructure = Compile(Prefix + ".Infrastructure", """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using LearnStack.SharedKernel.Jobs;
            using LearnStack.Modules.JobsSubmissionFixture.Application.Contracts;
            namespace LearnStack.Modules.JobsSubmissionFixture.Infrastructure;
            public sealed class Operation : IOperation
            {
                public Operation() => throw new InvalidOperationException("Admission constructed an external adapter.");
                public Task<BackgroundJobExternalResult<Outcome>> ExecuteAsync(BackgroundJobExternalContext context, Plan plan, CancellationToken cancellationToken = default) =>
                    Task.FromResult(BackgroundJobExternalResult.Unknown<Outcome>());
                public Task<BackgroundJobExternalResult<Outcome>> ReconcileAsync(BackgroundJobExternalContext context, Plan plan, CancellationToken cancellationToken = default) =>
                    Task.FromResult(BackgroundJobExternalResult.Unknown<Outcome>());
            }
            """, contracts.Image);
        var module = new BackgroundJobModuleRegistration(contracts.Assembly, application.Assembly, infrastructure.Assembly);

        const string foreign = "LearnStack.Modules.JobsForeignFixture";
        var foreignContracts = Compile(foreign + ".Application.Contracts", "namespace LearnStack.Modules.JobsForeignFixture.Application.Contracts; public sealed class AssemblyMarker { }");
        var foreignApplication = Compile(foreign + ".Application", """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using LearnStack.SharedKernel.Results;
            using LearnStack.Tests.Integration.Database;
            using MediatR;
            namespace LearnStack.Modules.JobsForeignFixture.Application;
            public sealed record Submit : IRequest<Result<None>>;
            public sealed class SubmitHandler : IRequestHandler<Submit, Result<None>>
            {
                private readonly IBackgroundJobFixtureProbe probe;
                private readonly IServiceProvider services;
                public SubmitHandler(IBackgroundJobFixtureProbe probe, IServiceProvider services)
                { this.probe = probe; this.services = services; probe.Constructed("Foreign"); }
                public Task<Result<None>> Handle(Submit request, CancellationToken cancellationToken) =>
                    probe.ExecuteAsync("Foreign", services, cancellationToken);
            }
            """);
        _foreignApplication = foreignApplication.Assembly;
        var foreignInfrastructure = Compile(foreign + ".Infrastructure", "namespace LearnStack.Modules.JobsForeignFixture.Infrastructure; public sealed class AssemblyMarker { }");
        var foreignModule = new BackgroundJobModuleRegistration(foreignContracts.Assembly, foreignApplication.Assembly, foreignInfrastructure.Assembly);
        var tenancyModule = new BackgroundJobModuleRegistration(typeof(CreateOrganizationCommand).Assembly,
            typeof(LearnStack.Modules.Tenancy.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Tenancy.Infrastructure.AssemblyMarker).Assembly);
        BackgroundJobModuleRegistration[] modules = [module, foreignModule, tenancyModule];
        _modules = modules;

        var topology = BackgroundJobInfrastructureRegistry.Create(
            BackgroundJobInfrastructureRegistry.CreateDefault().Pools.Values,
            [new(CapacityGroup, RequestType("Declarations"))],
            [new(contracts.Assembly.GetType(Prefix + ".Application.Contracts.IOperation", true)!,
                infrastructure.Assembly.GetType(Prefix + ".Infrastructure.Operation", true)!)], modules);
        Registry = BackgroundJobDefinitionRegistry.Create(
            [Definition(TenantKey, "TenantRoot"), Definition(OrganizationKey, "OrganizationRoot", BackgroundJobScope.Organization),
             Definition(PendingKey, "PendingRoot", readiness: BackgroundJobDefinitionReadiness.Pending),
             Definition(CapacityKey, "CapacityRoot", capacityGroup: CapacityGroup),
             new(ExternalKey, RequestType("Declarations"), ParameterType, BackgroundJobScope.TenantWide,
                 BackgroundJobDefinitionReadiness.Active, "external", BackgroundJobProfileRegistration.External(
                     RequestType("Prepare"), RequestType("Finalize"),
                     contracts.Assembly.GetType(Prefix + ".Application.Contracts.Plan", true)!,
                     contracts.Assembly.GetType(Prefix + ".Application.Contracts.Outcome", true)!,
                     contracts.Assembly.GetType(Prefix + ".Application.Contracts.IOperation", true)!), capacityGroup: CapacityGroup)],
            BackgroundJobDefinitionRegistry.CreateEmpty().All.Concat(new[]
            {
                Ordinary("TenantRoot"), Ordinary("OrganizationRoot", BackgroundJobScope.Organization),
                Ordinary("PendingRoot"), Ordinary("CapacityRoot"), Ordinary("Submit"),
                Ordinary("SubmitOrganization", BackgroundJobScope.Organization), Ordinary("Nested"), Ordinary("Prepare"), Ordinary("Finalize"),
                new BackgroundJobRequestDescriptor(ForeignRequestType, BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.TenantWide),
                new BackgroundJobRequestDescriptor(typeof(CreateOrganizationCommand), BackgroundJobRequestClassification.Ordinary, BackgroundJobScope.TenantWide),
            }), modules, infrastructure: topology);
    }

    internal Type RequestType(string name) => _application.GetType(Prefix + ".Application." + name, true)!;
    internal Type ForeignRequestType => _foreignApplication.GetType("LearnStack.Modules.JobsForeignFixture.Application.Submit", true)!;
    internal object Parameters(Guid? resource = null) => Activator.CreateInstance(ParameterType, resource ?? Guid.CreateVersion7())!;
    internal object Request(string name) => Activator.CreateInstance(name == "Foreign" ? ForeignRequestType : RequestType(name))!;
    internal int RootConstructions => RootRequests.Append("Prepare")
        .Sum(name => (int)RequestType(name).GetField("Constructions")!.GetValue(null)!);

    /// <summary>Explicit capacity-test clone; shared admission canaries retain their original manifest.</summary>
    internal BackgroundJobDefinitionRegistry CapacityRegistry(int operationalCeiling = 1, LimitKey? planLimit = null)
    {
        var external = Registry.Resolve(ExternalKey).Definition!;
        if (!Registry.Infrastructure.TryGetAdapter(external.ProfileRegistration.ExternalPortType!, out var adapter))
            throw new InvalidOperationException("The shared external construction canary is missing its adapter.");
        var topology = BackgroundJobInfrastructureRegistry.Create(Registry.Infrastructure.Pools.Values,
            [new(CapacityGroup, RequestType("Declarations"), operationalCeiling, planLimit)], [adapter!], _modules);
        var definitions = Registry.DefinitionKeys.Select(key =>
        {
            var definition = Registry.Resolve(key).Definition!;
            return new BackgroundJobDefinitionRegistration(key, RequestType("Declarations"), definition.ParameterType,
                definition.Scope, definition.Readiness, definition.Queue, definition.ProfileRegistration,
                definition.NestedRequestTypes, key == OrganizationKey ? CapacityGroup : definition.CapacityGroup);
        });
        return BackgroundJobDefinitionRegistry.Create(definitions, Registry.All, _modules, infrastructure: topology);
    }

    internal WebApplicationFactory<Program> Host(DisposableSchemaDatabase database, BackgroundJobFixtureProbe probe,
        ITenantContext? context = null, Action<IServiceCollection>? configure = null, string? appConnectionString = null) =>
        new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Default", appConnectionString ?? database.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", database.OutboxConnectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<BackgroundJobDefinitionRegistry>();
                services.AddSingleton(Registry);
                services.AddSingleton<IBackgroundJobFixtureProbe>(probe);
                services.AddSingleton<ITenantContextAccessor>(new BackgroundJobFixtureAccessor(context ?? new BackgroundJobFixtureContext()));
                services.AddSingleton<IAuditCatalogSource>(new FixtureAuditSource(this));
                foreach (var assembly in new[] { _application, _foreignApplication })
                    foreach (var type in assembly.GetTypes())
                        foreach (var contract in type.GetInterfaces().Where(candidate => candidate.IsGenericType
                                     && candidate.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)))
                            services.AddTransient(contract, type);
                configure?.Invoke(services);
            });
        });

    internal async Task<Result<None>> SendAsync(IServiceProvider services, string request = "Submit") =>
        (Result<None>)(await services.GetRequiredService<ISender>().Send(Request(request)))!;

    internal Task<BackgroundJobId> EnqueueAsync(IServiceProvider services, string key = TenantKey,
        object? parameters = null, DateTimeOffset? dueAt = null)
    {
        parameters ??= Parameters();
        var parameterType = parameters.GetType();
        var typedKey = Activator.CreateInstance(typeof(BackgroundJobKey<>).MakeGenericType(parameterType), key)!;
        var method = typeof(IBackgroundJobScheduler).GetMethod(dueAt.HasValue ? "ScheduleAsync" : "EnqueueAsync")!
            .MakeGenericMethod(parameterType);
        return (Task<BackgroundJobId>)method.Invoke(services.GetRequiredService<IBackgroundJobScheduler>(),
            dueAt.HasValue ? [typedKey, parameters, dueAt.Value, CancellationToken.None] : [typedKey, parameters, CancellationToken.None])!;
    }

    private BackgroundJobDefinitionRegistration Definition(string key, string root,
        BackgroundJobScope scope = BackgroundJobScope.TenantWide,
        BackgroundJobDefinitionReadiness readiness = BackgroundJobDefinitionReadiness.Active, string? capacityGroup = null) =>
        new(key, RequestType("Declarations"), ParameterType, scope, readiness, "transactional",
            BackgroundJobProfileRegistration.Transactional(RequestType(root)),
            nestedRequestTypes: scope == BackgroundJobScope.TenantWide ? [RequestType("Nested"), typeof(CreateOrganizationCommand)] : [],
            capacityGroup: capacityGroup);

    private BackgroundJobRequestDescriptor Ordinary(string name, BackgroundJobScope scope = BackgroundJobScope.TenantWide) =>
        new(RequestType(name), BackgroundJobRequestClassification.Ordinary, scope);

    private static string ApplicationSource()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using LearnStack.SharedKernel.Results;
            using LearnStack.SharedKernel.Jobs;
            using LearnStack.SharedKernel.Tenancy;
            using LearnStack.Modules.JobsSubmissionFixture.Application.Contracts;
            using LearnStack.Tests.Integration.Database;
            using MediatR;
            namespace LearnStack.Modules.JobsSubmissionFixture.Application;
            public sealed class Declarations { }
            public sealed record Submit : IRequest<Result<None>>;
            public sealed record SubmitOrganization : IRequest<Result<None>>;
            public sealed record Nested : IRequest<Result<None>>;
            [PublicSurface] public sealed record PublicSubmit : IRequest<Result<None>>;
            public sealed record Prepare : IRequest<Result<Plan>>
            {
                public static int Constructions;
                public Parameters Parameters { get; init; }
                public Prepare(Parameters parameters) { Parameters = parameters; Interlocked.Increment(ref Constructions); }
            }
            public sealed record Finalize(Parameters Parameters, BackgroundJobFinalization<Plan, Outcome> Finalization) : IRequest<Result<None>>;
            """;
        foreach (var root in RootRequests)
            source += $$"""
                public sealed record {{root}} : IRequest<Result<None>>
                {
                    public static int Constructions;
                    public Parameters Parameters { get; init; }
                    public {{root}}(Parameters parameters) { Parameters = parameters; Interlocked.Increment(ref Constructions); }
                }
                """;
        foreach (var request in new[] { "Submit", "SubmitOrganization", "Nested", "PublicSubmit", "TenantRoot", "OrganizationRoot", "PendingRoot", "CapacityRoot" })
            source += $$"""
                public sealed class {{request}}Handler : IRequestHandler<{{request}}, Result<None>>
                {
                    private readonly IBackgroundJobFixtureProbe probe;
                    private readonly IServiceProvider services;
                    public {{request}}Handler(IBackgroundJobFixtureProbe probe, IServiceProvider services)
                    { this.probe = probe; this.services = services; probe.Constructed("{{request}}"); }
                    public Task<Result<None>> Handle({{request}} request, CancellationToken cancellationToken) =>
                        probe.ExecuteAsync("{{request}}", services, cancellationToken);
                }
                """;
        return source;
    }

    private sealed class FixtureAuditSource(BackgroundJobRuntimeFixture fixture) : IAuditCatalogSource
    {
        public string ModuleName => "jobssubmissionfixture";
        public void Describe(IAuditCatalogBuilder builder)
        {
            foreach (var request in fixture._application.GetTypes().Concat(fixture._foreignApplication.GetTypes())
                         .Where(type => type.GetInterfaces().Any(contract => contract.IsGenericType
                             && contract.GetGenericTypeDefinition() == typeof(IRequest<>))))
                typeof(IAuditCatalogBuilder).GetMethod(nameof(IAuditCatalogBuilder.Off))!
                    .MakeGenericMethod(request).Invoke(builder, null);
        }
    }

    internal static (Assembly Assembly, byte[] Image) Compile(string name, string source, params byte[][] dependencies)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(IBackgroundJobParameters).Assembly.Location).Append(typeof(IRequest<>).Assembly.Location)
            .Append(typeof(IBackgroundJobFixtureProbe).Assembly.Location).Distinct(StringComparer.Ordinal);
        var references = paths.Select(path => MetadataReference.CreateFromFile(path)).Cast<MetadataReference>()
            .Concat(dependencies.Select(image => MetadataReference.CreateFromImage(image)));
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        if (!result.Success) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
        var image = output.ToArray();
        using var input = new MemoryStream(image);
        return (AssemblyLoadContext.Default.LoadFromStream(input), image);
    }
}

internal sealed class BackgroundJobFixtureAccessor(ITenantContext? context) : ITenantContextAccessor
{
    private readonly AsyncLocal<Slot?> _current = new();
    public ITenantContext? Current
    {
        get => _current.Value is { } slot ? slot.Context : context;
        set => _current.Value = new(value);
    }
    private sealed record Slot(ITenantContext? Context);
}

internal sealed class BackgroundJobFixtureContext(Guid? organization = null, Guid? tenant = null) : ITenantContext
{
    public TenantId TenantId { get; set; } = TenantId.From(tenant ?? SchemaFixture.TenantA);
    public OrganizationId? OrganizationId { get; set; } = organization is { } value ? LearnStack.SharedKernel.Identifiers.OrganizationId.From(value) : null;
    public UserId? UserId { get; set; } = LearnStack.SharedKernel.Identifiers.UserId.From(BackgroundJobRuntimeFixture.Human);
    public UserId? CausalActorUserId { get; set; }
    public string? CorrelationId => Activity.Current?.Id;
    public bool IsResolved => true;
    public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
    public HostScope? HostScope => null;
    public string? ModuleName => "untrusted_ambient_module";
}
