using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using LearnStack.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// Runs an in-tree analyzer over a project's real sources and reports what it found, for
/// <c>Domain_Methods_Do_Not_Throw_For_Expected_Cases</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a compilation of our own rather than the build's.</b> <c>LS0001</c> is listed in
/// <c>WarningsNotAsErrors</c> until the Phase 03 escalation ADR-0032 Amendment 1 documents,
/// so the build does not fail on it — and a <c>#pragma warning disable LS0001</c> is the
/// sanctioned way to keep a genuine aggregate-invariant throw. Both mean the build cannot be
/// the gate. Here the severity is set by this file and suppressed diagnostics are reported,
/// so neither a pragma nor a project setting can hide one.
/// </para>
/// <para>
/// <b>The compilation is not expected to be error-free.</b> Vogen's generated members and
/// every other source generator are absent from it: the analyzer reads the syntax it is
/// registered for and the semantic model resolves <c>DomainException</c> from the referenced
/// <c>LearnStack.SharedKernel</c> assembly, which is what it needs. Syntax errors are a
/// different matter and fail loudly — a compiler behind the SDK reads a new language feature
/// as one, and a scan that cannot parse a file cannot see what is in it.
/// </para>
/// </remarks>
internal static class AnalyzerReport
{
    /// <summary>One LS0001 report, placed in the source it came from.</summary>
    internal sealed record Finding(string File, int Line, string Member, bool ReturnsResult, bool Suppressed)
    {
        public override string ToString() =>
            $"{Path.GetFileName(File)}:{Line} {Member}"
            + (ReturnsResult ? " (returns Result)" : string.Empty)
            + (Suppressed ? " (suppressed)" : string.Empty);
    }

    /// <summary>What one project's analyzer run produced.</summary>
    internal sealed record Run(string Project, IReadOnlyList<Finding> Findings, int ResultMembers);

    /// <summary>
    /// Whether a report is one the rule refuses.
    /// </summary>
    /// <remarks>
    /// Two shapes, for one reason each. An <b>unsuppressed</b> report anywhere is a Warning the
    /// build prints and nobody has claimed: ADR-0032 § Sub-decision 4 reserves
    /// <c>DomainException</c> for programmer errors, and the catalogue entry for the analyzer
    /// makes a genuine aggregate-invariant throw the rare site that suppresses <i>with
    /// justification</i>. A report inside a <b>Result-returning</b> member is refused even when
    /// suppressed: that method has a channel for an expected case — returning
    /// <c>Result.Fail(business_rule_violation, …)</c> — so a throw there is the expected case
    /// taking the exception path, and a pragma over it silences the very question.
    /// </remarks>
    public static bool Violates(Finding finding) => finding.ReturnsResult || !finding.Suppressed;

    /// <summary>
    /// The projects the <c>LS0001</c> analyzer is wired into: the core <c>Domain</c> and
    /// <c>Application</c>, and every module's.
    /// </summary>
    public static IReadOnlyList<string> Projects()
    {
        var source = RepositoryPaths.BackendSrc();

        return
        [
            .. CoreProjects.Select(name => Path.Combine(source, name, $"{name}.csproj")),
            .. Modules.Names
                .SelectMany(module => AnalyzedLayers
                    .Select(layer => Path.Combine(
                        source, "Modules", module,
                        $"LearnStack.Modules.{module}.{layer}",
                        $"LearnStack.Modules.{module}.{layer}.csproj"))),
        ];
    }

    /// <summary>The two core projects the analyzer is wired into.</summary>
    private static readonly string[] CoreProjects = ["LearnStack.Domain", "LearnStack.Application"];

    /// <summary>The two layers of a module it is wired into.</summary>
    private static readonly string[] AnalyzedLayers = ["Domain", "Application"];

    /// <summary>
    /// Compiles a project's sources — plus any <paramref name="planted"/> ones — and runs the
    /// <see cref="DomainExceptionThrowAnalyzer"/> over them.
    /// </summary>
    public static async Task<Run> RunAsync(string projectPath, params string[] planted)
    {
        var name = Path.GetFileNameWithoutExtension(projectPath);
        var directory = Path.GetDirectoryName(projectPath)!;
        var parse = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.None);

