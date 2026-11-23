using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.AccountRemoval;
using LearnStack.SharedKernel.Identity;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// ADR-0107 § 7: the confined Keycloak account-removal adapter alone holds the <c>manage-users</c> realm client and its
/// secret key, builds only the token route and the single user-deletion route, sends only the token POST and the deletion
/// DELETE, reads no DeploymentMode, and the port it implements has one method and a closed set of holders. The client's role
/// can also create and update users, so every rule here narrows who can reach it. Each rule carries a planted control. The
/// federation adapter's own rules, including the transport allow-list this adapter's file is on, are in
/// <c>FederationAdapterConventionTests</c>.
/// </summary>
public sealed partial class AccountRemovalAdapterConventionTests
{
    private const string AdapterDirectory = "LearnStack.Infrastructure/Identity/AccountRemoval/";
    private const string RoutesSource = AdapterDirectory + "KeycloakAccountRemovalRoutes.cs";
    private const string AdapterSource = AdapterDirectory + "KeycloakAccountRemoval.cs";
    private const string OptionsSource = AdapterDirectory + "KeycloakAccountRemovalOptions.cs";
    private const string OwnerDirectory = "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/DataRequests/";
    private const string OwnerFilePrefix = OwnerDirectory + "AccountClosureOperatorOperations";
    private const string OwnerFinalizeSource = OwnerFilePrefix + ".Finalize.cs";

    [Fact]
    public void Account_Removal_Client_Is_Held_Only_By_The_Adapter()
    {
        ClientOffenders(Sources()).Should().BeEmpty(
            "Fix: only the confined adapter names learnstack-account-removal and reads AccountRemoval:ClientSecret (ADR-0107 § 7); "
            + "every other caller selects it through AccountRemovalAdapterFactory");
    }

    [Fact]
    public void The_client_holder_detector_refuses_a_planted_holder()
    {
        var planted = new Dictionary<string, string>
        {
            [RoutesSource] = "internal const string ClientId = \"learnstack-account-removal\";",
            [AdapterSource] = "internal const string SecretKey = \"AccountRemoval:ClientSecret\";",
            ["LearnStack.Api/Identity/Planted.cs"] = "var id = \"learnstack-account-removal\";",
            ["Modules/Identity/LearnStack.Modules.Identity.Application/Planted.cs"] = "secrets.GetSecret(\"AccountRemoval:ClientSecret\");",
            ["LearnStack.Infrastructure/Identity/Federation/Planted.cs"] = "var id = \"learnstack-account-removal\";",
            ["LearnStack.Api/Identity/CommentOnly.cs"] = "// the \"learnstack-account-removal\" client is named here in prose only",
            ["LearnStack.Api/Identity/Clean.cs"] = "var id = \"learnstack-federation-admin\"; secrets.GetSecret(\"Federation:ClientSecret\");",
        };
        ClientOffenders(planted).Should().Equal("LearnStack.Api/Identity/Planted.cs",
            "LearnStack.Infrastructure/Identity/Federation/Planted.cs", "Modules/Identity/LearnStack.Modules.Identity.Application/Planted.cs");
    }

