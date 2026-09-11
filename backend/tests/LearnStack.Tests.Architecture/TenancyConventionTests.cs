using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.SharedKernel.Tenancy;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The tenancy-edge rules
/// <see href="../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
/// assigns to Packet 4, catalogued in
/// <see href="../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21
/// § Tenant and organization resolution</see>.
/// </summary>
/// <remarks>
/// <para>
/// Most of these are <b>source scans</b>, and that is a deliberate choice rather
/// than a shortcut. Each rule is about a symbol not appearing outside one file —
/// a reflection or NetArchTest form would have to observe a call, and these rules were
/// written before Packet 7's resolver gave the values a consumer. A scan holds the line
/// from the day the symbol exists,
/// which is the day it can first be used wrongly. Where the type a rule names
/// now exists, the rule adds a reflection check alongside the scan rather than
/// replacing it: the two catch different mistakes.
/// </para>
/// <para>
/// Comment lines are skipped. Every one of these files argues in prose about the
/// very literal it is forbidden to use, so scanning them raw would fail on the
/// documentation that explains the rule.
/// </para>
/// </remarks>
public sealed partial class TenancyConventionTests
{
    [Fact]
    public void Effective_Host_Computed_In_One_Place()
    {
        // EffectiveHostAccessor decides what host a request is for — trusted-hop
        // predicate, header, normalization, all of it. A second reader of the host is a
        // second answer, and the one that skips the accessor is the one that skips the
        // trust check.
        // The banned list names every spelling that reaches a host, not only the
        // conventional one. `X-Forwarded-Host` appears nowhere in the source — banning it
        // alone made the rule green over the real hole: `TrustedHopOptions.HostHeaderName`
        // is a public const carrying `X-LearnStack-Host`, and a second file reading it
        // reads the forwarded host WITHOUT `IsTrustedHop`'s CIDR check and constant-time
        // secret comparison. Both the literal and the const are banned, because either
        // spelling reaches the same header — and since Packet 10 so are the header
        // collection's own routes to `Host` and the `Forwarded` header, which the entry
        // named and the scan did not read.
        var sources = ApiSources(
            except: [
                Path.Combine("Tenancy", "EffectiveHostAccessor.cs"),
                Path.Combine("Tenancy", "TrustedHopOptions.cs"),
            ]).ToList();

        sources.Should().NotBeEmpty("the premise: the scan reads the API's sources");

        sources.Where(source => ReadsAHost(source.Code)).Select(source => source.Relative)
            .Should().BeEmpty(
                "only EffectiveHostAccessor reads a request host (ADR-0036 § Effective "
                + "host and the trusted hop)");
    }

    [Fact]
    public void The_Host_Read_Scan_Can_Actually_Fail()
    {
        // Every file but the accessor is clean, so the rule above passes whether its
        // needles match anything or not. Each shape a handler could write is fed through
        // the same whitespace-blind match, and one that merely resembles them must pass.
        string[] reads =
        [
            "var host = context.Request.Host.Value;",
            "var host = context.Request.Headers.Host;",
            "var host = context.Request.Headers [ \"Host\" ];",
            "var host = context.Request.Headers[HeaderNames.Host];",
            "var host = context.Request.GetTypedHeaders().Host;",
            "var typed = context.Request.GetTypedHeaders(); var host = typed.Host;",
            "var host = context.Request.Headers[\"host\"];",
            "var forwarded = context.Request.Headers[\"Forwarded\"];",
            "var forwarded = context.Request.Headers[HeaderNames.Forwarded];",
            "var url = context.Request.GetDisplayUrl();",
        ];

        reads.Should().OnlyContain(code => ReadsAHost(code));
        ReadsAHost("var peer = context.Request.Headers[\"X-Forwarded-For\"];").Should().BeFalse(
            "the client-address header is not a host, and the rate limiter reads the peer");
        ReadsAHost("builder.Host.UseSerilog((context, services, configuration) => { });").Should().BeFalse(
            "the host builder is not a request host, and the composition root configures it");
        ReadsAHost("var tag = context.Request.GetTypedHeaders().IfMatch;").Should().BeFalse(
            "the typed-header helper carries every header; only a host read through it is banned");
    }

    /// <summary>
    /// Every spelling that reads a request's host, as patterns over whitespace-free source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Patterns rather than literals, because the receiver is a local name as often as a
    /// property path: <c>request.Host</c> is the same read as <c>context.Request.Host</c> and
    /// a literal needle for the second missed the first. <c>Headers.TryGetValue("Host"</c> is
    /// here for the same reason — the indexer is one of two ways the collection is read.
    /// </para>
    /// <para>
    /// Matched case-insensitively, because a header name is: <c>Headers["host"]</c> reads the
    /// same header as <c>Headers["Host"]</c> and the dictionary is case-blind.
    /// </para>
    /// </remarks>
    private static readonly string[] HostReads =
    [
        @"[A-Za-z_0-9]*[Rr]equest\.Host\b",
        @"Headers\.Host\b",
        @"Headers\[""Host""\]",
        @"TryGetValue\(""Host""",
        @"HeaderNames\.Host\b",
        "GetDisplayUrl",
        "GetEncodedUrl",
        "X-Forwarded-Host",
        @"HeaderNames\.XForwardedHost\b",
        @"""Forwarded""",
        @"HeaderNames\.Forwarded\b",
        "X-LearnStack-Host",
        @"TrustedHopOptions\.HostHeaderName\b",
    ];

