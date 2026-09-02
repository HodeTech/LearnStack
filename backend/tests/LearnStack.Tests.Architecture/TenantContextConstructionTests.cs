using System.Reflection;
using FluentAssertions;
using LearnStack.SharedKernel.Tenancy;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The rules that keep tenant context construction and tenant context <i>writing</i>
/// where <see href="../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
/// put them.
/// </summary>
public sealed class TenantContextConstructionTests
{
    private static readonly Assembly Kernel = typeof(TenantContext).Assembly;

    [Fact]
    public void TenantContext_Is_Constructed_Only_By_The_Factory()
    {
        // Five conjuncts, and they need two different instruments — which is the
        // whole reason this test is written out rather than expressed as one
        // NetArchTest chain. A type-reference scan can see a constructor's
        // accessibility and a method's return type; it cannot see a `new` expression,
        // because a call site is not a type reference. So the third conjunct is a
        // source scan, and without it the `internal` constructor's one residual — a
        // second caller inside this same assembly — is uncovered.
        var type = typeof(TenantContext);

        type.IsSealed.Should().BeTrue();

        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().BeEmpty("ADR-0036 § Rules: sealed with no public constructor");

        // Not "no internal constructor". C# has no friend types, so a private
        // constructor and a top-level TenantContextFactory — the name ADR-0036, the
        // glossary and two roadmap lines all carry — are mutually exclusive.
        // `internal` is the ceiling the language offers, and it holds because this
        // assembly has no InternalsVisibleTo: every module project, both
        // infrastructure assemblies, the API and all four test assemblies are blocked
        // by the compiler. That last clause is asserted below, because one attribute
        // would silently reopen construction to a whole assembly.
        Kernel.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
            .Should().BeEmpty(
                "an InternalsVisibleTo here would hand a whole assembly the constructor "
                + "and reduce this rule to its first two conjuncts");

        var producers = Kernel.GetTypes()
            .SelectMany(candidate => candidate.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => Produces(method.ReturnType))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToList();

        producers.Should().BeEquivalentTo(
            [$"{nameof(TenantContextFactory)}.{nameof(TenantContextFactory.Create)}"],
            "a second member handing back a TenantContext is a second entry point, "
            + "whatever it delegates to");
    }

    [Fact]
    public void TenantContext_Is_Instantiated_In_One_File()
    {
        // The conjunct reflection cannot reach. Exempts the factory's own file by
        // path rather than by name — two files may share a name in different folders,
        // and excluding both because one is exempt is how a rule quietly stops
        // covering half of what it names.
        const string Factory = "Tenancy/TenantContextFactory.cs";

        var offenders = SourceScan.FilesContaining(
            SourceScan.KernelRoot,
            "new TenantContext(",
            except: Factory);

        offenders.Should().BeEmpty(
            $"TenantContextFactory.Create is the single entry point; only {Factory} may call the "
            + "constructor, and an internal constructor leaves exactly this residual");
    }

