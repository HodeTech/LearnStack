using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using MediatR;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The catalogue in code and the matrix in prose have to agree — and every request the
/// backend ships has to be in the catalogue at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Discovered, not listed.</b> The request types, the catalogue sources and the modules
/// are all found by reflection over the backend's own assemblies. The first version of this
/// file compared slug sets built from two hard-coded sources, and a slug two request types
/// share hid a missing registration completely: the review of Packet 9 removed only
/// <c>MustAudit&lt;CreateOrganizationCommand&gt;</c> — provisioning still carries
/// <c>tenancy.organization.create</c> — and both directions stayed green while the running
/// system refused every <c>CreateOrganizationCommand</c> as unclassified.
/// </para>
/// <para>
/// <b>Every rule here has a companion that proves it can fail.</b> A rule that cannot tell
/// clean from blind is the defect this suite has found in itself more than once; each
/// predicate is therefore exercised against a fixture that genuinely violates it.
/// </para>
/// </remarks>
public sealed partial class AuditCoverageTests
{
    /// <summary>
    /// Every request type the backend can dispatch — one with a handler — is registered
    /// in the catalogue, <c>Off</c> included.
    /// </summary>
    [Fact]
    public void Every_Shipped_Request_Is_Registered()
    {
        // The rule AuditLogBehavior enforces at run time — an unregistered request is
        // refused with audit_unclassified_operation — checked where a defect costs a red
        // build instead of a 500 per call. Keyed on the REQUEST TYPE, which is what the
        // behavior looks up; a slug-level comparison cannot see a request whose slug some
        // other request also registers.
        var shipped = ShippedRequestTypes();

        shipped.Should().Contain(typeof(CreateOrganizationCommand),
            "a discovery that missed a shipped command would pass while proving nothing");

        UnregisteredOf(shipped, Catalogue()).Should().BeEmpty(
            "every request reaching pipeline step 3 must be classified, silence included, "
            + "or the running system refuses it");
    }

    [Fact]
    public void The_Request_Sweep_Can_Actually_Fail()
    {
        // The review's measured mutation, as a fixture: provisioning registers
        // tenancy.organization.create and CreateOrganizationCommand is left out. Every
        // slug-level comparison agrees with that catalogue; the request-level one does not.
        var catalog = new AuditCatalog([new ProvisioningOnlySource()]);

        UnregisteredOf([typeof(ProvisionTenantCommand), typeof(CreateOrganizationCommand)], catalog)
            .Should().Equal(typeof(CreateOrganizationCommand));
    }

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
        // Entry by entry, and every entry belongs to a request type the rule above has
        // already required to be registered — so the join runs at request-and-operation
        // granularity rather than over a set of slugs.
        var catalog = Catalogue();

        catalog.All.Should().NotBeEmpty("a sweep over an empty catalogue passes vacuously");

        var matrices = MatrixModules().ToDictionary(module => module, MatrixLines);
        var problems = new List<string>();

        foreach (var entry in catalog.All)
        {
            // Any module's matrix: a request-keyed entry's row is in its declaring module's
            // file, and an off-path entry whose segment is `platform` — no module of its own
            // — sits in the file a reader looks for it in.
            var row = matrices.Values
                .Select(lines => FindRow(entry.Operation, lines))
                .FirstOrDefault(found => found is not null);

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
        // do to silence this test is add the word. A shipped command that nobody
        // registered is caught one rule up, by request type, which is what this slug-level
        // direction cannot see.
        var registered = Catalogue().All
            .Select(entry => entry.Operation)
            .ToHashSet(StringComparer.Ordinal);

        var rows = MatrixModules()
            .SelectMany(module => MatrixSlugs(MatrixLines(module)).Select(row => (module, row.Slug, row.Cell)))
            .ToList();

        rows.Should().HaveCountGreaterThan(10,
            "a sweep that matched no matrix rows would pass while proving nothing");

        ReverseProblems(rows, registered).Should().BeEmpty(
            "a matrix row is a promise the catalogue keeps, and a (planned) marker is a "
            + "claim re-checked on every run rather than an exemption");
    }