    private static bool ReadsAHost(string code)
    {
        var compact = SourceText.WithoutWhitespace(code);

        if (HostReads.Any(read =>
            Regex.IsMatch(compact, read, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5))))
        {
            return true;
        }

        // The typed-header helper is banned for what is read FROM it, not for itself:
        // `GetTypedHeaders()` also carries `IfMatch`, `Range` and `AcceptLanguage`, and a rule
        // that refused the call would refuse an ETag read with a message about trusted hops.
        return compact.Contains("GetTypedHeaders", StringComparison.Ordinal)
            && Regex.IsMatch(compact, @"\.Host\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Out_Of_Band_Setters_Open_Read_Only_Transactions()
    {
        // Found, not listed. The rule used to name two files, and Packet 9 added two more
        // setters — AuditConfigService and FeatureFlags — that announced app.tenant_id in a
        // transaction that was not read-only while the rule stayed green, because nothing
        // told it they existed. Read-only is what makes an out-of-band announcement
        // acceptable at all: learnstack_app holds write grants on the tables these
        // connections reach, so nothing but the statement stops a later edit from writing
        // under an announcement no request made.
        //
        // Every file that calls set_config( is one of three kinds, and the kinds are the
        // closed set Security Standards § The out-of-band setters enumerates: the ambient
        // unit of work, the audit store's two writers — which must write — and the readers,
        // each of which issues SET TRANSACTION READ ONLY before anything else. A setter this
        // finds and no kind names is a new member of a closed set, and fails until the
        // standard, and ADR-0040, say which kind it is.
        var announcing = Directory
            .EnumerateFiles(SourceScan.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            // Migrations are excluded, and the exclusion is narrow: a migration runs as
            // learnstack_migration, outside any request, and announces nothing — what it
            // carries is the policy DDL that READS these variables. A setter moved into one
            // would be a setter this rule does not see, which is why the exclusion is by
            // directory rather than by pattern.
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => Announces(SourceText.WithoutComments(File.ReadAllText(file))))
            .Select(file => Path.GetRelativePath(SourceScan.SourceRoot, file).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        announcing.Should().BeEquivalentTo(
            AmbientSetters.Concat(WritingSetters).Concat(ReadingSetters),
            "every session-variable setter is one the standard names (Security Standards "
            + "§ The out-of-band setters; ADR-0040 Amendments 3 and 7)");

        // Then the readers, method by method, from the IL. A text scan answers this one badly:
        // it cannot tell which method a statement belongs to, so a `SET TRANSACTION READ ONLY`
        // anywhere earlier in the file lends cover to an announcement in another method — and
        // it cannot tell a statement from a sentence, so a log message naming the statement
        // satisfies it. Both were measured. The IL carries neither ambiguity: the strings are
        // the ones the method actually loads, in the order it loads them.
        var unguarded = ReadingSetters
            .SelectMany(reader => UnguardedAnnouncements(ReaderAssembly(reader), ReaderType(reader)))
            .ToList();

        unguarded.Should().BeEmpty(
            "a reader announces a session variable on a connection of its own, so each "
            + "announcing method issues SET TRANSACTION READ ONLY first — the statement binds "
            + "only what follows it");
    }

    /// <summary>The probe type's metadata name, for the companion's expectations.</summary>
    private static readonly string Probe = typeof(Probes.SetterProbes).FullName!;

    [Fact]
    public void The_Setter_Scan_Can_Actually_Fail()
    {
        // Every reader is correct today, so both halves pass whether they work or not.
        const string ReadOnly = "run(\"SET TRANSACTION READ ONLY\");";
        const string Announce = "run(\"SELECT set_config('app.tenant_id', @t, true)\");";

        // The per-method check, run over probes compiled into this assembly: a reader that
        // issues the statement, one that does not, one that issues it too late, one whose
        // second announcement has none of its own, and one that only NAMES the statement in a
        // message. Each is an async method, which is where a text scan and a naive IL walk
        // both lose the body.
        var probes = UnguardedAnnouncements(
            typeof(TenancyConventionTests).Assembly.Location,
            Probe);

        probes.Should().BeEquivalentTo(
            [
                $"{Probe}.{nameof(Probes.SetterProbes.AnnouncesWithNoStatementAsync)}",
                $"{Probe}.{nameof(Probes.SetterProbes.AnnouncesBeforeTheStatementAsync)}",
                $"{Probe}.{nameof(Probes.SetterProbes.AnnouncesTwiceUnderOneStatementAsync)}",
                $"{Probe}.{nameof(Probes.SetterProbes.NamesTheStatementInAMessageAsync)}",
            ],
            "every announcing method issues the statement itself, first, and a message that "
            + "merely names it issues nothing");

        // Discovery: the spellings that announce a session variable, and the ones that read it.
        _ = ReadOnly;
        _ = Announce;
        Announces("SELECT set_config('app.tenant_id', @t, true)").Should().BeTrue();
        Announces("SELECT SET_CONFIG('app.tenant_id', @t, true)").Should().BeTrue("SQL is not case-sensitive");
        Announces("SET LOCAL app.tenant_id = '...'").Should().BeTrue();
        Announces("cmd.CommandText = \"SET LOCAL app.tenant_id = @tenant\";").Should().BeTrue();
        Announces("set session app.scope to 'tenant'").Should().BeTrue();
        Announces("throw new InvalidOperationException(\"This connection never saw \" + \"SET LOCAL app.tenant_id.\");")
            .Should().BeFalse("a message that names the statement does not issue it — it sets no value");
        Announces("SELECT current_setting('app.tenant_id', true)").Should().BeFalse("a read is not a setter");
        Announces("USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)")
            .Should().BeFalse("a policy reads the variable it guards with");
    }

    /// <summary>Whether code announces a session variable, however the statement is spelled.</summary>
    /// <remarks>
    /// The <c>SET</c> form must carry a value — <c>=</c> or <c>TO</c> — because the same words
    /// appear in prose a message hands a developer: <c>ModuleDbContextRegistration</c> throws
    /// "…it never saw SET LOCAL app.tenant_id", which announces nothing. Comments are stripped
    /// before the scan; strings are not, because a setter IS a string.
    /// </remarks>
    [GeneratedRegex(
        @"set_config\s*\(|\bSET\s+(?:LOCAL\s+|SESSION\s+)?app\.[A-Za-z_][A-Za-z0-9_]*\s*(?:=|\bTO\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SessionSetter();

    private static bool Announces(string code) => SessionSetter().IsMatch(code);

    /// <summary>The ambient unit of work, which sets the variables on the request's transaction.</summary>
    private static readonly string[] AmbientSetters =
        ["LearnStack.Infrastructure/Persistence/NpgsqlUnitOfWork.cs"];

    /// <summary>
    /// The audit store: its standalone and best-effort writes own a short transaction that
    /// must write.
    /// </summary>
    private static readonly string[] WritingSetters =
        ["LearnStack.Infrastructure.Audit/PostgresAuditStore.cs"];

    /// <summary>
    /// The readers, each in a short read-only transaction of its own: the host resolver, the
    /// organization-scope validator, and the two cached-projection loaders.
    /// </summary>
    private static readonly string[] ReadingSetters =
    [
        "LearnStack.Infrastructure/MultiTenancy/CachedHostToTenantResolver.cs",
        "LearnStack.Infrastructure/MultiTenancy/OrganizationScopeValidator.cs",
        "LearnStack.Infrastructure.Audit/AuditConfigService.cs",
        "Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/FeatureFlags.cs",
    ];

    /// <summary>
    /// The methods of a reader that announce a session variable without first issuing
    /// <c>SET TRANSACTION READ ONLY</c>.
    /// </summary>
    /// <remarks>
    /// Read from the IL, per method, in instruction order. The read-only statement must be the
    /// <b>whole</b> string a method loads, not a phrase inside one: a message that names the
    /// statement issues nothing. The order is what carries the guarantee — PostgreSQL accepts
    /// the statement after other statements, measured, and binds only what follows it, so
    /// anything issued before it ran read-write.
    /// </remarks>
    private static List<string> UnguardedAnnouncements(string assemblyPath, string typeFullName)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        var type = module.GetType(typeFullName)
            ?? throw new InvalidOperationException($"{typeFullName} is a reader this rule inspects.");

        var unguarded = new List<string>();

        foreach (var (method, declaredAs) in AnnouncingMethods(type))
        {
            var guarded = false;
            var announced = false;

            foreach (var literal in method.Body.Instructions
                .Where(instruction => instruction.OpCode == OpCodes.Ldstr)
                .Select(instruction => (string)instruction.Operand))
            {
                if (literal.Trim() == ReadOnlyStatement)
                {
                    guarded = true;
                    continue;
                }

                if (!Announces(literal))
                {
                    continue;
                }

                announced = true;

                if (!guarded)
                {
                    unguarded.Add($"{typeFullName}.{declaredAs}");
                    break;
                }

                // One statement per announcement: the next one opens its own transaction.
                guarded = false;
            }

            if (!announced)
            {
                unguarded.Add($"{typeFullName}.{declaredAs} announces nothing the IL can see");
            }
        }

        return unguarded;
    }

    /// <summary>
    /// The methods of a type that load an announcing statement — state machines included,
    /// since an async reader's body is emitted into one.
    /// </summary>
    private static IEnumerable<(MethodDefinition Method, string DeclaredAs)> AnnouncingMethods(TypeDefinition type) =>
        Il.Methods(type)
            .Where(method => method.Definition.HasBody)
            .Where(method => method.Definition.Body.Instructions.Any(instruction =>
                instruction.OpCode == OpCodes.Ldstr && Announces((string)instruction.Operand)))
            .Select(method => (method.Definition, method.DeclaredAs));

    private const string ReadOnlyStatement = "SET TRANSACTION READ ONLY";

    /// <summary>The assembly a reader's file belongs to.</summary>
    private static string ReaderAssembly(string reader)
    {
        var project = reader.Split('/') is ["Modules", _, var module, ..]
            ? module
            : reader.Split('/')[0];

        return Path.Combine(AppContext.BaseDirectory, $"{project}.dll");
    }

    /// <summary>The type a reader's file declares, by convention: one public type per file.</summary>
    private static string ReaderType(string reader) => reader switch
    {
        "LearnStack.Infrastructure/MultiTenancy/CachedHostToTenantResolver.cs" =>
            "LearnStack.Infrastructure.MultiTenancy.CachedHostToTenantResolver",
        "LearnStack.Infrastructure/MultiTenancy/OrganizationScopeValidator.cs" =>
            "LearnStack.Infrastructure.MultiTenancy.OrganizationScopeValidator",
        "LearnStack.Infrastructure.Audit/AuditConfigService.cs" =>
            "LearnStack.Infrastructure.Audit.AuditConfigService",
        "Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/FeatureFlags.cs" =>
            "LearnStack.Modules.Tenancy.Infrastructure.FeatureFlags",
        _ => throw new InvalidOperationException($"{reader} has no type mapped for the IL leg."),
    };

    [Fact]
    public void Tenant_Scope_Widening_Is_Never_Set_From_Request_Input()
    {
        // app.scope = 'tenant' widens an organization-scoped read to the whole tenant, and
        // ADR-0036 § The reconciliation matrix derives it from the actor's role plus a
        // declared tenant-wide operation — never from a header, a query parameter, a cookie
        // or a body. The role arrives in Phase 03; until then nothing sets the variable at
        // all, and "nothing sets it" is the strongest form of "nothing sets it from request
        // input". The setter Phase 03 writes adds its own path to ScopeSetterSites, which is
        // where the role derivation gets reviewed.
        //
        // The premise first, or the rule guards a variable nothing reads: the policies do
        // read it.
        SourceScan.FilesContaining(SourceScan.SourceRoot, "current_setting('app.scope'", except: null)
            .Should().NotBeEmpty("the premise: a row-security policy reads app.scope");

        typeof(ITenantContext).GetMembers()
            .Where(member => member.Name.Contains("Scope", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty(
                "the tenant context carries no scope, so no request input can reach one through it");

        Directory.EnumerateFiles(SourceScan.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .Where(file => ScopeSetter().IsMatch(SourceText.WithoutComments(File.ReadAllText(file))))
            .Select(file => Path.GetRelativePath(SourceScan.SourceRoot, file).Replace('\\', '/'))
            .Where(file => !ScopeSetterSites.Contains(file))
            .Should().BeEmpty(
                "nothing sets app.scope until Phase 03 derives it from a role "
                + "(ADR-0036 § The reconciliation matrix)");
    }

    [Fact]
    public void The_Scope_Setter_Scan_Can_Actually_Fail()
    {
        ScopeSetter().IsMatch("SELECT set_config('app.scope', 'tenant', true)").Should().BeTrue();
        ScopeSetter().IsMatch("SELECT set_config ( 'app.scope' , @scope , true )").Should().BeTrue();
        ScopeSetter().IsMatch("SET LOCAL app.scope = 'tenant'").Should().BeTrue();
        ScopeSetter().IsMatch("set session app.scope to 'tenant'").Should().BeTrue();
        ScopeSetter().IsMatch("OR current_setting('app.scope', true) = 'tenant'").Should().BeFalse(
            "a policy reading the variable is its subject, not a setter");
    }

    /// <summary>Files allowed to set <c>app.scope</c>, by path under <c>backend/src</c>. Empty until Phase 03.</summary>
    private static readonly HashSet<string> ScopeSetterSites = new(StringComparer.Ordinal);

    [GeneratedRegex(
        @"set_config\s*\(\s*'app\.scope'|\bSET\s+(?:LOCAL\s+|SESSION\s+)?app\.scope\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ScopeSetter();

    [Fact]
    public void Host_Resolution_Makes_No_Outbound_Calls()
    {
        // The structural half of the rule, until Phase 02c gives a Hub client to register as a
        // throwing stub. Host resolution runs on every anonymous page load before a tenant is
        // known, and ADR-0034 forbids it to call the Hub, so that a Hub outage cannot take
        // tenant sites down. The resolver takes ports — a cache, a data source — and a port
        // is governed where it is declared; what it must not take is an HTTP client, a gRPC
        // channel or a Hub client, directly or through a LearnStack type it depends on.
        var resolvers = ProductionAssemblies.All()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && typeof(IHostToTenantResolver).IsAssignableFrom(type))
            .ToList();

        resolvers.Should().Equal(
            [typeof(CachedHostToTenantResolver)],
            "the premise: one resolver, and it is the one this rule inspects");

        OutboundDependencies(typeof(CachedHostToTenantResolver)).Should().BeEmpty(
            "host resolution reads platform_host_to_tenant and nothing else (ADR-0034)");

        // And the bodies, not only the declarations: `new HttpClient()` inside a method is a
        // call out that no walk over constructor parameters and fields can see.
        CallsOutInIl(typeof(CachedHostToTenantResolver)).Should().BeEmpty(
            "the resolver's own code names no network type either");
    }

    [Fact]
    public void The_Outbound_Dependency_Scan_Can_Actually_Fail()
    {
        // The resolver is clean, so the rule above passes whether its walk works or not. One
        // probe takes an HTTP client directly; the other hides a client factory one
        // LearnStack type down, which is where a real one would arrive.
        OutboundDependencies(typeof(HttpResolverProbe)).Should().Contain(typeof(HttpClient).FullName);
        OutboundDependencies(typeof(IndirectResolverProbe)).Should().Contain(typeof(HttpMessageHandler).FullName);
        OutboundDependencies(typeof(StaticResolverProbe)).Should().Contain(typeof(HttpClient).FullName,
            "a static field is where an HttpClient is canonically held");
        OutboundDependencies(typeof(InheritingResolverProbe)).Should().Contain(typeof(HttpClient).FullName,
            "and a base class is where a second resolver would put what it shares");
        OutboundDependencies(typeof(LocatingResolverProbe)).Should().Contain("System.IServiceProvider",
            "a service provider answers for every registered type, this rule's subjects included");
        CallsOutInIl(typeof(BodyResolverProbe)).Should().NotBeEmpty(
            "a client constructed inside a method is a call out the declarations do not show");
        CallsOutInIl(typeof(LocatingResolverProbe)).Should().BeEmpty(
            "and the IL leg answers about network types, not about the service provider the "
            + "declaration leg refuses");
    }

    /// <summary>
    /// Every type that can call out of the process reachable from a type's constructor
    /// parameters and fields, following LearnStack-owned classes transitively.
    /// </summary>
    private static List<string> OutboundDependencies(Type root)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<Type>();
        var pending = new Queue<Type>([root]);

        while (pending.TryDequeue(out var type))
        {
            if (!visited.Add(type))
            {
                continue;
            }

            // Constructor parameters and FIELDS — instance and static, declared here and
            // inherited. A `private static readonly HttpClient` is the canonical way to hold
            // one, and a base class is where a second resolver would put what it shares; a
            // walk over instance fields alone passed both, measured.
            var declared = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType))
                .Concat(Fields(type).Select(field => field.FieldType));

            foreach (var dependency in declared.SelectMany(Unwrap))
            {
                if (CallsOut(dependency))
                {
                    found.Add(dependency.FullName ?? dependency.Name);
                }
                else if (dependency is { IsClass: true } && dependency.Namespace?.StartsWith("LearnStack.", StringComparison.Ordinal) == true)
                {
                    pending.Enqueue(dependency);
                }
            }
        }

        return [.. found];
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        if (type.HasElementType)
        {
            foreach (var inner in Unwrap(type.GetElementType()!))
            {
                yield return inner;
            }
        }

        foreach (var argument in type.IsGenericType ? type.GetGenericArguments() : [])
        {
            foreach (var inner in Unwrap(argument))
            {
                yield return inner;
            }
        }
    }

    /// <summary>
    /// The network namespaces a type names anywhere in its IL — signatures, bodies, and the
    /// state machines its async methods compile into.
    /// </summary>
    private static List<string> CallsOutInIl(Type type)
    {
        using var module = ModuleDefinition.ReadModule(type.Assembly.Location);

        // Cecil spells a nested type with `/` where reflection uses `+`.
        var definition = module.GetType(type.FullName!.Replace('+', '/'))
            ?? throw new InvalidOperationException($"{type.FullName} is the type this rule inspects.");

        return [.. OutboundNamespaces
            .Where(space => Il.NamesNamespace(definition, space))
            .Select(space => $"{type.FullName} names {space}")];
    }

    /// <summary>The namespaces a type that talks to the network draws from.</summary>
    private static readonly string[] OutboundNamespaces =
        ["System.Net.Http", "System.Net.Sockets", "System.Net.WebSockets", "Grpc", "LearnStack.Infrastructure.Hub"];

    /// <summary>Every field a type holds, inherited and static ones included.</summary>
    private static IEnumerable<FieldInfo> Fields(Type type)
    {
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            foreach (var field in current.GetFields(
                BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic))
            {
                yield return field;
            }
        }
    }

    /// <summary>
    /// A type that can reach the network — or resolve something that can.
    /// </summary>
    /// <remarks>
    /// <see cref="IServiceProvider"/> is on the list because it answers for every registered
    /// type: a resolver holding one can obtain a Hub client at the call site, and no walk over
    /// its declared dependencies would see it.
    /// </remarks>
    private static bool CallsOut(Type type) =>
        type.Namespace is { } space
            && (space.StartsWith("System.Net.Http", StringComparison.Ordinal)
                || space.StartsWith("System.Net.Sockets", StringComparison.Ordinal)
                || space.StartsWith("System.Net.WebSockets", StringComparison.Ordinal)
                || space.StartsWith("Grpc", StringComparison.Ordinal))
        || type.Name.Contains("HubClient", StringComparison.Ordinal)
        || type == typeof(IServiceProvider)
        || type.Name is "IServiceScopeFactory" or "IServiceScope";

    /// <summary>Takes an HTTP client directly. Never constructed.</summary>
    private sealed class HttpResolverProbe(HttpClient client)
    {
        public HttpClient Client { get; } = client;
    }

    /// <summary>Takes a LearnStack type that takes a handler. Never constructed.</summary>
    private sealed class IndirectResolverProbe(IndirectResolverProbe.Transport transport)
    {
        public Transport Via { get; } = transport;

        public sealed class Transport(HttpMessageHandler handler)
        {
            public HttpMessageHandler Handler { get; } = handler;
        }
    }

    /// <summary>Holds an HTTP client in a static field. Never constructed.</summary>
    private sealed class StaticResolverProbe
    {
        private static readonly HttpClient Shared = new();

        public static HttpClient Client => Shared;
    }

    /// <summary>Holds one in a field a derived type inherits. Never constructed.</summary>
    private class InheritingResolverProbeBase(HttpClient client)
    {
        private readonly HttpClient _client = client;

        protected HttpClient Client => _client;
    }

    /// <summary>Inherits the client above, and declares nothing itself. Never constructed.</summary>
    private sealed class InheritingResolverProbe() : InheritingResolverProbeBase(new HttpClient());

    /// <summary>Builds a client inside a method rather than taking one. Never constructed.</summary>
    private sealed class BodyResolverProbe
    {
        public static async Task<string> ResolveAsync(string host)
        {
            using var client = new HttpClient();

            return await client.GetStringAsync(new Uri($"https://hub.example/{host}")).ConfigureAwait(false);
        }
    }

    /// <summary>Takes a service provider, which answers for anything. Never constructed.</summary>
    private sealed class LocatingResolverProbe(IServiceProvider services)
    {
        public IServiceProvider Services { get; } = services;
    }

    [Fact]
    public void Tenant_Headers_Are_Never_A_Resolution_Source()
    {
        // The header is an assertion the API compares against its own answer.
        // The moment a second file reads it, the question "did this select a
        // tenant, or check one?" stops having one answer.
        Offenders(
                except: Path.Combine("Tenancy", "TenantAssertionMiddleware.cs"),
                banned: ["X-Tenant-Id", "X-Organization-Id"])
            .Should().BeEmpty(
                "X-Tenant-Id and X-Organization-Id are compared, never resolved from "
                + "(ADR-0036 § The reconciliation matrix)");
    }

    [Fact]
    public void Assertion_Recorder_Is_The_Only_Mismatch_Writer()
    {
        // A rejected assertion is a security event. One writer means one place that
        // decides the metric's label cardinality — and, since Packet 9, one place that
        // decides which tenant the row carries, which is the whole of why the row is safe
        // to write at all.
        Offenders(
                except: Path.Combine("Tenancy", "LoggingTenantAssertionRecorder.cs"),
                banned: [
                    "learnstack_tenant_assertion_mismatch_total",
                    "learnstack_tenant_assertion_unresolved_total",
                ])
            .Should().BeEmpty(
                "only an ITenantAssertionRecorder writes a tenant-assertion mismatch "
                + "(ADR-0036 § Recording a rejected assertion)");
    }

    [Fact]
    public void Assertion_Recorder_Is_The_Only_Writer_Of_Its_Audit_Slugs()
    {
        // The other half this rule always claimed and, until the slugs existed, could not
        // check: "a log, a metric OR IAuditStore". The counter names above cannot catch a
        // second writer that goes straight to the store, and that writer is the dangerous
        // one — the row's tenant is what keeps an anonymous caller from choosing whose
        // audit log grows, and a second composer is a second chance to get it wrong.
        // Scanned across ALL of backend/src, not just LearnStack.Api. Measured: planting
        // the burst slug in LearnStack.Modules.Tenancy.Application passed the first
        // version of this rule — and a module is precisely where IAuditStore is reachable
        // from a handler, so the narrow scan exempted the dangerous half. Its exemption
        // named a path outside its own scan root, so it could never match either: coverage
        // that reads as real and is not.
        //
        // The declaring catalogue source is the one legitimate second namer. Declaring a
        // slug is not writing a row, and removing that exemption turns this red — measured.
        SourceOffenders(
                banned: [
                    "tenancy.tenant_assertion.reject",
                    "tenancy.tenant_assertion.anonymous_burst",
                ],
                except: [
                    Path.Combine("LearnStack.Api", "Tenancy", "AuditingTenantAssertionRecorder.cs"),
                    Path.Combine("Audit", "TenancyAuditCatalogSource.cs"),
                ])
            .Should().BeEmpty(
                "only AuditingTenantAssertionRecorder names the two assertion slugs "
                + "(ADR-0036 § Recording a rejected assertion)");
    }

    [Fact]
    public void The_Slug_Scan_Finds_The_Files_It_Exempts()
    {
        // The rule above passes when NOTHING offends, which is also what it does when the
        // exemption logic is broken open — measured: replacing the suffix match with a
        // tautology left it green, because no third file in backend/src names either slug.
        // A rule that cannot distinguish "clean" from "blind" is the exact defect its own
        // first draft had, one layer up.
        //
        // So: run the same scan with NO exemptions and require it to find precisely the
        // two files the rule exempts. That pins the scanner's reach and makes the
        // exemptions load-bearing in both directions.
        var found = SourceOffenders(
            banned: [
                "tenancy.tenant_assertion.reject",
                "tenancy.tenant_assertion.anonymous_burst",
            ],
            except: []);

        found.Should().HaveCount(2);
        found.Should().ContainSingle(path => path.EndsWith(
            Path.Combine("Tenancy", "AuditingTenantAssertionRecorder.cs"), StringComparison.Ordinal));
        found.Should().ContainSingle(path => path.EndsWith(
            Path.Combine("Audit", "TenancyAuditCatalogSource.cs"), StringComparison.Ordinal));
    }

    [Fact]
    public void Assertion_Budget_Does_Not_Depend_On_ICacheService()
    {
        // The anonymous burst counter is exactly the thing someone reaches for a
        // cache to share across instances, and a cache outage must not decide
        // whether a MUST-class security event is recorded.
        //
        // This began as a tripwire because ICacheService did not exist. Packet 5
        // ships it, so the rule is now what the catalogue promised: a real
        // dependency check as well as a text scan. Both are kept — reflection
        // catches an injected dependency, the scan catches a service-locator
        // resolve, and neither sees the other's case.
        Injectors().Should().BeEmpty(
            "no type under Tenancy takes an ICacheService "
            + "(ADR-0036 § Recording a rejected assertion)");

        Offenders(except: null, banned: ["ICacheService"], folder: "Tenancy")
            .Should().BeEmpty(
                "and none resolves one by name either "
                + "(ADR-0036 § Recording a rejected assertion)");
    }

    [Theory]
    // `required: true` for the type that exists — a rule accepting zero
    // declarations of `Organization` would stay green if the aggregate were
    // deleted, which is the vacuity this catalogue calls out generally.
    // `OrganizationBranding` is genuinely zero-or-one: it ships with the token
    // merge in Phase 06, and stating the rule now is what stops the first one
    // landing in the wrong module.
    [InlineData("Organization", true)]
    [InlineData("OrganizationBranding", false)]
    public void Organization_Aggregate_Declared_In_Tenancy_Domain(string typeName, bool required)
    {
        // ADR-0017's original sample put Organization in Identity; Amendment 2
        // moved it to Tenancy, and Identity now holds OrganizationId by value and
        // reads organization data through an application contract. A second
        // declaration is how the two drift back apart.
        //
        // The assembly set is ENUMERATED rather than discovered. A rule that
        // scanned loaded assemblies would silently skip the module nobody
        // referenced, and pass vacuously — the failure
        // Meta_NetArchTest_DetectsAPlantedViolation guards against generally.
        //
        // OrganizationBranding does not exist yet (Phase 06 ships it with the
        // token merge). The rule still runs: "exactly one, in Tenancy" is
        // satisfied by none as well as by one, and stating it now is what stops
        // the first one landing in the wrong module.
        var declarations = ModuleDomainAssemblies()
            .SelectMany(assembly => assembly.GetTypes()
                .Where(type => type.Name == typeName)
                .Select(type => $"{type.FullName} in {assembly.GetName().Name}"))
            .ToList();

        if (required)
        {
            declarations.Should().ContainSingle(
                $"{typeName} is declared exactly once across every module Domain "
                + "assembly (ADR-0017 Amendment 2)");
        }
        else
        {
            declarations.Should().HaveCountLessThanOrEqualTo(1,
                $"{typeName} does not exist yet; when it does it is declared once, "
                + "in Tenancy (ADR-0017 Amendment 2)");
        }

        declarations
            .Where(d => !d.EndsWith("LearnStack.Modules.Tenancy.Domain", StringComparison.Ordinal))
            .Should().BeEmpty($"and Tenancy is where {typeName} is declared");
    }

    /// <summary>
    /// The <c>Domain</c> assembly of every module, by name.
    /// </summary>
    private static IEnumerable<Assembly> ModuleDomainAssemblies() =>
        Modules.Names.Select(module => Assembly.Load($"LearnStack.Modules.{module}.Domain"));

    /// <summary>
    /// Types in the <c>LearnStack.Api.Tenancy</c> namespace that take an
    /// <see cref="LearnStack.SharedKernel.Caching.ICacheService"/> as a
    /// constructor parameter or hold one in a field.
    /// </summary>
    private static List<string> Injectors()
    {
        var cache = typeof(LearnStack.SharedKernel.Caching.ICacheService);

        return typeof(LearnStack.Api.Versioning.ApiVersioningExtensions).Assembly
            .GetTypes()
            .Where(type => type.Namespace?.StartsWith(
                "LearnStack.Api.Tenancy", StringComparison.Ordinal) == true)
            .Where(type =>
                type.GetConstructors().Any(constructor =>
                    constructor.GetParameters().Any(p => cache.IsAssignableFrom(p.ParameterType)))
                || type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic
                        | BindingFlags.Public)
                    .Any(field => cache.IsAssignableFrom(field.FieldType)))
            .Select(type => type.FullName!)
            .ToList();
    }

    /// <summary>
    /// Files anywhere under <c>backend/src</c> that mention a banned literal in code.
    /// </summary>
    /// <remarks>
    /// The wide sibling of <see cref="Offenders"/>, for a rule whose subject is not the
    /// API project. Exemptions match as path SUFFIXES, so a caller names as much of the
    /// path as it needs to be unambiguous and no more.
    /// </remarks>
    private static List<string> SourceOffenders(
        IReadOnlyList<string> banned, IReadOnlyList<string> except)
    {
        var root = RepositoryPaths.BackendSrc();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);

            if (relative.Split(Path.DirectorySeparatorChar) is var segments
                && (segments.Contains("obj") || segments.Contains("bin")))
            {
                continue;
            }

            if (except.Any(exempt => relative.EndsWith(exempt, StringComparison.Ordinal)))
            {
                continue;
            }

            var code = SourceText.WithoutComments(File.ReadAllText(file));

            if (banned.Any(literal => code.Contains(literal, StringComparison.Ordinal)))
            {
                offenders.Add(relative);
            }
        }

        return offenders;
    }

