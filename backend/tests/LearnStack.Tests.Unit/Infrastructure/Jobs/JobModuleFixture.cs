using System.Reflection;
using System.Runtime.Loader;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using MediatR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

/// <summary>Real separate fixture module assemblies exercise production ownership checks without a production definition.</summary>
internal sealed class JobModuleFixture
{
    private static readonly Lazy<JobModuleFixture> Instance = new(() => new JobModuleFixture());
    internal static JobModuleFixture Shared => Instance.Value;
    private const string Prefix = "LearnStack.Modules.JobsFixture";

    private JobModuleFixture()
    {
        var contracts = Compile(Prefix + ".Application.Contracts", """
            using System;
            using LearnStack.SharedKernel.Jobs;
            namespace LearnStack.Modules.JobsFixture.Application.Contracts;
            public sealed record Parameters(Guid ResourceId) : IBackgroundJobParameters;
            public sealed record Plan(Guid ArtifactId) : IBackgroundJobPlan;
            public sealed record Outcome(Guid ArtifactId) : IBackgroundJobOutcome;
            public interface IOperation : IBackgroundJobExternalOperation<Plan, Outcome> { }
            public interface IWrongOperation : IBackgroundJobExternalOperation<Plan, Outcome> { void Extra(); }
            public interface IExtra { void Extra(); }
            public interface IInheritedWrongOperation : IBackgroundJobExternalOperation<Plan, Outcome>, IExtra { }
            """);
        var application = Compile(Prefix + ".Application", """
            using System;
            using LearnStack.SharedKernel.Identifiers;
            using LearnStack.SharedKernel.Jobs;
            using LearnStack.SharedKernel.Results;
            using LearnStack.SharedKernel.Tenancy;
            using LearnStack.Modules.JobsFixture.Application.Contracts;
            using MediatR;
            namespace LearnStack.Modules.JobsFixture.Application;
            public static class Declarations { }
            public sealed record Root(Parameters Parameters) : IRequest<Result<None>>;
            public sealed record Nested : IRequest<Result<None>>;
            public sealed record OccurrenceRoot(BackgroundJobOccurrenceParameters Parameters) : IRequest<Result<None>>;
            public sealed record Prepare(Parameters Parameters) : IRequest<Result<Plan>>;
            public sealed record Finalize(Parameters Parameters, BackgroundJobFinalization<Plan, Outcome> Finalization) : IRequest<Result<None>>;
            public sealed record WrongRoot : IRequest<Result<None>>;
            [AllowsUnresolvedTenantContext]
            public sealed record UnresolvedRoot(Parameters Parameters) : IRequest<Result<None>>;
            [PublicSurface]
            public sealed record PublicRoot(Parameters Parameters) : IRequest<Result<None>>;
            public sealed record ProvisioningRoot(Parameters Parameters) : IRequest<Result<None>>, IProvisionsTenant
            {
                public TenantId ProvisioningTenantId => TenantId.From(Guid.Parse("019712ac-1234-7000-8000-0000000000ab"));
            }
            """, contracts.Image);
        var infrastructure = Compile(Prefix + ".Infrastructure", """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using LearnStack.SharedKernel.Jobs;
            using LearnStack.Modules.JobsFixture.Application.Contracts;
            namespace LearnStack.Modules.JobsFixture.Infrastructure;
            public sealed class Operation : IOperation, IWrongOperation, IInheritedWrongOperation
            {
                public void Extra() => throw new InvalidOperationException("Forbidden extra adapter entry.");
                public Operation() => throw new InvalidOperationException("Registry constructed an adapter.");
                public Task<BackgroundJobExternalResult<Outcome>> ExecuteAsync(BackgroundJobExternalContext context, Plan plan, CancellationToken cancellationToken = default) =>
                    Task.FromResult(BackgroundJobExternalResult.Unknown<Outcome>());
                public Task<BackgroundJobExternalResult<Outcome>> ReconcileAsync(BackgroundJobExternalContext context, Plan plan, CancellationToken cancellationToken = default) =>
                    Task.FromResult(BackgroundJobExternalResult.Unknown<Outcome>());
            }
            """, contracts.Image);
        Module = new(contracts.Assembly, application.Assembly, infrastructure.Assembly);
    }

    internal BackgroundJobModuleRegistration Module { get; }
    internal Type Contract(string name) => Module.Contracts.GetType(Prefix + ".Application.Contracts." + name, throwOnError: true)!;
    internal Type Request(string name) => Module.Application.GetType(Prefix + ".Application." + name, throwOnError: true)!;
    internal Type Declaration => Request("Declarations");
    internal Type Adapter => Module.Infrastructure.GetType(Prefix + ".Infrastructure.Operation", throwOnError: true)!;

    internal BackgroundJobRequestDescriptor Ordinary(string name, BackgroundJobScope scope = BackgroundJobScope.TenantWide) =>
        new(Request(name), BackgroundJobRequestClassification.Ordinary, scope);

    internal BackgroundJobDefinitionRegistration Transactional(string key = "jobsfixture.run.v1", string root = "Root",
        BackgroundJobScope scope = BackgroundJobScope.TenantWide,
        BackgroundJobDefinitionReadiness readiness = BackgroundJobDefinitionReadiness.Active,
        IEnumerable<Type>? nested = null, string queue = "transactional") =>
        new(key, Declaration, root == "OccurrenceRoot" ? typeof(BackgroundJobOccurrenceParameters) : Contract("Parameters"),
            scope, readiness, queue, BackgroundJobProfileRegistration.Transactional(Request(root)), nested);

    internal BackgroundJobDefinitionRegistration External(string? capacityGroup = "jobsfixture.resource") =>
        new("jobsfixture.external.v1", Declaration, Contract("Parameters"), BackgroundJobScope.TenantWide,
            BackgroundJobDefinitionReadiness.Active, "external",
            BackgroundJobProfileRegistration.External(Request("Prepare"), Request("Finalize"), Contract("Plan"), Contract("Outcome"), Contract("IOperation")),
            capacityGroup: capacityGroup);

    internal BackgroundJobInfrastructureRegistry Topology() => BackgroundJobInfrastructureRegistry.Create(
        BackgroundJobInfrastructureRegistry.CreateDefault().Pools.Values,
        [new("jobsfixture.resource", Declaration)],
        [new(Contract("IOperation"), Adapter)], [Module]);

    internal BackgroundJobDefinitionRegistry Registry(params BackgroundJobDefinitionRegistration[] definitions) =>
        BackgroundJobDefinitionRegistry.Create(definitions,
            definitions.SelectMany(definition => new[] { definition.Profile.RootRequestType, definition.Profile.FinalizeRequestType }
                .OfType<Type>().Concat(definition.NestedRequestTypes)
                .Select(type => new BackgroundJobRequestDescriptor(type, BackgroundJobRequestClassification.Ordinary, definition.Scope)))
                .DistinctBy(request => request.RequestType), [Module], infrastructure: Topology());

    private static (Assembly Assembly, byte[] Image) Compile(string name, string source, params byte[][] dependencies)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(IBackgroundJobParameters).Assembly.Location).Append(typeof(IRequest<>).Assembly.Location)
            .Distinct(StringComparer.Ordinal);
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
