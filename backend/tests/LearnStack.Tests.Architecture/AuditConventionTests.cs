using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Modules.Audit.Domain;
using LearnStack.Modules.Audit.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The audit subsystem's structural rules — the ones a reviewer cannot hold in their head.
/// </summary>
public sealed partial class AuditConventionTests
{
    // Restored after the review of Packet 9: the commit that added the rules below replaced
    // this case instead of adding beside it, and the catalogue went on calling it
    // Implemented while no test read a single ck_audit_log_* constraint.
    /// <summary>
    /// Every closed-set column on <c>audit_log</c> stores exactly what its own
    /// <c>CHECK</c> admits, and reads back what it stored.
    /// </summary>
    [Fact]
    public void Audit_Closed_Set_Columns_Store_What_Their_Check_Admits()
    {
        // Three closed-set text columns whose value lists are written in TWO places —
        // a value converter and a CHECK constraint — and the two are rendered in
        // DIFFERENT CASES, deliberately. `outcome` is lowercase because ADR-0033 § 3 and
        // ADR-0044 § 5 both write it that way; `operation_type` and `operation_class`
        // store the C# member name unchanged, on the ck_tenants_status precedent, which
        // is what lets the admin API's ?operationType=SecurityEvent filter be the same
        // string on both sides.
        //
        // That asymmetry is exactly the shape that drifts, and it drifts silently in
        // both directions. A converter that stopped lowercasing writes rows every INSERT
        // rejects with 23514 — on the write path whose whole job is that the record
        // survives. A parse that stopped being case-insensitive throws on EVERY row the
        // Phase 03 read API materialises, because the stored form is lowercase and the
        // member is `Success`. And an enum member added without its CHECK is a value the
        // code can produce and the column cannot hold.
        //
        // Read from the MODEL rather than from the source, so the rule sees what EF will
        // actually emit — including a converter some later configuration replaces.
        // The DESIGN-TIME model, not `Context.Model`: check constraints are migration
        // metadata and the runtime's read-optimized model drops them, throwing rather
        // than returning an empty list — which is the good failure, but only if the test
        // asks the right model.
        using var context = Modules.ModelOnly<AuditDbContext>();
        var model = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(AuditEntry))!;

        var checks = entity.GetCheckConstraints()
            .ToDictionary(c => c.Name!, c => c.Sql, StringComparer.Ordinal);

        checks.Should().ContainKeys(
            "ck_audit_log_outcome", "ck_audit_log_operation_type", "ck_audit_log_operation_class");