    /// <summary>
    /// Files under <c>LearnStack.Api</c> that mention a banned literal in code.
    /// </summary>
    /// <remarks>
    /// Whitespace is removed from both the source and the literal before the
    /// search, so a violation cannot hide behind a line break — measured, the
    /// first version of this scan was per-line, and a <c>context.Request</c>
    /// whose <c>.Host.Value</c> sat on the next line passed it clean. Comments
    /// are removed first, because every file here argues in prose about the very
    /// literal it is forbidden to write.
    /// </remarks>
    private static List<string> Offenders(
        string? except,
        IReadOnlyList<string> banned,
        string? folder = null,
        IReadOnlyList<string>? alsoExcept = null)
    {
        var exempt = new List<string>();
        if (except is not null)
        {
            exempt.Add(except);
        }

        if (alsoExcept is not null)
        {
            // The second exemption list is for the file that DECLARES a banned spelling as
            // opposed to reading it. `TrustedHopOptions` owns the header names; banning the
            // name without exempting its own declaration would make the rule unsatisfiable
            // rather than strict.
            exempt.AddRange(alsoExcept);
        }

        return
        [
            .. from source in ApiSources(folder, exempt)
               from literal in banned
               where source.Code.Contains(SourceText.WithoutWhitespace(literal), StringComparison.Ordinal)
               select $"{source.Relative} contains '{literal}'",
        ];
    }