    [Fact]
    public void SetTenant_Callers_Are_The_Enumerated_Four()
    {
        // ADR-0036 § Rules, as corrected by its Amendment 2: the member is
        // `ITenantContextAccessor.Current`, and writes to it have exactly four
        // callers — TenantResolverMiddleware (HTTP), HubCorrelationMiddleware
        // (/api/internal/*), the Hangfire JobActivator (jobs) and the outbox / inbox
        // handler scope (integration events). Two exist today; the rule is written
        // over the whole set so the third to arrive is a deliberate edit here rather
        // than a silent addition there.
        //
        // The needle is receiver-agnostic — a future `Activity.Current =` anywhere in
        // backend/src would trip this, which is a false positive to exempt by path
        // and not a reason to filter by folder.
        //
        // EnterPlatformAdminScope is deliberately NOT among them (Step 7): it opens a
        // second connection and sets no tenant context. A test that admitted it would
        // be admitting a cross-tenant path into the resolution set.
        // Unfiltered, and that is the whole rule. The first version narrowed the scan
        // to files whose path contained "Tenancy", which deleted the writer that had
        // already shipped — InProcessEventBus, the integration-event handler scope,
        // which ADR-0036 Amendment 2 names as the fourth caller — and, worse, meant a
        // fifth writer anywhere else in the tree passed green. A rule whose whole job
        // in this packet is the NEGATIVE cannot be scoped to the folder the positives
        // happen to live in. If a false positive ever appears, narrow the needle or
        // exempt the one file by path; do not re-narrow by folder.
        var writers = SourceScan.FilesContaining(
            SourceScan.SourceRoot, ".Current =", except: null);

        writers.Should().BeEquivalentTo(
            [
                "LearnStack.Api/Tenancy/TenantResolverMiddleware.cs",
                "LearnStack.Infrastructure/Messaging/InProcessEventBus.cs",
            ],
            "two of the four enumerated writers have landed — the HTTP one and the "
            + "integration-event handler scope. HubCorrelationMiddleware and the "
            + "Hangfire JobActivator are later phases, and a fifth writer is how a "
            + "request runs under a tenant nothing resolved");
    }

    [Fact]
    public void Organizations_Are_Read_By_Composite_Key()
    {
        // Two legs, because the rule is broader than its one implementation. The
        // primary key is the surrogate id (pk_organizations), so a lookup by id alone
        // is a well-formed, index-served query that returns another tenant's row —
        // for the policy to hide, if the announcement was made, and to hand back if
        // it was not. That is the whole hazard: the belonging must be decided by the
        // key and the policy, never by comparing a tenant column in application code
        // after the row is already in hand.

        // Leg 1 — the SQL. Every organizations read in the source names both key
        // columns. Scanned rather than reflected: a raw command's text is a string
        // literal, which is exactly what a type-reference scan cannot see.
        var reads = SourceScan.FilesContaining(SourceScan.SourceRoot, "FROM organizations", except: null);

        reads.Should().BeEquivalentTo(
            ["LearnStack.Infrastructure/MultiTenancy/OrganizationScopeValidator.cs"],
            "a second raw reader of this table is a second place the composite key can be missed");

        var validator = File.ReadAllText(Path.Combine(
            SourceScan.SourceRoot,
            "LearnStack.Infrastructure", "MultiTenancy", "OrganizationScopeValidator.cs"));
        var code = SourceText.WithoutWhitespace(SourceText.WithoutComments(validator));

        code.Should().Contain(SourceText.WithoutWhitespace(
            "WHERE tenant_id = @tenant AND id = @organization"),
            "both key columns, in the WHERE clause, and never id alone");
        code.Should().Contain(SourceText.WithoutWhitespace("set_config('app.tenant_id'"),
            "the announcement is what makes the policy — not the WHERE clause — the "
            + "thing that decides, and it must come first");

        // Leg 2 — the same rule expressed in EF, which is how the NEXT organization
        // read will be written. Vacuous today and deliberately kept: nothing reads
        // organizations through a DbContext until Step 9 writes the first command,
        // and a scan that only starts existing once there is something to catch is a
        // scan nobody adds. `Find`/`FindAsync` take the primary key, which here is
        // the surrogate id, so they cannot express the composite key at all.
        var byPrimaryKey = PrimaryKeyReads
            .SelectMany(literal =>
                SourceScan.FilesContaining(SourceScan.SourceRoot, literal, except: null))
            .ToList();

        byPrimaryKey.Should().BeEmpty(
            "Find takes the primary key, which is the surrogate id alone — an "
            + "organization read must name (tenant_id, id)");
    }

    /// <summary>The EF spellings that take the primary key, which here is the id alone.</summary>
    private static readonly string[] PrimaryKeyReads =
        ["Organizations.Find", "Organizations.FindAsync"];

    private static bool Produces(Type returnType)
    {
        if (returnType == typeof(TenantContext))
        {
            return true;
        }

        return returnType.IsGenericType
            && returnType.GetGenericArguments().Contains(typeof(TenantContext));
    }
}