    [Fact]
    public void Account_Removal_Adapter_Admits_Only_The_Token_And_User_Routes()
    {
        var routes = typeof(AccountRemovalAdapterFactory).Assembly
            .GetType("LearnStack.Infrastructure.Identity.AccountRemoval.KeycloakAccountRemovalRoutes", true)!;
        routes.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.ReturnType == typeof(Uri)).Select(method => method.Name).Order(StringComparer.Ordinal)
            .Should().Equal(["Token", "User"], "the token endpoint and the deletion of one user are the only routes");
        RouteOffenders(Sources()).Should().BeEmpty(
            "Fix: build every provider route in KeycloakAccountRemovalRoutes, which admits only the token endpoint and users/{id} (ADR-0107 § 7)");
    }

    [Fact]
    public void The_route_detector_refuses_planted_routes()
    {
        var planted = new Dictionary<string, string>
        {
            [RoutesSource] = "new(options.AdminRealm, \"users/\" + id); const string role = \"manage-users\"; // \"users/x/reset-password\"",
            [OptionsSource] = "new Uri(root + \"/admin/realms/learnstack/\");",
            [AdapterDirectory + "Planted.cs"] = "new Uri(admin, \"users/\" + id + \"/role-mappings\");",
            [AdapterSource] = "var uri = new Uri(\"https://kc/admin/realms/learnstack/clients\");",
            [AdapterDirectory + "Sessions.cs"] = "new Uri(admin, \"users/\" + id + \"/logout\");",
            [AdapterDirectory + "Groups.cs"] = "new Uri(admin, \"groups\");",
            [AdapterDirectory + "Roles.cs"] = "new Uri(admin, \"roles-by-id/x\");",
            [AdapterDirectory + "Clean.cs"] = "throw new ArgumentException(\"Account removal subject is not a provider user identifier.\");",
            [AdapterDirectory + "Claim.cs"] = "root.TryGetProperty(\"roles\", out var roles);",
            [AdapterDirectory + "Build.cs"] = "var uri = new Uri(options.AdminRealm, segment);",
            [AdapterDirectory + "Builder.cs"] = "var uri = new UriBuilder(options.Issuer).Uri;",
            ["LearnStack.Infrastructure/Identity/Federation/Elsewhere.cs"] = "new Uri(admin, \"users/\" + id);",
        };
        planted[RoutesSource] += " new(options.AdminRealm, \"users/\" + id + \"/credentials\");";
        RouteOffenders(planted).Should().Equal(new[]
        {
            AdapterSource, AdapterDirectory + "Build.cs", AdapterDirectory + "Builder.cs", AdapterDirectory + "Claim.cs",
            AdapterDirectory + "Groups.cs", AdapterDirectory + "Planted.cs", AdapterDirectory + "Roles.cs", RoutesSource,
            AdapterDirectory + "Sessions.cs",
        }.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Account_Removal_Adapter_Sends_Only_The_Token_Post_And_The_User_Delete()
    {
        VerbOffenders(Sources()).Should().BeEmpty(
            "Fix: the adapter's client can also create and update users; it sends the token POST and the deletion DELETE and no other verb (ADR-0107 § 7)");
        var adapter = Sources()[AdapterSource];
        adapter.Should().Contain("HttpMethod.Post").And.Contain("HttpMethod.Delete",
            "the premise: the scan reads the file that sends the two admitted requests");
    }

    [Fact]
    public void The_verb_detector_refuses_a_planted_verb()
    {
        VerbOffenders(new Dictionary<string, string>
        {
            [AdapterSource] = "new HttpRequestMessage(HttpMethod.Put, uri); new HttpRequestMessage(HttpMethod.Delete, uri);",
            [AdapterDirectory + "Create.cs"] = "new HttpRequestMessage(HttpMethod.Post, users); client.PostAsync(users, body);",
            [AdapterDirectory + "Read.cs"] = "client.GetAsync(uri); new HttpRequestMessage(HttpMethod.Get, uri);",
            [AdapterDirectory + "Custom.cs"] = "new HttpMethod(\"PATCH\");",
            [AdapterDirectory + "Json.cs"] = "await client.PutAsJsonAsync(uri, body);",
            [AdapterDirectory + "Clean.cs"] = "new HttpRequestMessage(HttpMethod.Delete, uri); new HttpRequestMessage(HttpMethod.Post, token);",
            ["LearnStack.Api/Elsewhere.cs"] = "client.GetAsync(uri);",
        }).Should().Equal(AdapterDirectory + "Create.cs", AdapterDirectory + "Custom.cs", AdapterDirectory + "Json.cs", AdapterSource,
            AdapterDirectory + "Read.cs");
    }

    [Fact]
    public void Account_Removal_Adapter_Does_Not_Read_DeploymentMode()
    {
        DeploymentModeOffenders(Sources()).Should().BeEmpty(
            "Fix: the composition root selects the adapter and the Development loopback rule; the adapter never reads DeploymentMode");
        var types = typeof(AccountRemovalAdapterFactory).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(AccountRemovalAdapterFactory).Namespace);
        types.Should().NotBeEmpty("the premise: the namespace holds the adapter");
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
            [AdapterDirectory + "Other.cs"] = "var m = DeploymentMode.SelfHosted;",
            ["LearnStack.Api/Composition/AccountRemovalComposition.cs"] = "configuration.RequireDeploymentMode();",
        }).Should().Equal(AdapterSource, AdapterDirectory + "Other.cs");
    }

    [Fact]
    public void Account_Removal_Port_Has_One_Method_And_A_Closed_Set_Of_Holders()
    {
        typeof(IIdentityProviderAccountRemoval).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal)
            .Should().Equal(["CanRemove", "RemoveAsync"],
                "the client behind the port can create and update users, so the port deletes one account and does nothing else; "
                + "its one other member is the closure Begin's local preflight, which sends nothing");
        typeof(IIdentityProviderAccountRemoval).GetMethod("CanRemove")!.ReturnType.Should().Be<bool>(
            "the preflight answers synchronously, so it cannot reach the provider");
        typeof(IIdentityProviderAccountRemoval).GetProperties().Should().BeEmpty();
        PortHolders(Sources()).Should().BeEmpty(
            "Fix: the port is named only by its declaration, its default, the adapter and its composition, and the closure's Finalize "
            + "owner (ADR-0107 § 7: one caller). A new holder needs a deliberate entry in AdmittedPortHolders and the ADR amendment that admits it");
    }

    [Fact]
    public void The_port_holder_detector_refuses_a_planted_holder()
    {
        PortHolders(new Dictionary<string, string>
        {
            ["LearnStack.SharedKernel/Identity/IIdentityProviderAccountRemoval.cs"] = "public interface IIdentityProviderAccountRemoval { }",
            ["LearnStack.Api/Identity/Planted.cs"] = "class C(IIdentityProviderAccountRemoval remover) { }",
            ["Modules/Identity/LearnStack.Modules.Identity.Application/Planted.cs"] = "services.GetService<IIdentityProviderAccountRemoval>();",
            ["Modules/Tenancy/LearnStack.Modules.Tenancy.Application/Planted.cs"] = "var x = typeof(RefusingIdentityProviderAccountRemoval);",
            ["LearnStack.Api/Identity/Clean.cs"] = "// IIdentityProviderAccountRemoval is mentioned in a comment only\nvar federation = typeof(IFederationOperator);",
        }).Should().Equal("LearnStack.Api/Identity/Planted.cs", "Modules/Identity/LearnStack.Modules.Identity.Application/Planted.cs",
            "Modules/Tenancy/LearnStack.Modules.Tenancy.Application/Planted.cs");
    }

    [Fact]
    public void Account_Removal_Port_Is_Called_Only_From_The_Closure_Finalize()
    {
        var sources = Sources();
        CallSiteOffenders(sources).Should().BeEmpty(
            "Fix: the port's one method deletes an account with a manage-users credential and has one call, in the closure owner's "
            + "Finalize part, after the redaction checkpoint (ADR-0107 § 7). The captured port is held only by the owner's partial "
            + "files and is used only as the receiver of a call, never passed on or kept");
        sources.Should().ContainKey(OwnerFinalizeSource, "the premise: the scan reads the file that makes the one call");
        CallCount(SourceText.WithoutCommentsOrLiterals(sources[OwnerFinalizeSource])).Should().Be(1, "the premise: Finalize makes the call, once");
    }

    [Fact]
    public void The_call_site_detector_refuses_planted_departures()
    {
        const string Constructor = "public sealed partial class AccountClosureOperatorOperations(IIdentityProviderAccountRemoval accountRemoval) { }";
        const string Finalize = "var outcome = await accountRemoval.RemoveAsync(providerSubject, cancellationToken);";
        Dictionary<string, string> Owner(string file, string text) => new()
        {
            [OwnerFilePrefix + ".cs"] = Constructor,
            [OwnerFinalizeSource] = Finalize,
            [OwnerFilePrefix + file] = text,
        };

        // The clean shape, with prose, a literal and a comment that merely say the words, and a file that does not name the port.
        CallSiteOffenders(new Dictionary<string, string>
        {
            [OwnerFilePrefix + ".cs"] = Constructor,
            [OwnerFinalizeSource] = Finalize + " // accountRemoval.RemoveAsync(again) in a comment\nvar prose = \"accountRemoval.RemoveAsync(\";",
            [OwnerFilePrefix + ".Begin.cs"] = "var judged = Judge(subject); /* accountRemoval.RemoveAsync( */",
            [AdapterDirectory + "AccountRemovalAdapterFactory.cs"] = "public const string PortName = \"accountRemoval\";",
            ["LearnStack.Api/Identity/Clean.cs"] = "var accountRemovalSettings = 1; // accountRemoval",
        }).Should().BeEmpty();

        CallSiteOffenders(Owner(".Begin.cs", "var outcome = await accountRemoval.RemoveAsync(subject, cancellationToken);"))
            .Should().Equal(OwnerFilePrefix + ".Begin.cs");
        CallSiteOffenders(Owner(".Begin.cs", "var outcome = await accountRemoval . RemoveAsync (subject, ct);"))
            .Should().Equal(OwnerFilePrefix + ".Begin.cs");
        CallSiteOffenders(new Dictionary<string, string> { [OwnerFilePrefix + ".cs"] = Constructor, [OwnerFinalizeSource] = Finalize + Finalize })
            .Should().Equal(OwnerFinalizeSource);
        CallSiteOffenders(new Dictionary<string, string> { [OwnerFilePrefix + ".cs"] = Constructor, [OwnerFinalizeSource] = "var nothing = 1;" })
            .Should().Equal(OwnerFinalizeSource);
        CallSiteOffenders(Owner(".Begin.cs", "var port = accountRemoval;")).Should().Equal(OwnerFilePrefix + ".Begin.cs");
        CallSiteOffenders(Owner(".Begin.cs", "Run(() => accountRemoval);")).Should().Equal(OwnerFilePrefix + ".Begin.cs");
        CallSiteOffenders(Owner(".Begin.cs", "Helper(accountRemoval, subject);")).Should().Equal(OwnerFilePrefix + ".Begin.cs");
        CallSiteOffenders(Owner(".Begin.cs", "var remove = accountRemoval.RemoveAsync;")).Should().Equal(OwnerFilePrefix + ".Begin.cs");
        CallSiteOffenders(new Dictionary<string, string>
        {
            [OwnerFilePrefix + ".cs"] = Constructor,
            [OwnerFinalizeSource] = Finalize,
            ["LearnStack.Api/Identity/Planted.cs"] = "await accountRemoval.RemoveAsync(subject, ct);",
            [OwnerDirectory + "IdentityDeletion.cs"] = "var port = accountRemoval;",
        }).Should().Equal("LearnStack.Api/Identity/Planted.cs", OwnerDirectory + "IdentityDeletion.cs");
    }

    private static Dictionary<string, string> Sources() =>
        Directory.EnumerateFiles(SourceScan.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(path => Path.GetRelativePath(SourceScan.SourceRoot, path).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText);

    /// <summary>A source with its whole-line comments removed: these files argue in prose about the very literals they confine.</summary>
    private static string Code(string source) => WholeLineComment().Replace(source, string.Empty);

    private static List<string> ClientOffenders(IReadOnlyDictionary<string, string> sources) =>
        [.. sources.Where(pair => pair.Key.EndsWith(".cs", StringComparison.Ordinal)
                && (Code(pair.Value).Contains("learnstack-account-removal", StringComparison.Ordinal) && pair.Key != RoutesSource
                    || Code(pair.Value).Contains("AccountRemoval:ClientSecret", StringComparison.Ordinal) && pair.Key != AdapterSource))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    /// <summary>Every string literal in the adapter directory that names an Admin, user, client, role, group or session path.</summary>
    private static List<string> RouteOffenders(IReadOnlyDictionary<string, string> sources)
    {
        var admitted = new HashSet<string>(StringComparer.Ordinal) { "users/" };
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (path, text) in sources.Where(pair => pair.Key.StartsWith(AdapterDirectory, StringComparison.Ordinal)))
        {
            foreach (Match literal in StringLiteral().Matches(Code(text)))
            {
                var value = literal.Groups["value"].Value;
                var routeLike = RouteSegment().IsMatch(value);
                if (!routeLike) continue;
                // "roles" is the token claim the adapter narrows; it is a route-like word, never a route, because no URI is built here.
                var allowed = path == RoutesSource && admitted.Contains(value) || path == OptionsSource && value == "/admin/realms/learnstack/"
                    || path == AdapterSource && value == "roles";
                if (!allowed) offenders.Add(path);
            }
            // Every URI is derived from the realm in the routes and options files; no other file builds or extends one.
            if (path != RoutesSource && path != OptionsSource && UriConstruction().IsMatch(Code(text))) offenders.Add(path);
        }
        return [.. offenders];
    }

    private static List<string> VerbOffenders(IReadOnlyDictionary<string, string> sources) =>
        [.. sources.Where(pair => pair.Key.StartsWith(AdapterDirectory, StringComparison.Ordinal) && NonAdmittedVerb().IsMatch(Code(pair.Value)))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    private static List<string> DeploymentModeOffenders(IReadOnlyDictionary<string, string> sources) =>
        [.. sources.Where(pair => pair.Key.StartsWith(AdapterDirectory, StringComparison.Ordinal)
                && pair.Value.Contains("DeploymentMode", StringComparison.Ordinal))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Every production file that may name the port or its concrete types, with its role. The closure owner is the one
    /// caller ADR-0107 § 7 admits: its constructor file takes the port, and its Finalize part calls it through that field
    /// without naming the type, so the Finalize file is not a holder and stays out of this list.
    /// </summary>
    private static readonly HashSet<string> AdmittedPortHolders = new(StringComparer.Ordinal)
    {
        "LearnStack.SharedKernel/Identity/IIdentityProviderAccountRemoval.cs",
        "LearnStack.SharedKernel/Identity/RefusingIdentityProviderAccountRemoval.cs",
        AdapterSource,
        AdapterDirectory + "AccountRemovalAdapterFactory.cs",
        "LearnStack.Api/Composition/AccountRemovalComposition.cs",
        "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Bindings/IdentityFoundationRegistration.cs",
        "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/DataRequests/AccountClosureOperatorOperations.cs",
    };

    /// <summary>
    /// The one call. <c>accountRemoval</c> is the closure owner's captured port: only the owner's partial files (the constructor,
    /// Begin and Finalize parts) may name it, only as the receiver of a call (never aliased, passed on or captured), and its one
    /// method is called exactly once, in the Finalize part. A file that names the port types is already confined by
    /// <see cref="AdmittedPortHolders"/>; this rule covers the files that reach the port without naming a type.
    /// </summary>
    private static List<string> CallSiteOffenders(IReadOnlyDictionary<string, string> sources)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (path, text) in sources.Where(pair => pair.Key.EndsWith(".cs", StringComparison.Ordinal)))
        {
            var code = SourceText.WithoutCommentsOrLiterals(text);
            var isOwner = path.StartsWith(OwnerFilePrefix, StringComparison.Ordinal);
            if (CapturedPort().IsMatch(code) && !isOwner)
                offenders.Add(path);
            else if (isOwner && CapturedPortUse().Matches(code).Any(use => !use.Groups["receiver"].Success && !use.Groups["declaration"].Success))
                offenders.Add(path);
            if (CallCount(code) != (path == OwnerFinalizeSource ? 1 : 0))
                offenders.Add(path);
        }

        return [.. offenders];
    }

    private static int CallCount(string code) => CapturedPortCall().Count(code);

    private static List<string> PortHolders(IReadOnlyDictionary<string, string> sources) =>
        [.. sources.Where(pair => !AdmittedPortHolders.Contains(pair.Key) && AccountRemovalNames().IsMatch(Code(pair.Value)))
            .Select(pair => pair.Key).Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"^\s*//.*$", RegexOptions.Multiline)]
    private static partial Regex WholeLineComment();

    [GeneratedRegex("\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex StringLiteral();

    /// <summary>A string that names an Admin API path segment: realm, user, client, role, group, session, credential or flow.</summary>
    [GeneratedRegex("(?:^|/)(?:admin/realms|users|clients|roles|roles-by-id|groups|sessions|logout|credentials|role-mappings|identity-provider|authentication|required-actions|execute-actions|reset-password)(?:/|$)")]
    private static partial Regex RouteSegment();

    /// <summary>A URI built or extended: the realm and token members of the options, a <c>new Uri(</c>, or a <c>UriBuilder</c>.</summary>
    [GeneratedRegex(@"\bAdminRealm\b|\bTokenEndpoint\b|new\s+Uri\s*\(|\bUriBuilder\b")]
    private static partial Regex UriConstruction();

    /// <summary>A verb other than POST and DELETE: an <c>HttpMethod</c> member, a constructed method, or a verb-named client helper.</summary>
    [GeneratedRegex(@"\bHttpMethod\.(Get|Put|Patch|Head|Options|Trace|Connect)\b|new\s+HttpMethod\s*\(|\.(Get|Put|Patch|Head|Post|Delete)(String|ByteArray|Stream|FromJson|AsJson)?Async\s*\(")]
    private static partial Regex NonAdmittedVerb();

    [GeneratedRegex(@"\b(IIdentityProviderAccountRemoval|RefusingIdentityProviderAccountRemoval|KeycloakAccountRemoval|AccountRemovalAdapterFactory)\b")]
    private static partial Regex AccountRemovalNames();

    /// <summary>The closure owner's captured port, by its parameter name, outside comments and literals.</summary>
    [GeneratedRegex(@"\baccountRemoval\b")]
    private static partial Regex CapturedPort();

    /// <summary>
    /// Each use of the captured port: as the receiver of a member access (<c>accountRemoval.</c>) or in the constructor's own
    /// parameter declaration (the port type then the name). Any other use matches neither group: an alias, an argument, a capture.
    /// </summary>
    [GeneratedRegex(@"(?<declaration>\bIIdentityProviderAccountRemoval\s+accountRemoval\b)|\baccountRemoval\b(?<receiver>\s*\.\s*\w+\s*\()?")]
    private static partial Regex CapturedPortUse();

    /// <summary>A call of the port's deleting method through the captured port, with any spacing.</summary>
    [GeneratedRegex(@"\baccountRemoval\s*\.\s*RemoveAsync\s*\(")]
    private static partial Regex CapturedPortCall();
}
