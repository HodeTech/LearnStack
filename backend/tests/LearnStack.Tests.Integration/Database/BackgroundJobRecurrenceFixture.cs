using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Separate test module manifests; no production definition or feature service is installed.</summary>
internal sealed class BackgroundJobRecurrenceFixture
{
    private const string Prefix = "LearnStack.Modules.JobsRecurrenceFixture";
    private static readonly Lazy<BackgroundJobRecurrenceFixture> Instance = new(() => new());
    internal static BackgroundJobRecurrenceFixture Shared => Instance.Value;

    private BackgroundJobRecurrenceFixture()
    {
        var contracts = BackgroundJobRuntimeFixture.Compile(Prefix + ".Application.Contracts",
            "namespace LearnStack.Modules.JobsRecurrenceFixture.Application.Contracts; public sealed class AssemblyMarker { }");
        var application = BackgroundJobRuntimeFixture.Compile(Prefix + ".Application", """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using LearnStack.SharedKernel.Jobs;
            using LearnStack.SharedKernel.Results;
            using LearnStack.Tests.Integration.Database;
            using MediatR;
            namespace LearnStack.Modules.JobsRecurrenceFixture.Application;
            public sealed class Declarations { }
            public interface InterfaceDeclarations { }
            public readonly struct StructDeclarations { }
            public sealed class GenericDeclarations<T> { }
            public sealed record TenantRoot : IRequest<Result<None>>
            {
                public static int Constructions;
                public BackgroundJobOccurrenceParameters Parameters { get; }
                public TenantRoot(BackgroundJobOccurrenceParameters parameters)
                { Parameters = parameters; Interlocked.Increment(ref Constructions); }
            }
            public sealed record OrganizationRoot : IRequest<Result<None>>
            {
                public static int Constructions;
                public BackgroundJobOccurrenceParameters Parameters { get; }
                public OrganizationRoot(BackgroundJobOccurrenceParameters parameters)
                { Parameters = parameters; Interlocked.Increment(ref Constructions); }
            }
            public sealed class TenantRootHandler : IRequestHandler<TenantRoot, Result<None>>
            {
                private readonly IBackgroundJobFixtureProbe _probe;
                private readonly IServiceProvider _services;
                public TenantRootHandler(IBackgroundJobFixtureProbe probe, IServiceProvider services)
                { _probe = probe; _services = services; probe.Constructed("recurrence:tenant"); }
                public Task<Result<None>> Handle(TenantRoot request, CancellationToken token) =>
                    _probe.ExecuteAsync("recurrence:tenant", _services, token);
            }
            public sealed class OrganizationRootHandler : IRequestHandler<OrganizationRoot, Result<None>>
            {
                private readonly IBackgroundJobFixtureProbe _probe;
                private readonly IServiceProvider _services;
                public OrganizationRootHandler(IBackgroundJobFixtureProbe probe, IServiceProvider services)
                { _probe = probe; _services = services; probe.Constructed("recurrence:organization"); }
                public Task<Result<None>> Handle(OrganizationRoot request, CancellationToken token) =>
                    _probe.ExecuteAsync("recurrence:organization", _services, token);
            }
            """);
        var infrastructure = BackgroundJobRuntimeFixture.Compile(Prefix + ".Infrastructure",
            "namespace LearnStack.Modules.JobsRecurrenceFixture.Infrastructure; public sealed class AssemblyMarker { }");
        Module = new(contracts.Assembly, application.Assembly, infrastructure.Assembly);
    }

    internal BackgroundJobModuleRegistration Module { get; }
    internal int RootConstructions => (int)Root(BackgroundJobScope.TenantWide).GetField("Constructions")!.GetValue(null)!
        + (int)Root(BackgroundJobScope.Organization).GetField("Constructions")!.GetValue(null)!;
    private Type Declaration => Module.Application.GetType(Prefix + ".Application.Declarations", true)!;
    private Type Root(BackgroundJobScope scope) => Module.Application.GetType(Prefix + ".Application."
        + (scope == BackgroundJobScope.TenantWide ? "TenantRoot" : "OrganizationRoot"), true)!;

    internal BackgroundJobDefinitionRegistry Definitions(bool pendingV1 = false, bool omitV1 = false)
    {
        var keys = omitV1 ? new[] { "jobsrecurrencefixture.run.v2" }
            : new[] { "jobsrecurrencefixture.run.v1", "jobsrecurrencefixture.run.v2", "jobsrecurrencefixture.organization.v1" };
        var definitions = keys.Select(key =>
        {
            var scope = key.Contains("organization", StringComparison.Ordinal)
                ? BackgroundJobScope.Organization : BackgroundJobScope.TenantWide;
            return new BackgroundJobDefinitionRegistration(key, Declaration, typeof(BackgroundJobOccurrenceParameters),
                scope, pendingV1 && key == "jobsrecurrencefixture.run.v1"
                    ? BackgroundJobDefinitionReadiness.Pending : BackgroundJobDefinitionReadiness.Active,
                "transactional", BackgroundJobProfileRegistration.Transactional(Root(scope)));
        }).ToArray();
        return BackgroundJobDefinitionRegistry.Create(definitions,
            definitions.Select(definition => new BackgroundJobRequestDescriptor(definition.Profile.RootRequestType,
                BackgroundJobRequestClassification.Ordinary, definition.Scope)).DistinctBy(request => request.RequestType)
                .Concat(BackgroundJobFrameworkRequests.All), [Module]);
    }

    internal BackgroundJobRecurrenceDescriptor Manifest(DateTimeOffset activation, DateTimeOffset anchor,
        long revision = 1, string key = "jobsrecurrencefixture.scan", string target = "jobsrecurrencefixture.run.v1",
        BackgroundJobRecipientPolicy policy = BackgroundJobRecipientPolicy.ServingTenants,
        TimeSpan? jitter = null, TimeSpan? period = null, Type? declarationType = null)
    {
        var registration = new BackgroundJobRecurrenceRegistration(key, declarationType ?? Declaration, revision, activation,
            new(target), BackgroundJobRecurrenceSchedule.Interval, anchor, period ?? TimeSpan.FromDays(1),
            jitter ?? TimeSpan.Zero, policy);
        var registry = BackgroundJobRecurrenceRegistry.Create([registration], Definitions(), [Module]);
        registry.TryGet(key, out var descriptor);
        return descriptor!;
    }

    internal BackgroundJobRecurrenceRegistry Recurrences(params BackgroundJobRecurrenceDescriptor[] manifests) =>
        BackgroundJobRecurrenceRegistry.Create(manifests.Select(manifest => manifest.Registration), Definitions(), [Module]);
}
