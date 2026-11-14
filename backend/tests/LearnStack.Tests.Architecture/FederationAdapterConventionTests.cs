using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.Federation;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// ADR-0099 Step 3: the confined Keycloak adapter alone holds the federation admin client and its secret key, builds only
/// the enumerated Admin routes, never sends a remote-URL import, reads no DeploymentMode, and is, with the guarded egress
/// fetcher, the only federation code that opens an HTTP connection. Each rule carries a planted control.
/// </summary>
public sealed partial class FederationAdapterConventionTests
{
    private const string AdapterDirectory = "LearnStack.Infrastructure/Identity/Federation/";
    private const string RoutesSource = AdapterDirectory + "KeycloakFederationRoutes.cs";
    private const string AdapterSource = AdapterDirectory + "KeycloakFederationProvider.cs";
    private const string FetcherSource = AdapterDirectory + "FederationEgressFetcher.cs";
    private const string OptionsSource = AdapterDirectory + "KeycloakFederationOptions.cs";

    [Fact]
    public void Federation_Client_Is_Held_Only_By_The_Adapter()
    {
        ClientOffenders(Sources()).Should().BeEmpty(
            "Fix: only the confined adapter names learnstack-federation-admin and reads Federation:ClientSecret (ADR-0099); "
            + "every other caller selects it through FederationAdapterFactory");
    }

    [Fact]
    public void The_client_holder_detector_refuses_a_planted_holder()
    {
        var planted = new Dictionary<string, string>
        {
            [RoutesSource] = "internal const string ClientId = \"learnstack-federation-admin\";",
            [AdapterSource] = "internal const string SecretKey = \"Federation:ClientSecret\";",
            ["LearnStack.Api/Identity/Planted.cs"] = "var id = \"learnstack-federation-admin\";",
            ["Modules/Identity/LearnStack.Modules.Identity.Application/Planted.cs"] = "secrets.GetSecret(\"Federation:ClientSecret\");",
            ["LearnStack.Api/Identity/Clean.cs"] = "var id = \"learnstack-web\";",
        };
        ClientOffenders(planted).Should().Equal("LearnStack.Api/Identity/Planted.cs", "Modules/Identity/LearnStack.Modules.Identity.Application/Planted.cs");
    }

