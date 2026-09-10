using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Modules.Audit.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Domain;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The audit subsystem's structural rules — the ones a reviewer cannot hold in their head.
/// </summary>
public sealed partial class AuditConventionTests
{
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
