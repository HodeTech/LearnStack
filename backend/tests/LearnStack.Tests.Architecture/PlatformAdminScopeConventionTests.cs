using System.Reflection;
using FluentAssertions;
using LearnStack.SharedKernel.Tenancy;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The rules that keep the one <c>BYPASSRLS</c> credential reachable from one place.
/// </summary>
public sealed class PlatformAdminScopeConventionTests
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
    private static IEnumerable<Assembly> ProductionAssemblies() =>
        Directory.EnumerateFiles(
                RepositoryPaths.BackendSrc(), "LearnStack.*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => Assembly.Load(name!));

    private const string ScopeFile = "LearnStack.Infrastructure/MultiTenancy/PlatformAdminScope.cs";

    private const string CompositionFile =
        "LearnStack.Api/Composition/PersistenceCompositionExtensions.cs";

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
            [ScopeFile, CompositionFile],
            "the scope injects the keyed source and the composition root registers it; a "
            + "third resolver is a second path to a credential that sees every tenant");

        // Leg 2 — IConfiguration.GetConnectionString is called in exactly one file under
        // backend/src. Not "every credential is read in one file": the two design-time
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
                [CompositionFile],
                "one file reads credentials, so one file decides what is done with them");

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
    public async Task PlatformAdminScope_Entry_Requires_Platform_Permission()
    {
        // Conjunct A — live. The gate port exists, the registered implementation refuses
        // everyone, and the scope consults it. That last part is what makes ADR-0036's
        // "checked before the scope opens" a call rather than a sentence; the ORDER —
        // before the credential is touched — is asserted behaviourally in
        // PlatformAdminGateTests, which a structural test cannot see.
        typeof(IPlatformAdminGate).Should().BeAssignableTo<object>();

        (await new DenyAllPlatformAdminGate().IsPermittedAsync("any", CancellationToken.None))
            .Should().BeFalse("the shipped gate permits nobody until Phase 03");

        SourceScan.FilesContaining(SourceScan.SourceRoot, "IsPermittedAsync", except: null)
            .Should().Contain(ScopeFile, "the scope must actually consult the gate");

        // Conjunct B — VACUOUS, and doubly so. The permission key itself does not exist:
        // there is no permission system until Phase 03, and no production caller enters
        // the scope at all. What can be asserted now is that no second gate
        // implementation has appeared to sit beside the deny-all one, because that is how
        // a permissive default arrives — registered somewhere else, for a demo.
        // Swept across every production assembly, not just the one declaring the
        // interface. The first version scanned typeof(IPlatformAdminGate).Assembly —
        // SharedKernel — so a permissive gate in Infrastructure or Api, which is exactly
        // where "registered elsewhere for a demo" would put one, was invisible to the
        // rule whose own message names that scenario. The mutation that was supposed to
        // prove this leg passed only because the probe was planted in SharedKernel too.
        var gates = ProductionAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .Where(typeof(IPlatformAdminGate).IsAssignableFrom)
            .Select(type => type.Name)
            .Distinct()
            .ToList();

        gates.Should().BeEquivalentTo(
            [nameof(DenyAllPlatformAdminGate)],
            "Phase 03 replaces this one; a second implementation appearing before then is "
            + "how 'permissive to unblock a demo' gets shipped");
    }

    [Fact]
    public void The_Platform_Scope_Writes_No_Tenant_Context_And_Sets_No_Session_Variable()
    {
        // Two closed sets it must stay outside of, both stated in Accepted ADRs. It is
        // not one of the four writers of ITenantContextAccessor.Current — ADR-0036 names
        // it as explicitly not one — and it is not an eighth out-of-band setter of
        // app.tenant_id, because the role bypasses policies and there is nothing to
        // announce to. SetTenant_Callers_Are_The_Enumerated_Four covers the first
        // globally; this pins the second, which no rule covered.
        var scope = Path.Combine(SourceScan.SourceRoot, ScopeFile.Replace('/', Path.DirectorySeparatorChar));
        var code = SourceText.WithoutWhitespace(SourceText.WithoutComments(File.ReadAllText(scope)));

        code.Should().NotContain(SourceText.WithoutWhitespace("set_config("),
            "a BYPASSRLS connection has no policy to announce a tenant to, and announcing "
            + "one would make this an eighth setter in a set two ADRs close at seven");
        code.Should().NotContain(SourceText.WithoutWhitespace("SetTenantContextAsync"));
        code.Should().NotContain(SourceText.WithoutWhitespace("IUnitOfWork"),
            "enlisting would put the bypass on the request's own connection");
    }
}
