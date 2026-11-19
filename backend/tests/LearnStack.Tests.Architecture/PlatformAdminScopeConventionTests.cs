using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Api.Messaging;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The rules that keep the one <c>BYPASSRLS</c> credential reachable from one place.
/// </summary>
public sealed partial class PlatformAdminScopeConventionTests
{
    /// <summary>
    /// Every <c>LearnStack.*</c> assembly under <c>backend/src</c>, loaded without a
    /// null filter.
    /// </summary>
    /// <remarks>
    /// Derived from the tree for the reason the sibling rules record: a listed sweep
    /// silently stops asserting where the list stops, and a rule that counts a hole
    /// cannot afford to. An assembly that fails to load is a missing project reference,
    /// and the right outcome is a red build naming it rather than a smaller scan.
    /// </remarks>
    private static IEnumerable<Assembly> ProductionAssemblies() => Architecture.ProductionAssemblies.All();

    private const string ScopeFile = "LearnStack.Infrastructure/MultiTenancy/PlatformAdminScope.cs";

    private const string CompositionFile =
        "LearnStack.Api/Composition/PersistenceCompositionExtensions.cs";

    // A distinct keyed credential, confined by ADR-0046. Naming it explicitly
    // preserves this global resolution fence while permitting its approved root.
    private const string OutboxCompositionFile =
        "LearnStack.Api/Composition/OutboxCompositionExtensions.cs";
    private const string InvitationCompositionFile =
        "LearnStack.Api/Composition/InvitationEmailComposition.cs";
    private const string OutboxWorkerFile = "LearnStack.Api/Messaging/OutboxProcessor.cs";
    private const string DeliveryWorkerFile = "LearnStack.Api/Messaging/DeliveryProcessor.cs";
    private const string JobRuntimeRegistrationFile =
        "LearnStack.Infrastructure/Jobs/BackgroundJobRuntimeRegistration.cs";

    [Fact]
    public void Platform_DataSource_Resolved_Only_By_PlatformAdminScope()
    {
        // Three legs, because "only PlatformAdminScope may resolve it" is not one claim.
        //
        // Leg 1 — nothing but the scope names the keyed registration. This is the repo's
        // first keyed DI registration anywhere, so there is no house pattern to lean on
        // and the whole boundary is this scan. The key being a public const is not a
        // weakness: GetKeyedServices(KeyedService.AnyKey) reaches a keyed registration
        // whatever the key is spelled, so hiding the string would buy a reader nothing.
        var resolvers = SourceScan.FilesContaining(
            SourceScan.SourceRoot, "FromKeyedServices", except: null)
            .Concat(SourceScan.FilesContaining(
                SourceScan.SourceRoot, "GetRequiredKeyedService", except: null))
            .Concat(SourceScan.FilesContaining(
                SourceScan.SourceRoot, "GetKeyedService", except: null))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        resolvers.Should().BeEquivalentTo(
            [ScopeFile, OutboxCompositionFile, OutboxWorkerFile, DeliveryWorkerFile, JobRuntimeRegistrationFile],
            "the platform scope and the separately confined dispatcher are the only "
            + "credential consumers, alongside their composition roots");

        typeof(OutboxProcessor).GetConstructors().SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.GetCustomAttribute<FromKeyedServicesAttribute>())
            .OfType<FromKeyedServicesAttribute>().Should().ContainSingle().Which.Key
            .Should().Be(OutboxDispatcherDataSource.Key,
                "permitting the worker file must not permit the platform credential");
        File.ReadAllText(Path.Combine(SourceScan.SourceRoot, OutboxWorkerFile))
            .Should().NotContain("PlatformAdmin");

