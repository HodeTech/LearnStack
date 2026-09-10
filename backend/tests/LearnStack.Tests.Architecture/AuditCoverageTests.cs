using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Customization.Application.Audit;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.SharedKernel.Audit;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The catalogue in code and the matrix in prose have to agree.
/// </summary>
public sealed partial class AuditCoverageTests
{
    /// <summary>
    /// Every catalogue entry has a matrix row that states the same class and type.
    /// </summary>
    [Fact]
    public void Every_TenantOwned_Command_HasAuditCoverage()
    {
        // THE CATALOGUE-TO-MATRIX DIRECTION, which ADR-0044 Amendment 3 makes total: every
        // entry a module registers has a row carrying the same slug, and one that does not
        // fails. Neither artifact is derived from the other — that is the point — so
        // without this rule the two drift silently and the first reader to notice is
        // someone auditing a row that says something the matrix denies.
        //
        // The reverse direction — a matrix row whose request type EXISTS but which nobody
        // registered — binds only to a slug whose command has shipped, so it has to
        // re-derive the (planned) marker against the assemblies. That half lands with the
        // rest of Packet 9's architecture rules; this half is what catches a registration
        // that disagrees with its own row, which is the drift that has actually happened.
        var catalog = new AuditCatalog(
            [new TenancyAuditCatalogSource(), new CustomizationAuditCatalogSource()]);

        catalog.All.Should().NotBeEmpty("a sweep over an empty catalogue passes vacuously");

        var problems = new List<string>();

        foreach (var entry in catalog.All)
        {
            // The DECLARING module's matrix for a request-keyed entry. An off-path entry
            // takes its ModuleName from the slug, and `platform` has no module directory —
            // its row lives in Tenancy's file, where a reader looks for it.
            var row = FindRow(entry.Operation, "tenancy")
                ?? FindRow(entry.Operation, "customization");

            if (row is null)
            {
                problems.Add($"{entry.Operation}: no matrix row carries this slug");
                continue;
            }

            var declared = ClassOf(row);

            if (declared is not null && declared != entry.OperationClass)
            {
                problems.Add(
                    $"{entry.Operation}: the catalogue says {entry.OperationClass}, "
                    + $"the matrix says {declared}");
            }

            var type = TypeOf(row);

            if (type is not null && type != entry.OperationType)
            {
                problems.Add(
                    $"{entry.Operation}: the catalogue says {entry.OperationType}, "
                    + $"the matrix says {type}");
            }
        }

        problems.Should().BeEmpty(
            "the catalogue is code and the matrix is the document, and one architecture "
            + "test is what stops them drifting");
    }

    /// <summary>The matrix row whose Operation cell carries the slug, or <c>null</c>.</summary>
    /// <remarks>
    /// Matched on the cell rather than on the line, because a slug also appears in the
    /// prose above and below the table — and a prose mention is not a classification.
    /// </remarks>
    private static string? FindRow(string operation, string module)
    {
        var path = Path.Combine(
            RepositoryPaths.RepoRoot(), "docs", "modules", module, "audit.md");

        return File.ReadAllLines(path)
            .Where(line => line.StartsWith('|'))
            // Cell 2, not 1: splitting on '|' leaves an empty first element, so the
            // columns are Resource, Operation, Class at 1, 2 and 3.
            .FirstOrDefault(line => Cells(line).Length >= 4
                && Cells(line)[2].Contains('`' + operation + '`', StringComparison.Ordinal));
    }

    /// <summary>
    /// The class the row's third cell states, or <c>null</c> when it states none.
    /// </summary>
    private static OperationClass? ClassOf(string row)
    {
        var cell = Cells(row)[3];

        // MUST is bold in the table and SHOULD/MAY are not, so the marker is stripped
        // rather than matched — a rule that depended on the emphasis would break the day
        // someone tidied the formatting.
        var text = cell.Replace("*", string.Empty, StringComparison.Ordinal).Trim();

        if (text.StartsWith("MUST", StringComparison.Ordinal)) { return OperationClass.Must; }
        if (text.StartsWith("SHOULD", StringComparison.Ordinal)) { return OperationClass.Should; }
        if (text.StartsWith("MAY", StringComparison.Ordinal)) { return OperationClass.May; }

        return null;
    }

    /// <summary>
    /// The operation type the row names in parentheses, or <c>null</c> when it names none.
    /// </summary>
    /// <remarks>
    /// The matrix writes it in Audit Coverage's hyphenated spelling — `security-event`,
    /// `read-sensitive` — and the enum is PascalCase, so the comparison strips the hyphen.
    /// Only some rows name a type; a row that does not is classified by its class alone
    /// and this returns <c>null</c> rather than guessing one.
    /// </remarks>
    private static OperationType? TypeOf(string row)
    {
        var match = TypePattern().Match(Cells(row)[3]);

        if (!match.Success)
        {
            return null;
        }

        var spelled = match.Groups[1].Value.Replace("-", string.Empty, StringComparison.Ordinal);

        return Enum.TryParse<OperationType>(spelled, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    private static string[] Cells(string row) => row.Split('|');

    [GeneratedRegex("\\(`([a-z-]+)`\\)")]
    private static partial Regex TypePattern();
}