    [Fact]
    public void The_Matrix_Sweep_Reads_Every_Slug_And_Can_Actually_Fail()
    {
        // Three defects, each in a row the real matrices do not contain today — which is
        // exactly why they need a fixture. Audit Coverage allows several slugs in one
        // Operation cell, and the parser read only the first; a (planned) marker left
        // behind by a command that shipped; and a classified row nothing registers and
        // nothing marks.
        string[] matrix =
        [
            "| Resource | Operation | Class | Why |",
            "|---|---|---|---|",
            "| `Thing` | `mod.thing.create` `mod.thing.clone` | **MUST** | two slugs, one row |",
            "| `Thing` | `mod.thing.rename` `(planned)` | **MUST** | shipped since |",
            "| `Thing` | `mod.thing.archive` | **MUST** | nobody registered it |",
        ];

        MatrixSlugs(matrix).Select(row => row.Slug).Should().Equal(
            "mod.thing.create", "mod.thing.clone", "mod.thing.rename", "mod.thing.archive");

        var rows = MatrixSlugs(matrix).Select(row => ("mod", row.Slug, row.Cell)).ToList();
        var registered = new HashSet<string>(["mod.thing.create", "mod.thing.rename"], StringComparer.Ordinal);

        ReverseProblems(rows, registered).Should().SatisfyRespectively(
            clone => clone.Should().StartWith("mod/mod.thing.clone:"),
            rename => rename.Should().StartWith("mod/mod.thing.rename:").And.Contain("(planned)"),
            archive => archive.Should().StartWith("mod/mod.thing.archive:"));
    }

    /// <summary>
    /// A module that ships a request type has a coverage matrix to classify it in.
    /// </summary>
    [Fact]
    public void Every_Module_That_Ships_A_Request_Has_A_Matrix()
    {
        // ADR-0044 Amendment 4 § 3 binds the matrix to a module that has shipped an
        // aggregate or a request type. Every_Module_Has_An_AuditCoverage_Matrix walks the
        // spec directories that exist; this walks the CODE, so a new module that ships a
        // command with no spec directory at all is caught — the state in which the matrix
        // join has nothing to compare against and passes.
        var shipping = ShippedRequestTypes()
            .Select(ModuleOf)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        shipping.Should().Contain(["Tenancy", "Customization"],
            "a discovery that found no shipping module would pass while proving nothing");

        ModulesWithoutMatrix(shipping, ModuleSpecs()).Should().BeEmpty(
            "a module that ships a command owes a matrix that classifies it");
    }

    [Fact]
    public void The_Module_Sweep_Can_Actually_Fail()
    {
        ModulesWithoutMatrix(["Tenancy", "Ghost"], ModuleSpecs()).Should().Equal("Ghost");
    }

    /// <summary>The request types among <paramref name="shipped"/> the catalogue does not know.</summary>
    private static List<Type> UnregisteredOf(IEnumerable<Type> shipped, AuditCatalog catalog) =>
        [.. shipped.Where(request => !catalog.TryGet(request, out _))];