        var trees = Sources(directory)
            .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), parse, file))
            .Concat(planted.Select((code, index) =>
                CSharpSyntaxTree.ParseText(code, parse, Path.Combine(directory, $"Planted{index}.cs"))))
            .ToList();

        var unparsable = trees
            .SelectMany(tree => tree.GetDiagnostics())
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic =>
                $"{diagnostic.Location.GetLineSpan()}: {diagnostic.GetMessage(CultureInfo.InvariantCulture)}")
            .ToList();

        if (unparsable.Count > 0)
        {
            throw new InvalidOperationException(
                $"{name} does not parse with the pinned Roslyn — raise Microsoft.CodeAnalysis.CSharp "
                + "to the SDK's compiler, or this scan is blind to whatever it cannot read:"
                + Environment.NewLine + string.Join(Environment.NewLine, unparsable.Take(5)));
        }

        var compilation = CSharpCompilation.Create(
            name,
            trees,
            References.Value.Where(reference =>
                Path.GetFileNameWithoutExtension(reference.Display) != name),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                specificDiagnosticOptions: new Dictionary<string, ReportDiagnostic>(StringComparer.Ordinal)
                {
                    [DomainExceptionThrowAnalyzer.DiagnosticId] = ReportDiagnostic.Warn,
                }));

        var kernel = compilation.GetTypeByMetadataName("LearnStack.SharedKernel.Errors.DomainException");

        if (kernel is null)
        {
            throw new InvalidOperationException(
                $"{name}'s compilation cannot resolve DomainException, so the analyzer has "
                + "nothing to recognise and would report nothing whatever the code did.");
        }

        var diagnostics = await compilation
            .WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new DomainExceptionThrowAnalyzer()),
                new CompilationWithAnalyzersOptions(
                    new AnalyzerOptions([]),
                    onAnalyzerException: null,
                    concurrentAnalysis: true,
                    logAnalyzerExecutionTime: false,
                    reportSuppressedDiagnostics: true))
            .GetAnalyzerDiagnosticsAsync()
            .ConfigureAwait(false);

        var crashed = diagnostics.Where(diagnostic => diagnostic.Id == "AD0001").ToList();

        if (crashed.Count > 0)
        {
            throw new InvalidOperationException(
                $"The analyzer crashed on {name}: {crashed[0].GetMessage(CultureInfo.InvariantCulture)}");
        }

        return new Run(
            name,
            [.. diagnostics
                .Where(diagnostic => diagnostic.Id == DomainExceptionThrowAnalyzer.DiagnosticId)
                .Select(Locate)],
            trees.Sum(tree => ResultReturningMembers(tree).Count));
    }

    /// <summary>The member a diagnostic sits in, and whether that member returns a result.</summary>
    private static Finding Locate(Diagnostic diagnostic)
    {
        var position = diagnostic.Location.GetLineSpan();
        var tree = diagnostic.Location.SourceTree!;
        var node = tree.GetRoot().FindNode(diagnostic.Location.SourceSpan);

        var member = node.AncestorsAndSelf()
            .FirstOrDefault(ancestor => ancestor is MethodDeclarationSyntax or LocalFunctionStatementSyntax);

        return new Finding(
            position.Path,
            position.StartLinePosition.Line + 1,
            member switch
            {
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                LocalFunctionStatementSyntax local => local.Identifier.ValueText,
                _ => "(no enclosing method)",
            },
            member is not null && ReturnsResult(ReturnTypeOf(member)),
            diagnostic.IsSuppressed);
    }

    /// <summary>Every method and local function in a tree whose return type is a result.</summary>
    public static IReadOnlyList<SyntaxNode> ResultReturningMembers(SyntaxTree tree) =>
        [.. tree.GetRoot().DescendantNodes()
            .Where(node => node is MethodDeclarationSyntax or LocalFunctionStatementSyntax)
            .Where(node => ReturnsResult(ReturnTypeOf(node)))];

    private static TypeSyntax? ReturnTypeOf(SyntaxNode member) => member switch
    {
        MethodDeclarationSyntax method => method.ReturnType,
        LocalFunctionStatementSyntax local => local.ReturnType,
        _ => null,
    };

    /// <summary>
    /// Whether a return type is <c>Result</c> or <c>Result&lt;T&gt;</c>, awaited or not.
    /// </summary>
    /// <remarks>
    /// Read from the syntax rather than from a symbol: the compilation is missing every
    /// generated member, so a semantic answer would be an error type for some of these and
    /// the walk would quietly shrink.
    /// </remarks>
    public static bool ReturnsResult(TypeSyntax? type)
    {
        switch (type)
        {
            case QualifiedNameSyntax qualified:
                return ReturnsResult(qualified.Right);
            case GenericNameSyntax generic when generic.Identifier.ValueText is "Task" or "ValueTask":
                return generic.TypeArgumentList.Arguments.Count == 1
                    && ReturnsResult(generic.TypeArgumentList.Arguments[0]);
            case GenericNameSyntax generic:
                return generic.Identifier.ValueText == "Result";
            case IdentifierNameSyntax identifier:
                return identifier.Identifier.ValueText == "Result";
            default:
                return false;
        }
    }

    private static IEnumerable<string> Sources(string projectDirectory) =>
        Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .Concat(GlobalUsings(projectDirectory));

    /// <summary>
    /// The <c>GlobalUsings.g.cs</c> the SDK generated for the project, if the project has been
    /// built.
    /// </summary>
    /// <remarks>
    /// Read rather than reconstructed: <c>ImplicitUsings</c> and any <c>&lt;Using&gt;</c> item
    /// decide the set, and a list written here would be a second copy of a build setting.
    /// </remarks>
    private static IEnumerable<string> GlobalUsings(string projectDirectory)
    {
        var obj = Path.Combine(projectDirectory, "obj");

        return Directory.Exists(obj)
            ? Directory.EnumerateFiles(obj, "*.GlobalUsings.g.cs", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(1)
            : [];
    }

    /// <summary>
    /// Everything the test host has loaded, as metadata references: the framework, the
    /// packages, and every LearnStack assembly this project references.
    /// </summary>
    private static readonly Lazy<List<MetadataReference>> References = new(() =>
    {
        var beside = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll");
        var platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        return [.. beside.Concat(platform)
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(IsManaged)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    });

    private static bool IsManaged(string path)
    {
        try
        {
            _ = AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
