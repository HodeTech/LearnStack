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
        // Step 3: the confined adapter and guarded fetcher implement the ports, their factory returns them, and the
        // composition root selects them (Federation_Client_Is_Held_Only_By_The_Adapter owns the adapter's own rule).
        "LearnStack.Infrastructure/Identity/Federation/KeycloakFederationProvider.cs",
        "LearnStack.Infrastructure/Identity/Federation/FederationEgressFetcher.cs",
        "LearnStack.Infrastructure/Identity/Federation/FederationAdapterFactory.cs",
        "LearnStack.Api/Composition/FederationComposition.cs",
    ];

    [Fact]
    public void Federation_Provider_Port_Is_Held_Only_By_The_Runner()
    {
        var sources = Directory.EnumerateFiles(SourceScan.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(SourceScan.SourceRoot, path).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText);
        PortOffenders(sources).Should().BeEmpty(
            "Fix: only FederationOperationRunner calls the federation provider and egress fetcher; the Step 3 adapter, fetcher and composition implement or select them (ADR-0099)");
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

    /// <summary>
    /// The three raw SQL readers of the registry; none can use the EF <c>Live</c> helper (Step 2 review S-D). The host
    /// alias reader is ADR-0099 Amendment 6's setter for the native self read.
    /// </summary>
    private static readonly string[] RawLiveReaders =
    [
        "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Persistence/FederationConnectionStore.cs",
        "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Persistence/FederationHostAliasReader.cs",
        "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Persistence/PublicSignInReader.cs",
    ];

    /// <summary>
    /// The operator's one read of the registry (ADR-0099 Amendment 10 and 11): an orphan is a broker with no row or a
    /// tombstoned one, so this reader must see tombstones and is exempt from the shared live predicate. It is pinned
    /// instead to exactly this statement, which names the four columns the platform role may select and reads by
    /// primary key; a fifth column or another predicate is refused by PostgreSQL and by this census.
    /// </summary>
    private const string OperatorRowReader = IdentityInfrastructure + "Federation/FederationBrokerOperations.cs";

    private const string OperatorRowQuery =
        "SELECT id, tenant_id, state, deleted_at IS NOT NULL FROM federation_connections WHERE id = ANY(@ids)";

    private static readonly string[] AllRawReaders = [.. RawLiveReaders, OperatorRowReader];

    [Fact]
    public void Raw_Federation_Reads_Share_The_One_Pinned_Live_Predicate()
    {
        // Pinned text: a change here is a deliberate tombstone-semantics change, made once for both raw readers.
        FederationConnectionReads.LiveSql.Should().Be("state <> 'Deleted' AND deleted_at IS NULL");
        var sources = Directory.EnumerateFiles(SourceScan.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(SourceScan.SourceRoot, path).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText);
        sources.Keys.Should().Contain(path => path.StartsWith(IdentityApplication, StringComparison.Ordinal))
            .And.Contain(path => path.StartsWith(IdentityInfrastructure, StringComparison.Ordinal), "the scan reads both Identity projects");
        RawLiveOffenders(sources).Should().BeEmpty(
            "Fix: every raw read of federation_connections lives in one of the three closed readers, and each statement interpolates "
            + "FederationConnectionReads.LiveSql and spells no tombstone predicate of its own");
        RawReaders(sources).Should().Equal(AllRawReaders.Order(StringComparer.Ordinal),
            "the census of raw registry readers is closed at three tenant readers and the operator's one pinned read");
    }

    [Fact]
    public void The_raw_live_predicate_detector_refuses_a_reader_with_its_own_predicate()
    {
        const string Shared = "WHERE tenant_id=@tenant AND {FederationConnectionReads.LiveSql}";
        var clean = new Dictionary<string, string>
        {
            [RawLiveReaders[0]] = "SELECT public.fn_identity_assert_tenant_fence(@tenant,false);\nSELECT id FROM public.federation_connections "
                + Shared + " FOR NO KEY UPDATE;",
            [RawLiveReaders[1]] = "\"\"\"\nSELECT alias,protocol FROM public.federation_connections " + Shared + " AND state='Active'\n\"\"\";",
            [RawLiveReaders[2]] = "\"\"\"\nSELECT alias FROM public.federation_connections " + Shared + " AND state='Active'\n\"\"\";",
            [IdentityInfrastructure + "Persistence/FederationConnectionStorageDdl.cs"] =
                "REVOKE ALL ON public.federation_connections FROM PUBLIC; CREATE TRIGGER t BEFORE INSERT OR UPDATE ON public.federation_connections;",
            [OperatorRowReader] = "private const string RowQuery = \"" + OperatorRowQuery + "\";",
        };
        RawLiveOffenders(clean).Should().BeEmpty();
        RawReaders(clean).Should().Equal(AllRawReaders.Order(StringComparer.Ordinal));
        // The operator's statement is pinned whole: a fifth column, another predicate or a second statement is refused.
        foreach (var changed in new[]
        {
            OperatorRowQuery.Replace("state,", "state, alias,", StringComparison.Ordinal),
            OperatorRowQuery.Replace("id = ANY(@ids)", "tenant_id = @tenant", StringComparison.Ordinal),
            OperatorRowQuery + " AND state <> 'Deleted'",
        })
        {
            var widened = new Dictionary<string, string>(clean) { [OperatorRowReader] = "private const string RowQuery = \"" + changed + "\";" };
            RawLiveOffenders(widened).Should().Equal([OperatorRowReader], "the operator reader is pinned: " + changed);
        }
        var own = new Dictionary<string, string>(clean)
        {
            [RawLiveReaders[1]] = "SELECT alias FROM public.federation_connections WHERE tenant_id=@tenant AND state='Active' AND deleted_at IS NULL",
        };
        RawLiveOffenders(own).Should().Equal(RawLiveReaders[1]);
        var both = new Dictionary<string, string>(clean)
        {
            [RawLiveReaders[0]] = clean[RawLiveReaders[0]].Replace(" FOR NO KEY UPDATE;", " AND deleted_at IS NULL FOR NO KEY UPDATE;", StringComparison.Ordinal),
        };
        RawLiveOffenders(both).Should().Equal(RawLiveReaders[0]);
        // Per statement, not per file: a second statement in a closed reader that skips the shared predicate is refused
        // even though the file still interpolates it once.
        var second = new Dictionary<string, string>(clean)
        {
            [RawLiveReaders[0]] = clean[RawLiveReaders[0]] + "\nSELECT count(*) FROM public.federation_connections WHERE tenant_id=@tenant;",
        };
        RawLiveOffenders(second).Should().Equal(RawLiveReaders[0]);
        // A reader outside the closed census is refused however it spells the table: a JOIN, no schema, other spacing,
        // a quoted identifier or the mapped table-name constant.
        const string Planted = IdentityApplication + "Federation/PlantedReader.cs";
        foreach (var reference in new[]
        {
            "SELECT m.id FROM memberships m JOIN public.federation_connections f ON f.tenant_id=m.tenant_id " + Shared,
            "SELECT alias FROM federation_connections " + Shared,
            "SELECT alias\n  from   public .\n\tfederation_connections " + Shared,
            "SELECT alias FROM \"public\".\"federation_connections\" " + Shared,
            "SELECT alias FROM public.{FederationConnectionConfiguration.Table} " + Shared,
        })
        {
            var planted = new Dictionary<string, string>(clean) { [Planted] = reference };
            RawLiveOffenders(planted).Should().Equal([Planted], "an unlisted reader is refused: " + reference);
            RawReaders(planted).Should().Contain(Planted, "the census sees the planted reader: " + reference);
        }
        // A closed reader that is removed leaves the census short.
        var dropped = new Dictionary<string, string>(clean) { [RawLiveReaders[2]] = "public sealed class PublicSignInReader;" };
        RawReaders(dropped).Should().Equal([OperatorRowReader, RawLiveReaders[0], RawLiveReaders[1]]);
    }

    private const string IdentityApplication = "Modules/Identity/LearnStack.Modules.Identity.Application/";
    private const string IdentityInfrastructure = "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/";

    /// <summary>
    /// A relational read reference to the registry: <c>FROM</c> or <c>JOIN</c>, any case and whitespace, with or without
    /// the schema, quoted or not, or through the mapped table-name constant. DDL (<c>ON</c>, <c>REVOKE … FROM PUBLIC</c>)
    /// and constraint names are not reads.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex RawRead = new(
        """\b(?:FROM|JOIN)\s+(?:\\?"?public\\?"?\s*\.\s*)?(?:\\?"?federation_connections\\?"?(?![A-Za-z0-9_])|\{[^{}]*FederationConnectionConfiguration\.Table\})""",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static List<string> RawReaders(IReadOnlyDictionary<string, string> sources) =>
        [.. sources.Where(pair => RawRead.IsMatch(pair.Value)).Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Each raw read must sit in one of the three closed readers, and the statement around it, bounded by the nearest
    /// <c>;</c> or string delimiter on either side, must interpolate the shared constant and spell no tombstone predicate.
    /// </summary>
    private static List<string> RawLiveOffenders(IReadOnlyDictionary<string, string> sources) =>
        [.. sources.Where(pair => RawRead.Matches(pair.Value).Any(match => pair.Key == OperatorRowReader
                    ? !string.Equals(Statement(pair.Value, match.Index).Trim(), OperatorRowQuery, StringComparison.Ordinal)
                    : !RawLiveReaders.Contains(pair.Key, StringComparer.Ordinal)
                || Statement(pair.Value, match.Index) is var statement
                    && (!statement.Contains("{FederationConnectionReads.LiveSql}", StringComparison.Ordinal)
                        || statement.Contains("deleted_at", StringComparison.OrdinalIgnoreCase)
                        || statement.Contains("'Deleted'", StringComparison.OrdinalIgnoreCase))))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    private static string Statement(string text, int index)
    {
        var start = text.LastIndexOfAny([';', '"'], index) + 1;
        var end = text.IndexOfAny([';', '"'], index);
        return text[start..(end < 0 ? text.Length : end)];
    }

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
