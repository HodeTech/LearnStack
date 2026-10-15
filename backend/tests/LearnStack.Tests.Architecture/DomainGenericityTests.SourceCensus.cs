using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace LearnStack.Tests.Architecture;

public sealed partial class DomainGenericityTests
{
    private static readonly string[] DemoOutputProperties = ["BaseOutputPath", "BaseIntermediateOutputPath"];

    private static string[] DemoProductionSourceCensus()
    {
        var projects = ProductionSourceRoots.SelectMany(root => Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal).ToArray();
        var evaluated = projects.Select(EvaluatedDemoCompile).ToArray();
        RequireProductionProjectCoverage(projects, evaluated.SelectMany(row => row.References));
        var outputs = evaluated.SelectMany(row => row.Outputs).Distinct(StringComparer.Ordinal).ToArray();
        var census = ProductionSourceRoots.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => !OmitIgnoredDemoLocalFile(path)).ToArray();
        var files = census.Where(path => BackendDemoSourceKind(path, outputs) == 1).ToArray();
        // Evaluation includes explicit/linked Compile items as well as the SDK's default glob.
        // A compiler input outside the supported census must refuse, never disappear.
        RequireCompileCoverage(files, evaluated.SelectMany(row => row.Sources));
        foreach (var file in census
            .Where(path => BackendDemoSourceKind(path, outputs) == 2)
            .Concat([Path.Combine(RepositoryPaths.RepoRoot(), "backend/Directory.Build.props"),
                Path.Combine(RepositoryPaths.RepoRoot(), "backend/Directory.Packages.props")])
            .Where(path => Path.GetExtension(path) is ".csproj" or ".props" or ".targets"))
            RefuseExecutionTimeCompile(File.ReadAllText(file));
        return files;
    }

    private static bool IsDemoLocalArtifact(string path) => Path.GetFileName(path) is
        ".env" or ".env.local" or ".env.development" or ".env.test" or ".env.production"
        or ".env.development.local" or ".env.test.local" or ".env.production.local" or ".DS_Store" or "Thumbs.db"
        || Path.GetExtension(path) is ".swp" or ".log" or ".suo" or ".rsuser";

    private static bool OmitIgnoredDemoLocalFile(string path)
    {
        if (!IsDemoLocalArtifact(path)) return false;
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                ArgumentList = { "check-ignore", "--quiet", "--", path },
                WorkingDirectory = RepositoryPaths.RepoRoot(),
                RedirectStandardError = true,
            }
        };
        child.Start();
        var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(10_000))
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
            throw new InvalidOperationException("Git ignored-local-file admission deadline.");
        }
        error.GetAwaiter().GetResult();
        return child.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new InvalidOperationException("Git ignored-local-file admission failed.")
        };
    }

    private static int BackendDemoSourceKind(string path, IEnumerable<string> outputs)
    {
        path = Path.GetFullPath(path);
        if (outputs.Any(root => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            return 0;
        if (Path.GetExtension(path) is ".cs" or ".json" or ".sql") return 1;
        if (Path.GetExtension(path) is ".csproj" or ".props" or ".targets" or ".md" || Path.GetFileName(path) == ".editorconfig") return 2;
        throw new InvalidOperationException($"Unclassified backend production source: {path}; Fix: review its census ownership.");
    }

    private static void RequireCompileCoverage(IEnumerable<string> files, IEnumerable<string> compiled)
    {
        var admitted = files.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
        if (compiled.Any(path => !admitted.Contains(Path.GetFullPath(path))))
            throw new InvalidOperationException("Unclassified Compile input; Fix: include the linked source in the supported production census.");
    }

    private static void RequireProductionProjectCoverage(IEnumerable<string> projects, IEnumerable<string> references)
    {
        var admitted = projects.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
        if (references.Any(path => !admitted.Contains(Path.GetFullPath(path))))
            throw new InvalidOperationException("Unclassified production ProjectReference; Fix: include its compiler inputs in the supported census.");
    }

    // Property-selected SDK hooks can import targets absent from evaluation-only Compile items.
    // New build properties must be reviewed rather than silently expanding this bounded model.
    private static readonly HashSet<string> DemoBuildProperties = new(StringComparer.Ordinal)
    {
        "AnalysisLevel", "AnalysisMode", "AssemblyName", "EnforceCodeStyleInBuild",
        "EnforceExtendedAnalyzerRules", "GenerateDocumentationFile", "ImplicitUsings", "IsPackable",
        "LangVersion", "ManagePackageVersionsCentrally", "NoWarn", "NuGetAuditLevel", "NuGetAuditMode",
        "Nullable", "OutputType", "RootNamespace", "TargetFramework", "TreatWarningsAsErrors",
        "UserSecretsId", "WarningsNotAsErrors"
    };

    private static void RefuseExecutionTimeCompile(string source)
    {
        var document = XDocument.Parse(source);
        const string modulesImport = "$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))";
        if (document.Root?.Elements().Any(node => node.Name.LocalName is not ("PropertyGroup" or "ItemGroup" or "Import")) == true
            || document.Descendants().Where(node => node.Name.LocalName == "PropertyGroup")
                .SelectMany(node => node.Elements()).Any(node => !DemoBuildProperties.Contains(node.Name.LocalName))
            || document.Descendants().Any(node => node.Name.LocalName is "Target" or "Sdk"
                || node.Name.LocalName == "Import" && (string?)node.Attribute("Project") != modulesImport)
            || document.Root?.Attribute("Sdk") is { } sdk && sdk.Value is not ("Microsoft.NET.Sdk" or "Microsoft.NET.Sdk.Web"))
            throw new InvalidOperationException("Execution-time Compile/import shapes are unmodeled; Fix: review compiler-input admission before introducing a custom target or import.");
    }

    private static (string[] Sources, string[] Outputs, string[] References) EvaluatedDemoCompile(string project)
    {
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                ArgumentList = { "msbuild", project, "-getItem:Compile,ProjectReference", "-getProperty:BaseOutputPath,BaseIntermediateOutputPath", "-p:Configuration=Release", "-nologo" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(project)!,
            }
        };
        child.Start();
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(120_000))
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
            throw new InvalidOperationException("MSBuild Compile census deadline.");
        }
        Task.WaitAll(output, error);
        if (child.ExitCode != 0) throw new InvalidOperationException("MSBuild Compile census failed.");
        using var document = JsonDocument.Parse(output.Result);
        var sources = document.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
            .Select(row => Path.GetFullPath(row.GetProperty("FullPath").GetString()!)).ToArray();
        var references = document.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray()
            .Select(row => Path.GetFullPath(row.GetProperty("FullPath").GetString()!)).ToArray();
        var properties = document.RootElement.GetProperty("Properties");
        var outputs = DemoOutputProperties
            .Select(name => properties.GetProperty(name).GetString()!)
            .Select(path => Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(project)!)).ToArray();
        return (sources, outputs, references);
    }
}
