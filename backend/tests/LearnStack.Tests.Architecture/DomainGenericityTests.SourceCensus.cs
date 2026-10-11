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
        var outputs = evaluated.SelectMany(row => row.Outputs).Distinct(StringComparer.Ordinal).ToArray();
        var files = ProductionSourceRoots.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => BackendDemoSourceKind(path, outputs) == 1).ToArray();
        // Evaluation includes explicit/linked Compile items as well as the SDK's default glob.
        // A compiler input outside the supported census must refuse, never disappear.
        RequireCompileCoverage(files, evaluated.SelectMany(row => row.Sources));
        foreach (var file in ProductionSourceRoots.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => BackendDemoSourceKind(path, outputs) == 2)
            .Concat([Path.Combine(RepositoryPaths.RepoRoot(), "backend/Directory.Build.props"),
                Path.Combine(RepositoryPaths.RepoRoot(), "backend/Directory.Packages.props")])
            .Where(path => Path.GetExtension(path) is ".csproj" or ".props" or ".targets"))
            RefuseExecutionTimeCompile(File.ReadAllText(file));
        return files;
    }

    private static int BackendDemoSourceKind(string path, IEnumerable<string> outputs)
    {
        path = Path.GetFullPath(path);
        if (outputs.Any(root => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            return 0;
        if (Path.GetExtension(path) is ".cs" or ".json") return 1;
        if (Path.GetExtension(path) is ".csproj" or ".props" or ".targets" or ".md" || Path.GetFileName(path) == ".editorconfig") return 2;
        throw new InvalidOperationException($"Unclassified backend production source: {path}; Fix: review its census ownership.");
    }

    private static void RequireCompileCoverage(IEnumerable<string> files, IEnumerable<string> compiled)
    {
        var admitted = files.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
        if (compiled.Any(path => !admitted.Contains(Path.GetFullPath(path))))
            throw new InvalidOperationException("Unclassified Compile input; Fix: include the linked source in the supported production census.");
    }

    private static void RefuseExecutionTimeCompile(string source)
    {
        var document = XDocument.Parse(source);
        const string modulesImport = "$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))";
        if (document.Descendants().Any(node => node.Name.LocalName is "Target" or "Sdk"
                || node.Name.LocalName == "Import" && (string?)node.Attribute("Project") != modulesImport)
            || document.Root?.Attribute("Sdk") is { } sdk && sdk.Value is not ("Microsoft.NET.Sdk" or "Microsoft.NET.Sdk.Web"))
            throw new InvalidOperationException("Execution-time Compile/import shapes are unmodeled; Fix: review compiler-input admission before introducing a custom target or import.");
    }

    private static (string[] Sources, string[] Outputs) EvaluatedDemoCompile(string project)
    {
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                ArgumentList = { "msbuild", project, "-getItem:Compile", "-getProperty:BaseOutputPath,BaseIntermediateOutputPath", "-p:Configuration=Release", "-nologo" },
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
        var properties = document.RootElement.GetProperty("Properties");
        var outputs = DemoOutputProperties
            .Select(name => properties.GetProperty(name).GetString()!)
            .Select(path => Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(project)!)).ToArray();
        return (sources, outputs);
    }
}