        AssertColumn<AuditOutcome>(entity, checks, nameof(AuditEntry.Outcome), "ck_audit_log_outcome");
        AssertColumn<OperationType>(entity, checks, nameof(AuditEntry.OperationType), "ck_audit_log_operation_type");
        AssertColumn<OperationClass>(entity, checks, nameof(AuditEntry.OperationClass), "ck_audit_log_operation_class");
    }

    private static void AssertColumn<TEnum>(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity,
        Dictionary<string, string> checks,
        string propertyName,
        string constraintName)
        where TEnum : struct, Enum
    {
        var property = entity.FindProperty(propertyName)!;
        var converter = property.GetValueConverter();

        converter.Should().NotBeNull(
            "{0} is a closed-set text column and maps through a converter", propertyName);

        var stored = Enum.GetValues<TEnum>()
            .Select(member => (string)converter!.ConvertToProvider(member)!)
            .ToList();

        // The CHECK's own literals, read out of the SQL the model carries rather than
        // restated here — a second copy in this file would be the third place the list
        // lives, and the one nothing compares against the database.
        var admitted = Regex
            .Matches(checks[constraintName], "'([^']*)'")
            .Select(match => match.Groups[1].Value)
            .ToList();

        stored.Should().BeEquivalentTo(admitted,
            "every value {0}'s converter can write is one {1} admits, and every value "
            + "{1} admits is one the enum can produce", propertyName, constraintName);

        foreach (var member in Enum.GetValues<TEnum>())
        {
            var roundTripped = converter!.ConvertFromProvider(converter.ConvertToProvider(member));

            roundTripped.Should().Be(member,
                "{0} must read back what it wrote — a case-sensitive parse against a "
                + "lowercased column throws on every row", propertyName);
        }
    }

    [Fact]
    public void AuditEntry_Inherits_Entity_Not_AuditableEntity()
    {
        // An audit row that carries UpdatedAt / DeletedAt is a MUTABLE audit row, which is
        // a contradiction — and the contradiction would be invisible: the columns simply
        // exist, nothing writes them, and the table looks append-only until the day
        // something does. The base class is what makes the shape impossible rather than
        // merely unused.
        var baseType = typeof(AuditEntry).BaseType;

        baseType.Should().NotBeNull();
        baseType!.IsGenericType.Should().BeTrue();
        baseType.GetGenericTypeDefinition().Should().Be(typeof(Entity<>),
            "AuditableEntity<TId> would give an append-only table a soft-delete column");

        // And not by any ancestor either: AuditableEntity<TId> derives from Entity<TId>,
        // so a check on the immediate base alone would pass for a type that inherited it
        // one level further down.
        for (var ancestor = typeof(AuditEntry); ancestor is not null; ancestor = ancestor.BaseType)
        {
            ancestor.Name.Should().NotStartWith("AuditableEntity",
                "an append-only row must not inherit UpdatedAt or DeletedAt at any depth");
        }
    }

    [Fact]
    public void OperationType_Enum_Matches_Catalog()
    {
        // Two artifacts, neither derived from the other: the enum every module names and
        // the table in Audit Coverage a human reads. A member in one and not the other is
        // either an operation nothing can record or a spelling the corpus promises and the
        // code refuses, and both are silent.
        var declared = Enum.GetNames<OperationType>()
            .Select(Hyphenate)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var catalogued = CatalogueRows()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        catalogued.Should().HaveCount(7,
            "a sweep that read no rows would agree with any enum");
        declared.Should().BeEquivalentTo(catalogued);
    }

    [Fact]
    public void Every_Module_Has_An_AuditCoverage_Matrix()
    {
        // The file's EXISTENCE is the assertion. What is in it is
        // Every_TenantOwned_Command_HasAuditCoverage's job; this is what stops a module
        // shipping a spec with no coverage document at all, which is the state in which
        // that other rule silently has nothing to compare against.
        var modules = Directory
            .EnumerateDirectories(Path.Combine(RepositoryPaths.RepoRoot(), "docs", "modules"))
            .ToList();

        // Named, not counted. Three module specs exist and each of them is a decision;
        // a fourth appearing without a matrix is what this rule is for, and a bare count
        // would let one be swapped for another.
        modules.Select(Path.GetFileName).Should().BeEquivalentTo(
            ["tenancy", "customization", "audit"]);

        WithoutMatrix(modules).Should().BeEmpty(
            "a module spec without a coverage matrix is a module whose audit obligations "
            + "nobody wrote down");
    }

    [Fact]
    public void The_Matrix_Sweep_Can_Actually_Fail()
    {
        // Every module HAS the file, so the rule above passes whether its predicate works
        // or is defeated — measured: replacing the check with a tautology left it green.
        // A rule that cannot tell "clean" from "blind" is the defect this suite has now
        // found in itself twice, so the predicate is exercised against a directory that
        // genuinely lacks the file.
        var empty = Directory.CreateTempSubdirectory("learnstack-matrix-probe");

        try
        {
            WithoutMatrix([empty.FullName]).Should().ContainSingle()
                .Which.Should().Be(empty.Name);
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    /// <summary>The module directories with no <c>audit.md</c>, by name.</summary>
    private static List<string?> WithoutMatrix(IEnumerable<string> modules) =>
        modules
            .Where(directory => !File.Exists(Path.Combine(directory, "audit.md")))
            .Select(Path.GetFileName)
            .ToList();

    /// <summary>
    /// No backend source writes through EF Core's set-based APIs, which leave the change
    /// tracker — and therefore the audit capture — without an entry.
    /// </summary>
    [Fact]
    public void No_Set_Based_Write_Bypasses_The_Audit_Capture()
    {
        // ADR-0044 § 7: the interceptor captures what the ChangeTracker holds, and nothing
        // else. ExecuteUpdate and ExecuteDelete write rows no entry describes, and so does
        // ExecuteSql*, so a MUST-class operation written that way commits with no before,
        // no after and no changes — silently, because the intent still writes its row.
        // They look like ordinary EF, which is what makes them the likely accident; a
        // hand-written NpgsqlCommand is visibly SQL, and Database Standards § Raw SQL
        // governs it. A live negative: nothing in backend/src uses any of the three.
        SetBasedWrites(SourceScan.SourceRoot).Should().BeEmpty(
            "a write the change tracker never sees is never audited — load the entities, "
            + "change them through the aggregate, and let SaveChanges write them");
    }

    [Fact]
    public void The_Set_Based_Write_Sweep_Can_Actually_Fail()
    {
        // Nothing in backend/src violates the rule, so it passes whether its scan works or
        // not. The probe writes one call across two lines, as a formatter would leave it,
        // and one file that names every API only in prose.
        var probe = Directory.CreateTempSubdirectory("learnstack-set-based-probe");

        try
        {
            File.WriteAllText(Path.Combine(probe.FullName, "Purge.cs"), """
                internal static class Purge
                {
                    public static Task<int> RunAsync(IQueryable<object> rows) => rows
                        .ExecuteDeleteAsync();
                }
                """);
            File.WriteAllText(Path.Combine(probe.FullName, "Prose.cs"), """
                // ExecuteUpdate, ExecuteDelete and ExecuteSqlRaw bypass the capture.
                internal static class Prose;
                """);

            SetBasedWrites(probe.FullName).Should().Equal("Purge.cs");
        }
        finally
        {
            probe.Delete(recursive: true);
        }
    }

    /// <summary>The files under <paramref name="root"/> that call a set-based write API.</summary>
    /// <remarks>
    /// Each name is a prefix of its async form and <c>ExecuteSql</c> of every raw variant,
    /// so three needles cover the nine methods.
    /// </remarks>
    private static List<string> SetBasedWrites(string root) =>
        [.. SetBasedWriteApis
            .SelectMany(api => SourceScan.FilesContaining(root, api, except: null))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static readonly string[] SetBasedWriteApis = ["ExecuteUpdate", "ExecuteDelete", "ExecuteSql"];

    [Fact]
    public void AuditEntry_Is_AppendOnly()
    {
        // No UPDATE and no DELETE against audit_log anywhere in backend/src, and the
        // exception list is CLOSED and NAMED: the GDPR redaction handler, the retention
        // purge job, and a module's IUserReferenceLocator. None of the three ships yet —
        // they land in Phase 11 and Phase 03 — so today the honest expectation is that
        // there are no sites at all, and the list exists so that adding one is a decision
        // rather than an edit.
        //
        // The database-level guard is AuditLog_Update_Is_Column_Restricted; this is the
        // source-level one, and it is the half that catches a statement written before the
        // policy is consulted.
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var code = SourceText.WithoutComments(File.ReadAllText(file));
            var relative = Path.GetRelativePath(RepositoryPaths.BackendSrc(), file);

            if (AuditLogMutation().IsMatch(code) && !IsSanctionedRedactionSite(relative))
            {
                offenders.Add(relative);
            }
        }

        offenders.Should().BeEmpty(
            "audit_log is append-only; the three sanctioned redaction sites land with "
            + "Phase 03 and Phase 11, and widening that list requires an ADR");
    }

    [Fact]
    public void The_AppendOnly_Sweep_Can_Actually_Fail()
    {
        // The companion the same lesson demands twice over: with no offending statement
        // anywhere, the rule above passes whether its pattern works or matches nothing.
        // These strings are the shapes it must catch, checked directly against the pattern.
        AuditLogMutation().IsMatch("UPDATE audit_log SET actor_email = NULL").Should().BeTrue();
        AuditLogMutation().IsMatch("delete from audit_log where id = @id").Should().BeTrue();
        AuditLogMutation().IsMatch("DELETE  FROM   audit_log").Should().BeTrue();

        // And the shapes it must not: the store's own INSERT, and a name that merely
        // starts the same way.
        AuditLogMutation().IsMatch("INSERT INTO audit_log (id, tenant_id)").Should().BeFalse();
        AuditLogMutation().IsMatch("DELETE FROM audit_log_archive").Should().BeFalse();
    }

    [Fact]
    public void IAuditStore_Exposes_No_Update_Method()
    {
        // The other half of the same claim, and the one a reader checks first. ADR-0033
        // says four writes and no update; a fifth method whose name says otherwise would
        // make the port contradict the table's own triggers.
        typeof(IAuditStore).GetMethods()
            .Select(method => method.Name)
            .Should().BeEquivalentTo(
                "WritePendingAsync", "WriteStandaloneAsync", "WriteBestEffortAsync",
                "WritePlatformScopeAsync");
    }

    /// <summary>
    /// Whether the path is one of the three sites the standard names.
    /// </summary>
    /// <remarks>
    /// None of them exists yet. The predicate ships with the rule so that the first one to
    /// land is exempted by NAME rather than by widening the pattern — which is how a rule
    /// stops meaning anything.
    /// </remarks>
    private static bool IsSanctionedRedactionSite(string relative) =>
        relative.Contains("Modules.Audit.Infrastructure", StringComparison.Ordinal)
            && (relative.Contains("Redaction", StringComparison.Ordinal)
                || relative.Contains("RetentionPurge", StringComparison.Ordinal));

    private static IEnumerable<string> SourceFiles() =>
        Directory
            .EnumerateFiles(RepositoryPaths.BackendSrc(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "obj" or "bin"));

    /// <summary>An `UPDATE` or `DELETE` whose target is `audit_log` itself.</summary>
    [GeneratedRegex(
        @"\b(?:UPDATE\s+audit_log\b|DELETE\s+FROM\s+audit_log\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex AuditLogMutation();

    /// <summary>
    /// The seven `OperationType` spellings the standard's table carries.
    /// </summary>
    /// <remarks>
    /// Read from the first column of the § Operation Types table and nowhere else: the
    /// same words appear in the prose around it, and a prose mention is not a catalogue
    /// entry.
    /// </remarks>
    private static IEnumerable<string> CatalogueRows()
    {
        var path = Path.Combine(
            RepositoryPaths.RepoRoot(), "docs", "standards", "18-audit-coverage.md");

        var inTable = false;

        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inTable = line.Contains("Operation Types", StringComparison.Ordinal);
                continue;
            }

            if (!inTable || !line.StartsWith("| `", StringComparison.Ordinal))
            {
                continue;
            }

            var match = MemberPattern().Match(line);

            if (match.Success)
            {
                yield return match.Groups[1].Value;
            }
        }
    }

    /// <summary>`ReadSensitive` becomes `read-sensitive`, the spelling the table uses.</summary>
    private static string Hyphenate(string member) =>
        string.Concat(member.Select((character, index) =>
            char.IsUpper(character) && index > 0
                ? "-" + char.ToLowerInvariant(character)
                : char.ToLowerInvariant(character).ToString()));

    [GeneratedRegex(@"^\| `([a-z-]+)` \|")]
    private static partial Regex MemberPattern();
}