    /// <summary>The reverse direction's findings, one line each.</summary>
    private static List<string> ReverseProblems(
        IEnumerable<(string Module, string Slug, string Cell)> rows, HashSet<string> registered)
    {
        var problems = new List<string>();

        foreach (var (module, slug, cell) in rows)
        {
            var planned = cell.Contains("(planned)", StringComparison.Ordinal);
            var offPath = cell.Contains("(off-path)", StringComparison.Ordinal);

            if (registered.Contains(slug))
            {
                // Anti-rot. An off-path row is registered by slug and keeps its marker for
                // the reader; a `(planned)` one is making a claim about the future, and the
                // future has arrived.
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

        return problems;
    }

    /// <summary>The modules among <paramref name="shipping"/> with no <c>audit.md</c>.</summary>
    private static List<string> ModulesWithoutMatrix(IEnumerable<string> shipping, string specs) =>
        [.. shipping.Where(module =>
            !File.Exists(Path.Combine(specs, module.ToLowerInvariant(), "audit.md")))];

    /// <summary>
    /// Every request type with a handler in any backend assembly.
    /// </summary>
    /// <remarks>
    /// A request with a handler is one the pipeline can be asked to run, which is the
    /// population AuditLogBehavior classifies. The assemblies are every project under
    /// <c>backend/src</c>, loaded by name — so a new module is swept the moment it builds,
    /// without a line here.
    /// </remarks>
    private static List<Type> ShippedRequestTypes() =>
        [.. BackendAssemblies()
            .SelectMany(LoadableTypes)
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .SelectMany(type => type.GetInterfaces())
            .Where(contract => contract.IsGenericType
                && (contract.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
                    || contract.GetGenericTypeDefinition() == typeof(IRequestHandler<>)))
            .Select(contract => contract.GetGenericArguments()[0])
            .Distinct()];

    /// <summary>
    /// The catalogue merged from every <see cref="IAuditCatalogSource"/> the backend ships.
    /// </summary>
    /// <remarks>
    /// Discovered rather than listed, for the reason <see cref="ShippedRequestTypes"/> is: a
    /// source the composition root registers and this file forgot would make every rule here
    /// report on a catalogue the running system does not have.
    /// </remarks>
    private static AuditCatalog Catalogue() =>
        new(BackendAssemblies()
            .SelectMany(LoadableTypes)
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && typeof(IAuditCatalogSource).IsAssignableFrom(type))
            .Select(type => (IAuditCatalogSource)Activator.CreateInstance(type)!)
            .ToList());

    private static IEnumerable<Assembly> BackendAssemblies() =>
        Directory
            .EnumerateFiles(RepositoryPaths.BackendSrc(), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => Assembly.Load(Path.GetFileNameWithoutExtension(path)));

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            return partial.Types.OfType<Type>();
        }
    }

    /// <summary>The module a request type belongs to, from its namespace, or <c>null</c>.</summary>
    private static string? ModuleOf(Type request)
    {
        var match = ModuleNamespace().Match(request.Namespace ?? string.Empty);

        return match.Success ? match.Groups[1].Value : null;
    }

    private static string ModuleSpecs() => Path.Combine(RepositoryPaths.RepoRoot(), "docs", "modules");

    /// <summary>Every module spec directory that carries a matrix.</summary>
    private static IEnumerable<string> MatrixModules() =>
        Directory.EnumerateDirectories(ModuleSpecs())
            .Where(directory => File.Exists(Path.Combine(directory, "audit.md")))
            .Select(directory => Path.GetFileName(directory)!)
            .Order(StringComparer.Ordinal);

    private static string[] MatrixLines(string module) =>
        File.ReadAllLines(Path.Combine(ModuleSpecs(), module, "audit.md"));

    /// <summary>Every <c>(slug, operation cell)</c> a matrix classifies — every slug in the cell.</summary>
    /// <remarks>
    /// Read from the Operation column only — cell 2 — so a slug mentioned in the prose or
    /// in another column is not mistaken for a classification. A row with no backticked
    /// slug in that cell is a header or a separator and is skipped.
    /// <see href="../../../docs/standards/18-audit-coverage.md">Audit Coverage</see> allows
    /// several slugs in one cell, and reading only the first — which this did — made the
    /// rest of the cell invisible to the reverse direction.
    /// </remarks>
    private static IEnumerable<(string Slug, string Cell)> MatrixSlugs(IEnumerable<string> lines)
    {
        foreach (var line in lines.Where(line => line.StartsWith('|')))
        {
            var cells = Cells(line);

            if (cells.Length < 4)
            {
                continue;
            }

            foreach (Match match in SlugPattern().Matches(cells[2]))
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
    private static string? FindRow(string operation, IEnumerable<string> lines) =>
        lines
            .Where(line => line.StartsWith('|'))
            // Cell 2, not 1: splitting on '|' leaves an empty first element, so the
            // columns are Resource, Operation, Class at 1, 2 and 3.
            .FirstOrDefault(line => Cells(line).Length >= 4
                && Cells(line)[2].Contains('`' + operation + '`', StringComparison.Ordinal));

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

    /// <summary>The catalogue the review's mutation left: provisioning, and nothing else.</summary>
    private sealed class ProvisioningOnlySource : IAuditCatalogSource
    {
        public string ModuleName => "tenancy";

        public void Describe(IAuditCatalogBuilder builder) =>
            builder
                .MustAudit<ProvisionTenantCommand>("tenancy.tenant.create", OperationType.Create, typeof(Tenant))
                .MustAudit<ProvisionTenantCommand>("tenancy.organization.create", OperationType.Create, typeof(Organization));
    }

    [GeneratedRegex("\\(`([a-z-]+)`\\)")]
    private static partial Regex TypePattern();

    /// <summary>A dotted audit slug in backticks: `{module}.{resource}.{verb}`.</summary>
    [GeneratedRegex("`([a-z][a-z0-9_]*\\.[a-z0-9_]+\\.[a-z0-9_]+)`")]
    private static partial Regex SlugPattern();

    /// <summary>A module namespace: <c>LearnStack.Modules.{Name}.…</c>.</summary>
    [GeneratedRegex("^LearnStack\\.Modules\\.([A-Za-z]+)\\.")]
    private static partial Regex ModuleNamespace();
}
