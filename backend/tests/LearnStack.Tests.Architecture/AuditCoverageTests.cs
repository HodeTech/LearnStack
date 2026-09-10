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

    /// <summary>
    /// Every matrix row whose command exists is registered, and no `(planned)` marker
    /// outlives the command it was waiting for.
    /// </summary>
    [Fact]
    public void Every_Matrix_Row_Whose_Command_Exists_Is_Registered()
    {
        // THE MATRIX-TO-CATALOGUE DIRECTION, and it is scoped rather than total on
        // purpose: classifying ahead of the command is what Audit Coverage asks for, so a
        // row may legitimately name an operation nothing implements yet. Such a row
        // carries `(planned)` or `(off-path)` in its Operation cell.
        //
        // The ANTI-ROT half is what stops that scoping becoming a hole: a `(planned)` row
        // whose command HAS since shipped fails. The marker is a claim this rule re-checks
        // on every run, not an exemption from it — otherwise the first thing anyone would
        // do to silence this test is add the word.
        var catalog = new AuditCatalog(
            [new TenancyAuditCatalogSource(), new CustomizationAuditCatalogSource()]);

        var registered = catalog.All
            .Select(entry => entry.Operation)
            .ToHashSet(StringComparer.Ordinal);

        var problems = new List<string>();
        var checkedRows = 0;

        foreach (var module in new[] { "tenancy", "customization" })
        {
            foreach (var (slug, cell) in MatrixSlugs(module))
            {
                checkedRows++;

                var planned = cell.Contains("(planned)", StringComparison.Ordinal);
                var offPath = cell.Contains("(off-path)", StringComparison.Ordinal);
                var isRegistered = registered.Contains(slug);

                if (isRegistered)
                {
                    // Anti-rot. An off-path row is registered by slug and keeps its marker
                    // for the reader; a `(planned)` one is making a claim about the
                    // future, and the future has arrived.
                    if (planned)
                    {
                        problems.Add(
                            $"{module}/{slug}: the matrix still says (planned), but the "
                            + "catalogue registers it — the marker outlived its command");
                    }

                    continue;
                }

                if (!planned && !offPath)
                {
                    problems.Add(
                        $"{module}/{slug}: the matrix classifies it and nothing registers "
                        + "it, and it carries no (planned) or (off-path) marker");
                }
            }
        }

        checkedRows.Should().BeGreaterThan(10,
            "a sweep that matched no matrix rows would pass while proving nothing");

        problems.Should().BeEmpty(
            "a matrix row is a promise the catalogue keeps, and a (planned) marker is a "
            + "claim re-checked on every run rather than an exemption");
    }

    /// <summary>Every `(slug, operation cell)` the module's matrix classifies.</summary>
    /// <remarks>
    /// Read from the Operation column only — cell 2 — so a slug mentioned in the prose or
    /// in another column is not mistaken for a classification. A row with no backticked
    /// slug in that cell is a header or a separator and is skipped.
    /// </remarks>
    private static IEnumerable<(string Slug, string Cell)> MatrixSlugs(string module)
    {
        var path = Path.Combine(
            RepositoryPaths.RepoRoot(), "docs", "modules", module, "audit.md");

        foreach (var line in File.ReadAllLines(path).Where(line => line.StartsWith('|')))
        {
            var cells = Cells(line);

            if (cells.Length < 4)
            {
                continue;
            }

            var match = SlugPattern().Match(cells[2]);

            if (match.Success)
            {
                yield return (match.Groups[1].Value, cells[2]);
            }
        }
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

    /// <summary>A dotted audit slug in backticks: `{module}.{resource}.{verb}`.</summary>
    [GeneratedRegex("`([a-z][a-z0-9_]*\\.[a-z0-9_]+\\.[a-z0-9_]+)`")]
    private static partial Regex SlugPattern();
}