    /// <summary>
    /// Every source file under <c>LearnStack.Api</c> — or one folder of it — with its
    /// comments stripped and its whitespace removed.
    /// </summary>
    /// <remarks>
    /// Exemptions are compared as paths, not as bare names: two files may share a name in
    /// different folders, and excluding both because one is exempt is how a rule quietly
    /// stops covering half of what it names.
    /// </remarks>
    private static IEnumerable<(string Relative, string Code)> ApiSources(
        string? folder = null, IReadOnlyList<string>? except = null)
    {
        var root = Path.Combine(RepositoryPaths.BackendSrc(), "LearnStack.Api");

        if (folder is not null)
        {
            root = Path.Combine(root, folder);
        }

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);

            if (relative.Split(Path.DirectorySeparatorChar) is var segments
                && (segments.Contains("obj") || segments.Contains("bin")))
            {
                continue;
            }

            if (except is not null
                && except.Any(allowed => relative.Equals(allowed, StringComparison.Ordinal)))
            {
                continue;
            }

            yield return (
                relative,
                SourceText.WithoutWhitespace(SourceText.WithoutComments(File.ReadAllText(file))));
        }
    }
    [Fact]
    public void Resolving_Host_Is_Set_In_One_Place()
    {
        // app.resolving_host is the fourth canonical session variable and the only
        // one with a single setter: the resolver announces the host it is about to
        // look up, and the policy on platform_host_to_tenant admits exactly that
        // row. A second setter is a second announcement, on the one table read
        // before any tenant context exists — the one place a widened read is not
        // already caught by app.tenant_id being NULL.
        //
        // Its own scan rather than the Offenders helper above, which is scoped to
        // LearnStack.Api: the sole setter lives in LearnStack.Infrastructure, so a
        // rule that only looked at the Api project would be green by construction.
        //
        // The SETTER spelling only. Banning the bare literal `app.resolving_host`
        // fails on the migration's own policy DDL, which must name the variable in
        // order to read it.
        const string Setter = "set_config('app.resolving_host'";
        var SoleSetter = Path.Combine(
            "LearnStack.Infrastructure", "MultiTenancy", "CachedHostToTenantResolver.cs");

        var offenders = Directory
            .EnumerateFiles(RepositoryPaths.BackendSrc(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // The full relative path, not a filename suffix. `EndsWith` on the bare name
            // exempts anything ending in it — `NoopCachedHostToTenantResolver.cs`,
            // `TestCachedHostToTenantResolver.cs` — so a second setter could be added in a
            // file the rule silently treats as the sole one.
            .Where(file => !string.Equals(
                Path.GetRelativePath(RepositoryPaths.BackendSrc(), file),
                SoleSetter,
                StringComparison.Ordinal))
            .Where(file => SourceText.WithoutComments(File.ReadAllText(file))
                .Contains(Setter, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(RepositoryPaths.BackendSrc(), file))
            .ToList();

        offenders.Should().BeEmpty(
            "CachedHostToTenantResolver is the sole setter of app.resolving_host "
            + "(Security Standards § Tenant Context)");
    }
}
