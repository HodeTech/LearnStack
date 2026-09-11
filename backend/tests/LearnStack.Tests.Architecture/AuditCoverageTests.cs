using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
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

        var problems = ForwardProblems(
            catalog.All,
            MatrixModules().ToDictionary(module => module, MatrixLines, StringComparer.OrdinalIgnoreCase));

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
        var catalogue = Catalogue();
        var registered = catalogue.All
            .Select(entry => entry.Operation)
            .ToHashSet(StringComparer.Ordinal);
        var offPath = registered
            .Where(slug => catalogue.TryGetOffPath(slug, out _))
            .ToHashSet(StringComparer.Ordinal);

        var rows = MatrixModules()
            .SelectMany(module => MatrixSlugs(MatrixLines(module)).Select(row => (module, row.Slug, row.Cell)))
            .ToList();

        rows.Should().HaveCountGreaterThan(10,
            "a sweep that matched no matrix rows would pass while proving nothing");

        ReverseProblems(rows, registered, offPath).Should().BeEmpty(
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

        ReverseProblems(rows, registered, offPath: []).Should().SatisfyRespectively(
            clone => clone.Should().StartWith("mod/mod.thing.clone:"),
            rename => rename.Should().StartWith("mod/mod.thing.rename:").And.Contain("(planned)"),
            archive => archive.Should().StartWith("mod/mod.thing.archive:"));
    }

    [Fact]
    public void The_Forward_Sweep_Rejects_A_Class_Or_A_Type_It_Cannot_Read()
    {
        // A class cell that parses to nothing used to skip the comparison: the review of
        // Packet 9 changed a registered MUST row's class to "Off" and every case stayed green.
        // The same for a type named in parentheses that the enum does not have — and, until
        // the third review, for any annotation the extraction did not recognise as one: a
        // PascalCase or snake_case spelling read as "names no type". An entry the catalogue
        // registers has a class and a type the matrix must state readably.
        AuditCatalogEntry[] entries =
        [
            new("mod", "mod.thing.create", OperationType.Create, OperationClass.Must, typeof(object)),
            new("mod", "mod.thing.read", OperationType.ReadSensitive, OperationClass.Must, typeof(object)),
            new("mod", "mod.thing.update", OperationType.Update, OperationClass.Should, typeof(object)),
            new("mod", "mod.thing.grant", OperationType.SecurityEvent, OperationClass.Must, typeof(object)),
            new("mod", "mod.thing.purge", OperationType.SecurityEvent, OperationClass.Must, typeof(object)),
            new("mod", "mod.thing.erase", OperationType.SecurityEvent, OperationClass.Must, typeof(object)),
            new("mod", "mod.thing.revoke", OperationType.SecurityEvent, OperationClass.Must, typeof(object)),
        ];

        string[] matrix =
        [
            "| Resource | Operation | Class | Why |",
            "|---|---|---|---|",
            "| `Thing` | `mod.thing.create` | Off | not a class |",
            "| `Thing` | `mod.thing.read` | **MUST** (`bogus-kind`) | not a type |",
            "| `Thing` | `mod.thing.update` | **MUST** | a disagreement |",
            "| `Thing` | `mod.thing.grant` | **MUST** (`ReadSensitive`) | read, then compared |",
            "| `Thing` | `mod.thing.purge` | **MUST** (`bogus_kind`) | read, and not a type |",
            "| `Thing` | `mod.thing.erase` | **MUST** (`SecurityEvent`) | the enum's spelling agrees |",
            "| `Thing` | `mod.thing.revoke` | **MUST** (`security-event`) | the documentation's agrees |",
        ];

        ForwardProblems(entries, new Dictionary<string, string[]> { ["mod"] = matrix }).Should().SatisfyRespectively(
            create => create.Should().StartWith("mod.thing.create:").And.Contain("no class"),
            read => read.Should().StartWith("mod.thing.read:").And.Contain("bogus-kind"),
            update => update.Should().StartWith("mod.thing.update:").And.Contain("the matrix says Must"),
            grant => grant.Should().StartWith("mod.thing.grant:").And.Contain("the matrix says ReadSensitive"),
            purge => purge.Should().StartWith("mod.thing.purge:").And.Contain("bogus_kind"));
    }

    [Fact]
    public void An_off_path_marker_has_to_match_how_the_operation_is_registered()
    {
        string[] matrix =
        [
            "| Resource | Operation | Class | Why |",
            "|---|---|---|---|",
            "| `Thing` | `mod.thing.create` `(off-path)` | **MUST** | a request type writes it |",
            "| `Thing` | `mod.thing.purge` | **MUST** | an off-path writer writes it |",
            "| `Thing` | `mod.thing.enter` `(off-path)` | **MUST** | marked, and off-path |",
        ];

        var registered = new HashSet<string>(
            ["mod.thing.create", "mod.thing.purge", "mod.thing.enter"], StringComparer.Ordinal);
        var offPath = new HashSet<string>(["mod.thing.purge", "mod.thing.enter"], StringComparer.Ordinal);

        ReverseProblems(MatrixSlugs(matrix).Select(row => ("mod", row.Slug, row.Cell)), registered, offPath)
            .Should().SatisfyRespectively(
                create => create.Should().StartWith("mod/mod.thing.create:").And.Contain("a request type registers it"),
                purge => purge.Should().StartWith("mod/mod.thing.purge:").And.Contain("does not say (off-path)"));
    }

    [Fact]
    public void A_slug_classified_twice_fails_whichever_row_comes_first()
    {
        // The fourth review of Packet 9: a correct row followed by a contradictory copy
        // passed the forward join, which read only the first carrier, and the same two rows
        // in the other order failed it. Every carrier is compared now, and the reverse
        // direction refuses the copy itself — in either order.
        AuditCatalogEntry[] entries =
        [
            new("mod", "mod.thing.create", OperationType.Create, OperationClass.Must, typeof(object)),
        ];

        const string Header = "| Resource | Operation | Class | Why |";
        const string Rule = "|---|---|---|---|";
        const string Correct = "| `Thing` | `mod.thing.create` | **MUST** (`create`) | the row |";
        const string Copy = "| `Thing` | `mod.thing.create` | MAY (`delete`) | a stale copy |";

        string[][] orders = [[Header, Rule, Correct, Copy], [Header, Rule, Copy, Correct]];
        var registered = new HashSet<string>(["mod.thing.create"], StringComparer.Ordinal);

        foreach (var matrix in orders)
        {
            ForwardProblems(entries, new Dictionary<string, string[]> { ["mod"] = matrix })
                .Should().BeEquivalentTo(
                    ["mod.thing.create: the catalogue says Must, the matrix says May",
                     "mod.thing.create: the catalogue says Create, the matrix says Delete"],
                    "the copy disagrees wherever it sits");

            ReverseProblems(MatrixSlugs(matrix).Select(row => ("mod", row.Slug, row.Cell)), registered, offPath: [])
                .Should().ContainSingle().Which.Should().StartWith("mod.thing.create: classified in 2 rows");
        }
    }

    [Fact]
    public void The_Forward_Sweep_Reads_Only_The_Registering_Module_s_Matrix()
    {
        // Audit Coverage: a module's registration has its row in THAT module's matrix. The
        // sweep searched every matrix, so the third review of Packet 9 moved
        // tenancy.tenant.create's row into Customization's table and every case stayed green.
        // A copy left beside the real row is the same drift from the other side: a reader of
        // the second matrix sees a classification the catalogue never compares.
        //
        // A platform-scope operation is the explicit exception: no module of its own, so its
        // one row sits in whichever matrix the module that writes it keeps — and one is the
        // number that matters.
        AuditCatalogEntry[] entries =
        [
            new("mod", "mod.thing.create", OperationType.Create, OperationClass.Must, typeof(object)),
            new("mod", "mod.thing.update", OperationType.Update, OperationClass.Must, typeof(object)),
            new("platform", "platform.scope.enter", OperationType.SecurityEvent, OperationClass.Must, null),
            new("platform", "platform.scope.leave", OperationType.SecurityEvent, OperationClass.Must, null),
        ];

        var matrices = new Dictionary<string, string[]>
        {
            ["mod"] =
            [
                "| Resource | Operation | Class | Why |",
                "|---|---|---|---|",
                "| `Thing` | `mod.thing.update` | **MUST** | where it belongs |",
                "| any | `platform.scope.leave` `(off-path)` | **MUST** | one of two |",
            ],
            ["other"] =
            [
                "| Resource | Operation | Class | Why |",
                "|---|---|---|---|",
                "| `Thing` | `mod.thing.create` | **MUST** | moved here |",
                "| `Thing` | `mod.thing.update` | SHOULD | copied here |",
                "| any | `platform.scope.enter` `(off-path)` | **MUST** | the one row, in the writer's matrix |",
                "| any | `platform.scope.leave` `(off-path)` | **MUST** | two of two |",
            ],
        };

        ForwardProblems(entries, matrices).Should().SatisfyRespectively(
            moved => moved.Should().StartWith("mod.thing.create:").And.Contain("other's matrix"),
            missing => missing.Should().StartWith("mod.thing.create:").And.Contain("no row in mod's matrix"),
            copied => copied.Should().StartWith("mod.thing.update:").And.Contain("other's matrix"),
            twice => twice.Should().StartWith("platform.scope.leave:").And.Contain("mod and other"));
    }

    /// <summary>
    /// A module that ships an aggregate or a request type has a coverage matrix to classify
    /// them in.
    /// </summary>
    [Fact]
    public void Every_Module_With_An_Aggregate_Or_A_Request_Has_A_Matrix()
    {
        // ADR-0044 Amendment 4 § 3 binds the matrix to a module that has shipped an
        // aggregate or a request type. Every_Module_Has_An_AuditCoverage_Matrix walks the
        // spec directories that exist; this walks the CODE, so a new module that ships an
        // aggregate or a command with no spec directory at all is caught — the state in
        // which the matrix join has nothing to compare against and passes. Aggregates are
        // half of it: the review of Packet 9 added one to a scaffold module's Domain, with no
        // handler and no matrix, and the request-only version of this rule stayed green.
        var owning = ModulesWithCode(ShippedRequestTypes(), AuditCatalogDiscovery.Types());

        owning.Should().Contain(["Tenancy", "Customization", "Audit"],
            "a discovery that found no module would pass while proving nothing — and Audit "
            + "ships aggregates and no request, which is exactly the half being checked");

        ModulesWithoutMatrix(owning, ModuleSpecs()).Should().BeEmpty(
            "a module that ships an aggregate or a command owes a matrix that classifies it");
    }

    [Fact]
    public void The_Module_Sweep_Can_Actually_Fail()
    {
        // Both halves, on a module with no spec directory: an aggregate root with no
        // request, and the predicate that names what has no matrix.
        var owning = ModulesWithCode([], [typeof(LearnStack.Modules.Ghost.Domain.GhostAggregate), typeof(string)]);

        owning.Should().Equal("Ghost");
        ModulesWithoutMatrix(["Tenancy", .. owning], ModuleSpecs()).Should().Equal("Ghost");
    }

    /// <summary>The request types among <paramref name="shipped"/> the catalogue does not know.</summary>
    private static List<Type> UnregisteredOf(IEnumerable<Type> shipped, AuditCatalog catalog) =>
        [.. shipped.Where(request => !catalog.TryGet(request, out _))];

    /// <summary>The catalogue → matrix direction's findings, one line each.</summary>
    /// <remarks>
    /// <para>
    /// The registering module's matrix, and no other: Audit Coverage puts a module's
    /// registrations in its own file, and a sweep of every file accepted a row moved into —
    /// or copied into — the wrong one.
    /// </para>
    /// <para>
    /// <see cref="PlatformSegment"/> is the one exception, and it is explicit: a
    /// platform-scope operation's row sits in the matrix of the module that writes it, in
    /// exactly one.
    /// </para>
    /// </remarks>
    private static List<string> ForwardProblems(
        IEnumerable<AuditCatalogEntry> entries, Dictionary<string, string[]> matrices)
    {
        var problems = new List<string>();

        foreach (var entry in entries)
        {
            var carrying = matrices
                .Where(matrix => FindRows(entry.Operation, matrix.Value).Count > 0)
                .Select(matrix => matrix.Key)
                .Order(StringComparer.Ordinal)
                .ToList();

            string? home;

            if (string.Equals(entry.ModuleName, PlatformSegment, StringComparison.Ordinal))
            {
                if (carrying.Count > 1)
                {
                    problems.Add(
                        $"{entry.Operation}: {string.Join(" and ", carrying)} all classify it — a "
                        + "platform operation has one row, in the matrix of the module that writes it");
                }

                home = carrying.FirstOrDefault();
            }
            else
            {
                foreach (var stray in carrying.Where(module =>
                    !string.Equals(module, entry.ModuleName, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add(
                        $"{entry.Operation}: {stray}'s matrix classifies it, but {entry.ModuleName} "
                        + "registers it — a module's operations are classified in its own matrix");
                }

                home = carrying.FirstOrDefault(module =>
                    string.Equals(module, entry.ModuleName, StringComparison.OrdinalIgnoreCase));
            }

            if (home is null)
            {
                problems.Add($"{entry.Operation}: no row in {entry.ModuleName}'s matrix carries this slug");
                continue;
            }

            // EVERY row that carries it, not the first: a correct row followed by a
            // contradictory copy passed, and the same two rows in the other order failed —
            // measured by the fourth review of Packet 9. That a slug has one row at all is the
            // reverse direction's check; here each carrier has to agree.
            foreach (var row in FindRows(entry.Operation, matrices[home]))
            {
                problems.AddRange(RowProblems(entry, row));
            }
        }

        return problems;
    }

    /// <summary>What one matrix row says that the catalogue entry does not.</summary>
    private static IEnumerable<string> RowProblems(AuditCatalogEntry entry, string row)
    {
        var declared = ClassOf(row);

        if (declared is null)
        {
            yield return $"{entry.Operation}: the matrix states no class — MUST, SHOULD or MAY — "
                + $"for an operation the catalogue registers at {entry.OperationClass}";
        }
        else if (declared != entry.OperationClass)
        {
            yield return $"{entry.Operation}: the catalogue says {entry.OperationClass}, "
                + $"the matrix says {declared}";
        }

        var (spelled, type) = TypeOf(row);

        if (spelled is not null && type is null)
        {
            yield return $"{entry.Operation}: the matrix names the operation type `{spelled}`, "
                + "which OperationType does not have";
        }
        else if (type is not null && type != entry.OperationType)
        {
            yield return $"{entry.Operation}: the catalogue says {entry.OperationType}, "
                + $"the matrix says {type}";
        }
    }

    /// <summary>
    /// The one slug segment that names no module: a platform-scope operation.
    /// </summary>
    /// <remarks>
    /// The builder takes an off-path entry's module from its slug's first segment, so
    /// <c>platform.admin_scope.enter</c> reaches the join as <c>platform</c> — a module with no
    /// matrix, because there is no such module to write one
    /// (<see href="../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment 4
    /// § 3</see>). Tenancy writes it and Tenancy's matrix carries its row.
    /// </remarks>
    private const string PlatformSegment = "platform";

    /// <summary>The modules that own a request type or an aggregate root, by name.</summary>
    private static List<string> ModulesWithCode(IEnumerable<Type> requests, IEnumerable<Type> types) =>
        [.. requests
            .Concat(types.Where(type => type is { IsAbstract: false, IsInterface: false }
                && type.GetInterfaces().Any(contract => contract.IsGenericType
                    && contract.GetGenericTypeDefinition() == typeof(IAggregateRoot<>))))
            .Select(ModuleOf)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)];

    /// <summary>The reverse direction's findings, one line each.</summary>
    private static List<string> ReverseProblems(
        IEnumerable<(string Module, string Slug, string Cell)> rows,
        HashSet<string> registered,
        HashSet<string> offPath)
    {
        var problems = new List<string>();
        var all = rows.ToList();

        foreach (var (module, slug, cell) in all)
        {
            var planned = cell.Contains("(planned)", StringComparison.Ordinal);
            var offPathMarked = cell.Contains("(off-path)", StringComparison.Ordinal);

            if (registered.Contains(slug))
            {
                // The marker says who writes the row, and a reader believes it. A request-keyed
                // operation marked (off-path) sends them looking for a writer that is not
                // there; an off-path one without it sends them looking for a request type
                // (the fifth review of Packet 9).
                if (offPathMarked != offPath.Contains(slug))
                {
                    problems.Add(offPathMarked
                        ? $"{module}/{slug}: the matrix says (off-path), but a request type registers it"
                        : $"{module}/{slug}: the catalogue declares it off-path, and the row does not say (off-path)");
                }

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

            if (!planned && !offPathMarked)
            {
                problems.Add(
                    $"{module}/{slug}: the matrix classifies it and nothing registers "
                    + "it, and it carries no (planned) or (off-path) marker");
            }
        }

        // One operation, one row. A copy is a second answer to the question the matrix
        // exists to settle, and it passes every comparison the moment it agrees — or, for a
        // (planned) slug, before anything compares it at all.
        foreach (var copies in all
            .GroupBy(row => row.Slug, StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            problems.Add(
                $"{copies.Key}: classified in {copies.Count()} rows "
                + $"({string.Join(", ", copies.Select(row => row.Module))}) — one operation has one row");
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
        [.. AuditCatalogDiscovery.Types()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .SelectMany(type => type.GetInterfaces())
            .Where(contract => contract.IsGenericType
                && (contract.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
                    || contract.GetGenericTypeDefinition() == typeof(IRequestHandler<>)))
            .Select(contract => contract.GetGenericArguments()[0])
            .Distinct()];

    /// <summary>The catalogue the composition roots build — see <see cref="AuditCatalogDiscovery"/>.</summary>
    private static AuditCatalog Catalogue() => AuditCatalogDiscovery.Catalogue();

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

    /// <summary>Every matrix row whose Operation cell carries the slug.</summary>
    /// <remarks>
    /// Matched on the cell rather than on the line, because a slug also appears in the
    /// prose above and below the table — and a prose mention is not a classification.
    /// </remarks>
    private static List<string> FindRows(string operation, IEnumerable<string> lines) =>
        [.. lines
            .Where(line => line.StartsWith('|'))
            // Cell 2, not 1: splitting on '|' leaves an empty first element, so the
            // columns are Resource, Operation, Class at 1, 2 and 3.
            .Where(line => Cells(line).Length >= 4
                && Cells(line)[2].Contains('`' + operation + '`', StringComparison.Ordinal))];

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
    /// The operation type the row names in parentheses, as spelled and as parsed — both
    /// <c>null</c> when it names none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted whatever it says, then read: an extraction that matched only the spelling
    /// it could parse turned every other annotation into "names no type", and the comparison
    /// was skipped for it — measured by the third review with `ReadSensitive` and
    /// `bogus_kind`. A row that names a type the enum does not have is refused by the caller.
    /// </para>
    /// <para>
    /// The matrix writes Audit Coverage's hyphenated spelling — `security-event`,
    /// `read-sensitive` — and the enum is PascalCase, so the hyphen is stripped and case
    /// ignored; the enum's own spelling reads the same. Letters only, because
    /// <c>Enum.TryParse</c> also accepts a number and a comma-separated list, and neither is a
    /// type.
    /// </para>
    /// </remarks>
    private static (string? Spelled, OperationType? Type) TypeOf(string row)
    {
        var match = TypePattern().Match(Cells(row)[3]);

        if (!match.Success)
        {
            return (null, null);
        }

        var spelled = match.Groups[1].Value.Trim();
        var normalized = spelled.Replace("-", string.Empty, StringComparison.Ordinal);

        return normalized.Length > 0
            && normalized.All(char.IsAsciiLetter)
            && Enum.TryParse<OperationType>(normalized, ignoreCase: true, out var parsed)
            ? (spelled, parsed)
            : (spelled, null);
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

    /// <summary>A parenthesised annotation in the Class cell, backticked or not, whatever it says.</summary>
    [GeneratedRegex("\\(\\s*`?([^`()]+)`?\\s*\\)")]
    private static partial Regex TypePattern();

    /// <summary>A dotted audit slug in backticks: `{module}.{resource}.{verb}`.</summary>
    [GeneratedRegex("`([a-z][a-z0-9_]*\\.[a-z0-9_]+\\.[a-z0-9_]+)`")]
    private static partial Regex SlugPattern();

    /// <summary>A module namespace: <c>LearnStack.Modules.{Name}.…</c>.</summary>
    [GeneratedRegex("^LearnStack\\.Modules\\.([A-Za-z]+)\\.")]
    private static partial Regex ModuleNamespace();
}