    [Fact]
    public void Federation_Adapter_Admits_Only_The_Enumerated_Routes()
    {
        var routes = typeof(FederationAdapterFactory).Assembly.GetType("LearnStack.Infrastructure.Identity.Federation.KeycloakFederationRoutes", true)!;
        routes.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.ReturnType == typeof(Uri)).Select(method => method.Name).Order(StringComparer.Ordinal)
            .Should().Equal(["Instance", "Instances", "Mappers", "Token"], "the token endpoint, create, one owned instance and its mappers only");
        RouteOffenders(Sources()).Should().BeEmpty(
            "Fix: build every provider route in KeycloakFederationRoutes, which admits only identity-provider/instances[/{alias}[/mappers]] (ADR-0099)");
    }

    [Fact]
    public void The_route_detector_refuses_planted_routes()
    {
        var planted = new Dictionary<string, string>
        {
            [RoutesSource] = "new(options.AdminRealm, \"identity-provider/instances\"); new(options.AdminRealm, \"identity-provider/instances/\" + Alias(owner) + \"/mappers\");",
            [OptionsSource] = "new Uri(root + \"/admin/realms/learnstack/\");",
            [AdapterDirectory + "Planted.cs"] = "new Uri(admin, \"users/\" + id + \"/role-mappings\");",
            [AdapterSource] = "var uri = new Uri(\"https://kc/admin/realms/learnstack/clients\");",
            [RoutesSource.Replace("Routes", "Routes2", StringComparison.Ordinal)] = "new(options.AdminRealm, \"authentication/flows\");",
        };
        planted[RoutesSource] += " new(options.AdminRealm, \"identity-provider/import-config\");";
        RouteOffenders(planted).Should().Equal(AdapterDirectory + "KeycloakFederationProvider.cs", AdapterDirectory + "KeycloakFederationRoutes.cs",
            AdapterDirectory + "KeycloakFederationRoutes2.cs", AdapterDirectory + "Planted.cs");
    }

    [Fact]
    public void Federation_Adapter_Never_Sends_A_Remote_Url_Import()
    {
        FromUrlOffenders(Sources()).Should().BeEmpty(
            "Fix: Keycloak is never handed a URL to fetch; the JSON fromUrl import is not admitted (ADR-0099 § Bounded egress)");
    }

    [Fact]
    public void The_from_url_detector_refuses_a_planted_import()
    {
        FromUrlOffenders(new Dictionary<string, string>
        {
            [AdapterSource] = "body[\"fromUrl\"] = url;",
            ["LearnStack.Api/Identity/Planted.cs"] = "new { providerId = \"oidc\", FromURL = url }",
            [FetcherSource] = "var clean = \"from a url\";",
        }).Should().Equal("LearnStack.Api/Identity/Planted.cs", AdapterSource);
    }

    [Fact]
    public void Federation_Adapter_Does_Not_Read_DeploymentMode()
    {
        DeploymentModeOffenders(Sources()).Should().BeEmpty(
            "Fix: the composition root selects the adapter and the Development loopback rule; the adapter never reads DeploymentMode");
        var types = typeof(FederationAdapterFactory).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(FederationAdapterFactory).Namespace);
        types.SelectMany(type => type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(field => field.FieldType)
                .Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType)))
            .Should().NotContain(type => type.Name == "DeploymentMode");
    }

    [Fact]
    public void The_deployment_mode_detector_refuses_a_planted_read()
    {
        DeploymentModeOffenders(new Dictionary<string, string>
        {
            [AdapterSource] = "if (mode == DeploymentMode.SaaS) return;",
            ["LearnStack.Api/Composition/FederationComposition.cs"] = "configuration.RequireDeploymentMode();",
        }).Should().Equal(AdapterSource);
    }

    [Fact]
    public void Federation_Egress_Fetcher_Is_The_Only_Tenant_Url_Fetcher()
    {
        TransportOffenders(Sources()).Should().BeEmpty(
            "Fix: a network transport primitive in production source lives only in a file TransportHolders names with its reason. "
            + "A tenant-supplied URL is fetched only by FederationEgressFetcher (ADR-0099 § Bounded egress); every other holder "
            + "reaches a fixed, composition-configured provider origin");
        var present = Sources().Where(pair => Transport().IsMatch(pair.Value)).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        TransportHolders.Keys.Should().OnlyContain(path => present.Contains(path),
            "an allow-list entry whose file no longer holds a transport is stale and must be removed");
    }

    [Fact]
    public void The_transport_detector_refuses_a_planted_fetcher()
    {
        TransportOffenders(new Dictionary<string, string>
        {
            [FetcherSource] = "new SocketsHttpHandler(); var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);",
            [AdapterSource] = "new HttpClient(handler)",
            [AdapterDirectory + "KeycloakFederationProfile.cs"] = "using var client = new HttpClient();",
            ["Modules/Identity/LearnStack.Modules.Identity.Application/Federation/Planted.cs"] = "await Dns.GetHostAddressesAsync(host);",
            ["Modules/Education/LearnStack.Modules.Education.Infrastructure/Planted.cs"] = "using var client = new TcpClient();",
            ["LearnStack.Api/Planted/Fetcher.cs"] = "var handler = new HttpClientHandler();",
            ["LearnStack.Infrastructure/Planted/Socket.cs"] = "var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);",
            ["LearnStack.Infrastructure/Planted/WebRequest.cs"] = "var request = WebRequest.Create(url);",
            ["LearnStack.Infrastructure/Planted/Factory.cs"] = "services.AddHttpClient(\"x\");",
            ["LearnStack.Api/Composition/Clean.cs"] = "t.AddHttpClientInstrumentation(); catch (SocketException) { } var type = SocketType.Stream;",
        }).Should().Equal("LearnStack.Api/Planted/Fetcher.cs", AdapterDirectory + "KeycloakFederationProfile.cs",
            "LearnStack.Infrastructure/Planted/Factory.cs", "LearnStack.Infrastructure/Planted/Socket.cs",
            "LearnStack.Infrastructure/Planted/WebRequest.cs",
            "Modules/Education/LearnStack.Modules.Education.Infrastructure/Planted.cs",
            "Modules/Identity/LearnStack.Modules.Identity.Application/Federation/Planted.cs");
    }

    private static Dictionary<string, string> Sources() =>
        Directory.EnumerateFiles(SourceScan.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(SourceScan.SourceRoot, path).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText);

    private static List<string> ClientOffenders(IReadOnlyDictionary<string, string> sources) =>
        [.. sources.Where(pair => pair.Value.Contains("learnstack-federation-admin", StringComparison.Ordinal) && pair.Key != RoutesSource
                || pair.Value.Contains("Federation:ClientSecret", StringComparison.Ordinal) && pair.Key != AdapterSource)
            .Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    /// <summary>Every string literal in the adapter directory that names an Admin or identity-provider path.</summary>
    private static List<string> RouteOffenders(IReadOnlyDictionary<string, string> sources)
    {
        var admitted = new HashSet<string>(StringComparer.Ordinal) { "identity-provider/instances", "identity-provider/instances/", "/mappers", "manage-identity-providers" };
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (path, text) in sources.Where(pair => pair.Key.StartsWith(AdapterDirectory, StringComparison.Ordinal)))
        {
            foreach (Match literal in StringLiteral().Matches(text))
            {
                var value = literal.Groups["value"].Value;
                var routeLike = value.Contains("admin/realms", StringComparison.Ordinal) || value.Contains("identity-provider", StringComparison.Ordinal)
                    || value.Contains("authentication/", StringComparison.Ordinal) || value.Contains("required-actions", StringComparison.Ordinal)
                    || value.StartsWith("users/", StringComparison.Ordinal) || value.StartsWith("clients/", StringComparison.Ordinal)
                    || value.StartsWith("roles-by-id/", StringComparison.Ordinal);
                if (!routeLike) continue;
                var allowed = path == RoutesSource && admitted.Contains(value)
                    || path == OptionsSource && value == "/admin/realms/learnstack/";
                if (!allowed) offenders.Add(path);
            }
        }
        return [.. offenders];
    }

    private static List<string> FromUrlOffenders(IReadOnlyDictionary<string, string> sources) =>
        [.. sources.Where(pair => FromUrl().IsMatch(pair.Value)).Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    private static List<string> DeploymentModeOffenders(IReadOnlyDictionary<string, string> sources) =>
        [.. sources.Where(pair => pair.Key.StartsWith(AdapterDirectory, StringComparison.Ordinal)
                && pair.Value.Contains("DeploymentMode", StringComparison.Ordinal))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Every production file that may hold a network transport primitive, with its reason. Only the egress fetcher fetches
    /// a tenant-supplied URL; every other entry dials a fixed origin the composition root configures.
    /// </summary>
    private static readonly Dictionary<string, string> TransportHolders = new(StringComparer.Ordinal)
    {
        [FetcherSource] = "the only tenant-URL fetcher: ADR-0099 § Bounded egress guards every address it dials",
        [AdapterSource] = "the federation admin client: the fixed tenant-realm issuer only (KeycloakFederationOptions)",
        ["LearnStack.Infrastructure/Identity/ProviderEvents/KeycloakIdentityEventProvider.cs"] =
            "the provider event reader (ADR-0097): the fixed tenant-realm issuer only",
        ["LearnStack.Api/Authentication/TenantAuthenticationBackchannel.cs"] =
            "JWT bearer metadata and JWKS (ADR-0064): the fixed Authentication:Tenant:Issuer only",
        ["LearnStack.Api/Authentication/CurrentInvitationRecipientReader.cs"] =
            "the invitation recipient's UserInfo read (ADR-0093): the fixed tenant-realm issuer only",
    };

    private static List<string> TransportOffenders(IReadOnlyDictionary<string, string> sources) =>
        [.. sources.Where(pair => !TransportHolders.ContainsKey(pair.Key) && Transport().IsMatch(pair.Value))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    [GeneratedRegex("\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex StringLiteral();

    [GeneratedRegex("from[\\s_-]*url", RegexOptions.IgnoreCase)]
    private static partial Regex FromUrl();

    /// <summary>
    /// Construction or use of a transport: an HTTP client or handler, a raw or TCP/UDP socket, a TLS or network stream, a
    /// web request, a DNS lookup, or an HTTP client factory registration. Instrumentation names such as
    /// AddHttpClientInstrumentation and exception or enum names such as SocketException and SocketType do not match.
    /// </summary>
    [GeneratedRegex("\\b(HttpClient|HttpClientHandler|SocketsHttpHandler|HttpMessageInvoker|Socket|TcpClient|TcpListener|UdpClient|SslStream|NetworkStream|ClientWebSocket|WebRequest|HttpWebRequest|WebClient|IHttpClientFactory|AddHttpClient)\\b|\\bDns\\.")]
    private static partial Regex Transport();
}