        typeof(DeliveryProcessor).GetConstructors().SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.GetCustomAttribute<FromKeyedServicesAttribute>())
            .OfType<FromKeyedServicesAttribute>().Should().ContainSingle().Which.Key
            .Should().Be(OutboxDispatcherDataSource.Key,
                "the subscriber scheduler reuses only the confined dispatcher credential");
        File.ReadAllText(Path.Combine(SourceScan.SourceRoot, DeliveryWorkerFile))
            .Should().NotContain("PlatformAdmin");

        // ADR-0047's closed job factories resolve the dispatcher key only;
        // the companion compiled credential census pins each exact caller/key.
        File.ReadAllText(Path.Combine(SourceScan.SourceRoot, JobRuntimeRegistrationFile))
            .Should().NotContain("PlatformAdmin");

        // Leg 2 — IConfiguration.GetConnectionString is confined to the three enumerated
        // API composition files. Not "every credential is read here": the design-time
        // DbContext factories read ConnectionStrings__Migration straight from the
        // environment, deliberately, because dotnet ef builds no host. What this pins is
        // that a second reader of ConnectionStrings:PlatformAdmin would be a second data
        // source built without the initializer asserting the role actually bypasses —
        // and the symptom of that is not an error but fewer rows.
        //
        // The needle is the READ, not the word "PlatformAdmin": that word is also the
        // type names, so scanning for it flagged the two SharedKernel contracts, which
        // is a rule matching its own vocabulary rather than a credential.
        SourceScan.FilesContaining(SourceScan.SourceRoot, "GetConnectionString", except: null)
            .Should().BeEquivalentTo(
                [CompositionFile, OutboxCompositionFile, InvitationCompositionFile, "LearnStack.Api/Composition/ProviderEventComposition.cs"],
                "each credential has an explicitly confined composition root");

        File.ReadAllText(Path.Combine(SourceScan.SourceRoot, OutboxCompositionFile))
            .Should().NotContain("PlatformAdmin", "outbox composition must never read the platform credential");
        File.ReadAllText(Path.Combine(SourceScan.SourceRoot, InvitationCompositionFile))
            .Should().NotContain("PlatformAdmin", "SMTP custody reuses only the application credential; the compiled census pins the exact read and private source factory");
        File.ReadAllText(Path.Combine(SourceScan.SourceRoot, "LearnStack.Api/Composition/ProviderEventComposition.cs"))
            .Should().NotContain("PlatformAdmin", "provider intake has only its separate machine credential; the compiled census pins that exact factory");

        // Leg 2b — the OTHER way to read the same value. GetConnectionString(name) is
        // sugar for configuration[$"ConnectionStrings:{name}"], so the indexer form
        // carries none of Leg 2's needle — and it is not a contrived evasion: the idiom
        // is already used four times in this solution, for Telemetry:* and for
        // Deployment:Mode. A contributor reaching for the familiar pattern would trip
        // none of these rules, could build a raw NpgsqlDataSourceBuilder with no
        // RequireBypassRole initializer — so even a wrong-role credential would pass
        // silently — and would skip the gate, the reason and the log line entirely.
        // Asserted separately from Leg 2 so a failure names the idiom that caused it.
        SourceScan.FilesContaining(
            SourceScan.SourceRoot, "ConnectionStrings:PlatformAdmin", except: CompositionFile)
            .Should().BeEmpty(
                "the indexer form reads the same credential as GetConnectionString and "
                + "is the one spelling the other legs cannot see");

        // Leg 3 — the scan itself found something. An allow-list assertion is satisfied
        // by an empty result, so a scan that silently stopped matching would read as
        // compliance; this is the failure a sibling rule records in its own words, that a
        // narrowed sweep does not fail but passes over less code.
        resolvers.Should().NotBeEmpty("a scan matching nothing would satisfy leg 1 vacuously");
    }

    [Fact]
    public void No_IgnoreQueryFilters_Outside_PlatformAdminScope()
    {
        // A live negative. Nothing in backend/src calls IgnoreQueryFilters today, and the
        // rule exists so the first call is a deliberate edit here rather than a quiet one
        // there — the query filters are one of the four isolation layers, and a call site
        // that removes them without going through the audited path removes a layer with
        // no record that it happened.
        //
        // A path check, not a marker: there is deliberately no escape-hatch comment to
        // write, because a comment is what a reviewer skims past.
        SourceScan.FilesContaining(SourceScan.SourceRoot, "IgnoreQueryFilters", except: ScopeFile)
            .Should().BeEmpty(
                "cross-tenant reads go through IPlatformAdminScope, which uses a "
                + "separately-credentialed connection rather than removing a filter");
    }

    [Fact]
    public void PlatformAdminScope_Entry_Requires_Platform_Permission()
    {
        // Conjunct A — the scope takes an admission and nothing else. ADR-0036 asks for the
        // permission to be "checked before the scope opens"; ADR-0101 § 7 makes that a type:
        // the only way to hold a PlatformAdmission is to have passed the gate, so entry
        // cannot happen unadmitted. The ORDER — before the credential is touched — is asserted
        // behaviourally in PlatformAdminGateTests, which a structural test cannot see.
        typeof(IPlatformAdminScope).GetMethod(nameof(IPlatformAdminScope.EnterAsync))!
            .GetParameters()[0].ParameterType.Should().Be<PlatformAdmission>(
                "entry takes the gate's proof and no caller-supplied reason or flag");

        // Conjunct B — the admission census is closed. Swept across every production
        // assembly, not just the one declaring the types: a permissive minter or gate would
        // be registered somewhere else, for a demo, and that is exactly where a narrowed scan
        // would not look. Test assemblies are not scanned, so a test double does not widen it.
        // Abstract subclasses count: a census that skipped them would let a permissive abstract minter
        // stand in the assembly for a test or a later edit to complete. Full names, not simple ones,
        // so a lookalike in another namespace is a different (and failing) entry.
        var types = ProductionAssemblies().SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsInterface).ToList();

        FullNames(types, typeof(PlatformAdmission.Minter)).Should().BeEquivalentTo(
            ["LearnStack.Infrastructure.PlatformOperator.PlatformOperatorGate"],
            "only the gate mints an admission, after it has evaluated a grant");
        FullNames(types, typeof(PlatformOperatorWitness.Minter)).Should().BeEquivalentTo(
            ["LearnStack.Api.Authentication.PlatformOperatorTokenProfile+WitnessMinter"],
            "only the operator token profile mints a witness, through a private nested minter; the rebind "
            + "tool joins in Step 3 by an edit to this list, not by accident");
        FullNames(types, typeof(IPlatformOperatorGate)).Should().BeEquivalentTo(
            ["LearnStack.Infrastructure.PlatformOperator.PlatformOperatorGate"],
            "a second gate is how a permissive default ships");
        FullNames(types, typeof(IPlatformOperatorGrantSource)).Should().BeEquivalentTo(
            ["LearnStack.Infrastructure.PlatformOperator.ConfigurationPlatformOperatorGrantSource"],
            "a second grant source is how a permissive grant ships; the table adapter joins on its trigger");
    }

    [Fact]
    public void The_Operator_Token_Profile_Is_Called_From_The_Authentication_Boundary_Only()
    {
        // The witness is evidence that a token passed the profile. TryRead is the only door to the
        // minter, so who may call it is the question: the scheme's TokenValidated event, which lives
        // beside it, and nothing else in production code. The real-realm test and the offline rebind
        // tool (Step 3) are the sanctioned further callers, the latter added here by name.
        const string Boundary = "LearnStack.Api/Authentication/PlatformOperatorAuthentication.cs";

        SourceScan.FilesContaining(SourceScan.SourceRoot, "PlatformOperatorTokenProfile", except: null)
            .Should().Equal([Boundary], "no other source file may name the profile that mints a witness");
        SourceScan.FilesContaining(SourceScan.SourceRoot, "PlatformOperatorTokenProfile.TryRead(", except: null)
            .Should().Equal([Boundary]);
        SourceScan.FilesContaining(SourceScan.SourceRoot, "PlatformOperatorWitness.Minter", except: null)
            .Should().Equal([Boundary], "the witness minter base is named by its one nested subclass");
    }

    [Fact]
    public void Only_The_Operator_Services_Hold_The_Platform_Scope()
    {
        // The scope is reachable from the services that perform an admitted operator act and
        // from nowhere else. A request handler holding it would enter a cross-tenant scope
        // from a tenant request path, which the marker rules forbid for a platform entry.
        SourceScan.FilesContaining(SourceScan.SourceRoot, "IPlatformAdminScope", except: null)
            .Should().BeEquivalentTo(
                [
                    "LearnStack.SharedKernel/Tenancy/IPlatformAdminScope.cs",
                    ScopeFile,
                    CompositionFile,
                    KillswitchWriterFile,
                ],
                "each new operator service is added here deliberately, with its denied proof");
    }

    [Fact]
    public void Only_The_Operator_Onboarding_Routes_Run_An_Operator_Act()
    {
        // ADR-0102: the executor is the one place an operator's tenant reaches the pipeline, and an admission is
        // the only thing that lets it run. The files that name it in code are the executor, the registration beside
        // the source it enters and the onboarding routes, and nothing else: a second caller is a second route to an
        // act, and is added here deliberately with its own denied proof.
        SourceScan.FilesContaining(SourceScan.SourceRoot, "PlatformOperatorActExecutor", except: null)
            .Should().BeEquivalentTo(
                [
                    "LearnStack.Infrastructure/PlatformOperator/PlatformOperatorActExecutor.cs",
                    "LearnStack.Infrastructure/PlatformOperator/PlatformOperatorOperationSource.cs",
                    "LearnStack.Api/Platform/PlatformOperatorEndpoints.Onboarding.cs",
                ],
                "each new caller of an operator act is added here deliberately, with its denied proof");
    }

    private const string KillswitchWriterFile =
        "Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/KillswitchToggle.cs";

    [Fact]
    public void Writes_To_The_Killswitch_Table_Are_Confined_To_The_Writer()
    {
        // ADR-0101 § 13: the overlay reads the table, so a rule on every mention would be wrong; the
        // confinement is on the write verbs, in any case, with an optional schema and quotes. The
        // writer inserts the default row, locks it and updates it, so those two verbs must be present
        // and DELETE and TRUNCATE must stay absent everywhere (the tests that clean up use the scope,
        // outside backend/src).
        var offenders = new List<string>();
        var sawWriter = false;
        foreach (var file in Directory.EnumerateFiles(SourceScan.SourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(SourceScan.SourceRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/') is var segments && (segments.Contains("obj") || segments.Contains("bin")))
            {
                continue;
            }

            var code = SourceText.WithoutComments(File.ReadAllText(file));
            if (KillswitchWrite().IsMatch(code))
            {
                offenders.Add(relative);
                sawWriter |= relative == KillswitchWriterFile;
            }
        }

        sawWriter.Should().BeTrue("the premise: the sweep read the writer's own statements");
        offenders.Should().Equal([KillswitchWriterFile], "the writer alone writes the table");

        var writer = SourceText.WithoutComments(File.ReadAllText(
            Path.Combine(SourceScan.SourceRoot, KillswitchWriterFile.Replace('/', Path.DirectorySeparatorChar))));
        KillswitchWrite().Matches(writer).Select(match => match.Groups["verb"].Value.ToUpperInvariant().Split(' ')[0])
            .Distinct().Should().BeEquivalentTo(["INSERT", "UPDATE"], "the writer never deletes or truncates");
    }

    [Fact]
    public void The_Killswitch_Write_Pattern_Can_Fail_And_Catches_The_Ordinary_Spellings()
    {
        // The rule above passes whether its pattern works or matches nothing, so the shapes it must
        // catch and the ones it must not are checked against the pattern directly.
        foreach (var caught in new[]
        {
            "INSERT INTO platform_killswitches (key) VALUES (@k)",
            "insert  into   platform_killswitches",
            "UPDATE platform_killswitches SET is_enabled = true",
            "update only public.platform_killswitches set x = 1",
            "UPDATE \"public\".\"platform_killswitches\" SET x = 1",
            "DELETE FROM platform_killswitches",
            "delete from only \"platform_killswitches\"",
            "TRUNCATE platform_killswitches",
            "truncate table public.platform_killswitches",
        })
        {
            KillswitchWrite().IsMatch(caught).Should().BeTrue(caught);
        }

        foreach (var ignored in new[]
        {
            "SELECT is_enabled FROM platform_killswitches WHERE key = @k FOR UPDATE",
            "INSERT INTO platform_killswitches_archive (key) VALUES (@k)",
            "UPDATE platform_killswitches2 SET x = 1",
            "GRANT SELECT, INSERT, UPDATE ON platform_killswitches TO learnstack_platform",
        })
        {
            KillswitchWrite().IsMatch(ignored).Should().BeFalse(ignored);
        }
    }

    [GeneratedRegex(
        @"\b(?<verb>INSERT\s+INTO|UPDATE|DELETE\s+FROM|TRUNCATE(?:\s+TABLE)?)(?:\s+ONLY)?\s+(?:""?[A-Za-z_][A-Za-z0-9_]*""?\s*\.\s*)?""?platform_killswitches""?(?![A-Za-z0-9_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KillswitchWrite();

    private static List<string> FullNames(IEnumerable<Type> types, Type baseType) =>
        types.Where(type => type != baseType && baseType.IsAssignableFrom(type))
            .Select(type => type.FullName!).Distinct().ToList();

    [Fact]
    public void The_Platform_Scope_Writes_No_Tenant_Context_And_Sets_No_Session_Variable()
    {
        // Two closed sets it must stay outside of, both stated in Accepted ADRs. It is
        // not one of the writers of ITenantContextAccessor.Current — ADR-0036 names
        // it as explicitly not one — and it is not a ninth setter of app.tenant_id beside
        // ADR-0040's eight, because the role bypasses policies and there is nothing to
        // announce to. SetTenant_Callers_Are_The_Enumerated_Four covers the first
        // globally; this pins the second, which no rule covered.
        var scope = Path.Combine(SourceScan.SourceRoot, ScopeFile.Replace('/', Path.DirectorySeparatorChar));
        var code = SourceText.WithoutWhitespace(SourceText.WithoutComments(File.ReadAllText(scope)));

        code.Should().NotContain(SourceText.WithoutWhitespace("set_config("),
            "a BYPASSRLS connection has no policy to announce a tenant to, and announcing "
            + "one would make this a ninth setter in the set ADR-0040 closes at eight");
        code.Should().NotContain(SourceText.WithoutWhitespace("SetTenantContextAsync"));
        code.Should().NotContain(SourceText.WithoutWhitespace("IUnitOfWork"),
            "enlisting would put the bypass on the request's own connection");
    }
}
