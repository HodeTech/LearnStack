using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Identity;
using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.Modules.Identity.Application.Federation;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identity;
using MediatR;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// ADR-0099 Step 2: the provider and egress ports are held only by the one federation runner, the federation
/// controller reaches the application only through that runner, and every federation request is classified.
/// </summary>
public sealed class FederationApplicationConventionTests
{
    private static readonly string[] PortTokens = ["IFederationProvider", "IFederationEgressFetcher"];

    /// <summary>The only production sources that may name the ports: their declarations, the runner and the fail-closed registration.</summary>
    private static readonly string[] AllowedPortSources =
    [
        "LearnStack.SharedKernel/Identity/IFederationProvider.cs",
        "LearnStack.SharedKernel/Identity/IFederationEgressFetcher.cs",
        "Modules/Identity/LearnStack.Modules.Identity.Application/Federation/FederationOperationRunner.cs",
        "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Bindings/IdentityFoundationRegistration.cs",
    ];

    [Fact]
    public void Federation_Provider_Port_Is_Held_Only_By_The_Runner()
    {
        var sources = Directory.EnumerateFiles(SourceScan.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(SourceScan.SourceRoot, path).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText);
        PortOffenders(sources).Should().BeEmpty(
            "Fix: only FederationOperationRunner calls the federation provider and egress fetcher; the Step 3 adapter is admitted with its own entry (ADR-0099)");
        var holders = ProductionAssemblies.All().SelectMany(SafeTypes)
            .Where(type => type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => parameter.ParameterType == typeof(IFederationProvider) || parameter.ParameterType == typeof(IFederationEgressFetcher)))
            .Select(type => type.FullName).ToArray();
        holders.Should().Equal([typeof(FederationOperationRunner).FullName], "no other production type takes a federation port by injection");
    }

    [Fact]
    public void The_port_holder_detector_refuses_a_planted_holder()
    {
        var planted = AllowedPortSources.ToDictionary(path => path, _ => "IFederationProvider");
        planted["LearnStack.Api/Identity/Planted.cs"] = "public sealed class Planted(IFederationProvider provider);";
        planted["LearnStack.Api/Identity/Clean.cs"] = "public sealed class Clean;";
        PortOffenders(planted).Should().Equal("LearnStack.Api/Identity/Planted.cs");
    }

    [Fact]
    public void Federation_Controller_Dispatches_Only_Through_The_Runner()
    {
        ControllerOffenders(typeof(FederationController)).Should().BeEmpty(
            "Fix: the federation controller stays thin and hands every operation to FederationOperationRunner, never to ISender or a port");
        typeof(FederationOperationRunner).GetInterfaces().Should().NotContain(type => type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(IRequestHandler<,>), "the runner is not a MediatR handler and opens no transaction");
        typeof(FederationOperationRunner).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType)
            .Should().Contain(typeof(ISender), "every committed step is a top-level request in its own frame");
    }

    [Fact]
    public void The_controller_detector_refuses_a_sender_or_port_holding_controller()
    {
        ControllerOffenders(typeof(PlantedSenderController)).Should().NotBeEmpty();
        ControllerOffenders(typeof(PlantedPortController)).Should().NotBeEmpty();
    }

    [Fact]
    public void Every_Federation_Request_Has_An_Audit_Catalogue_Entry()
    {
        var catalogue = AuditCatalogDiscovery.Catalogue();
        var requests = typeof(BeginConfigureFederationCommand).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(BeginConfigureFederationCommand).Namespace && !type.IsAbstract
                && type.GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IRequest<>)))
            .ToArray();
        requests.Should().HaveCount(14, "six Begins, Resume, Complete, Enable, Activate, the test record, Reconcile and two reads");
        foreach (var request in requests)
        {
            catalogue.TryGet(request, out var registration).Should().BeTrue("Fix: classify " + request.Name + " in IdentityAuditCatalogSource");
            if (request == typeof(GetFederationConnectionQuery) || request == typeof(GetPublicSignInQuery))
            {
                registration!.WritesNoRow.Should().BeTrue(request.Name + " is an explicit Off read: no personal data");
                continue;
            }
            registration!.WritesNoRow.Should().BeFalse();
            registration.Entries.Should().ContainSingle().Which.Should().Match<AuditCatalogEntry>(entry =>
                entry.OperationClass == OperationClass.Must && entry.Operation.StartsWith("identity.federation.", StringComparison.Ordinal),
                request.Name + " is MUST under identity.federation.*");
        }
    }

    /// <summary>The two raw SQL readers of the registry; neither can use the EF <c>Live</c> helper (Step 2 review S-D).</summary>
    private static readonly string[] RawLiveReaders =
    [
        "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Persistence/FederationConnectionStore.cs",
        "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Persistence/PublicSignInReader.cs",
    ];

    [Fact]
    public void Raw_Federation_Reads_Share_The_One_Pinned_Live_Predicate()
    {
        // Pinned text: a change here is a deliberate tombstone-semantics change, made once for both raw readers.
        FederationConnectionReads.LiveSql.Should().Be("state <> 'Deleted' AND deleted_at IS NULL");
        var sources = Directory.EnumerateFiles(SourceScan.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(SourceScan.SourceRoot, path).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText);
        RawLiveOffenders(sources).Should().BeEmpty(
            "Fix: every raw SELECT from federation_connections interpolates FederationConnectionReads.LiveSql and spells no tombstone predicate of its own");
        sources.Keys.Where(path => sources[path].Contains("FROM public.federation_connections", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Should().Equal(RawLiveReaders, "the census of raw registry readers is closed");
    }

    [Fact]
    public void The_raw_live_predicate_detector_refuses_a_reader_with_its_own_predicate()
    {
        const string Shared = "WHERE tenant_id=@tenant AND {FederationConnectionReads.LiveSql}";
        var clean = new Dictionary<string, string>
        {
            [RawLiveReaders[0]] = "SELECT id FROM public.federation_connections " + Shared + " FOR NO KEY UPDATE",
            [RawLiveReaders[1]] = "SELECT alias FROM public.federation_connections " + Shared + " AND state='Active'",
        };
        RawLiveOffenders(clean).Should().BeEmpty();
        var own = new Dictionary<string, string>(clean)
        {
            [RawLiveReaders[1]] = "SELECT alias FROM public.federation_connections WHERE tenant_id=@tenant AND state='Active' AND deleted_at IS NULL",
        };
        RawLiveOffenders(own).Should().Equal(RawLiveReaders[1]);
        var both = new Dictionary<string, string>(clean)
        {
            [RawLiveReaders[0]] = clean[RawLiveReaders[0]] + " AND deleted_at IS NULL",
        };
        RawLiveOffenders(both).Should().Equal(RawLiveReaders[0]);
    }

    private static List<string> RawLiveOffenders(IReadOnlyDictionary<string, string> sources) =>
        [.. sources.Where(pair => pair.Value.Contains("FROM public.federation_connections", StringComparison.Ordinal)
                && (!pair.Value.Contains("{FederationConnectionReads.LiveSql}", StringComparison.Ordinal)
                    || pair.Value.Contains("deleted_at", StringComparison.Ordinal)
                    || pair.Value.Contains("'Deleted'", StringComparison.Ordinal)))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    private static List<string> PortOffenders(IReadOnlyDictionary<string, string> sources) =>
        [.. sources.Where(pair => PortTokens.Any(token => pair.Value.Contains(token, StringComparison.Ordinal))
                && !AllowedPortSources.Contains(pair.Key, StringComparer.Ordinal))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    private static List<string> ControllerOffenders(Type controller)
    {
        var offenders = new List<string>();
        var dependencies = controller.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType)
            .Concat(controller.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Select(field => field.FieldType)).Distinct().ToArray();
        if (!dependencies.SequenceEqual([typeof(FederationOperationRunner)]))
            offenders.AddRange(dependencies.Where(type => type != typeof(FederationOperationRunner)).Select(type => "dependency " + type.FullName));
        return offenders;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException failure) { return failure.Types.Where(type => type is not null)!; }
    }

    private sealed class PlantedSenderController(FederationOperationRunner runner, ISender sender)
    {
        public object Runner => runner;
        public object Sender => sender;
    }

    private sealed class PlantedPortController(IFederationProvider provider)
    {
        public object Provider => provider;
    }
}
