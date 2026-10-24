using System.Diagnostics;
using System.Text.Json;

namespace LearnStack.Tests.Architecture;

/// <summary>Reads the compiler's evaluated references and rule scope, including imports.</summary>
internal static class AnalyzerProjectWiring
{
    internal sealed record Reference(string FullPath, string OutputItemType, string ReferenceOutputAssembly);
    internal sealed record Evaluation(Reference[] References, bool DomainRule, bool VisibleScope, string[] Exemptions)
    {
        public bool HasAnalyzer(string analyzerPath)
        {
            var matches = References.Where(reference =>
                Path.GetFullPath(reference.FullPath) == Path.GetFullPath(analyzerPath)).ToArray();
            return matches.Length == 1 && matches[0].OutputItemType == "Analyzer"
                && matches[0].ReferenceOutputAssembly.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string AnalyzerPath => Path.Combine(RepositoryPaths.RepoRoot(),
        "backend", "analyzers", "LearnStack.Analyzers", "LearnStack.Analyzers.csproj");

    public static async Task<Evaluation> ReadAsync(string project, string configuration)
    {
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                ArgumentList =
                {
                    "msbuild", project, "-nologo", "-property:Configuration=" + configuration,
                    "-getItem:ProjectReference,CompilerVisibleProperty",
                    "-getProperty:LearnStackAnalyzeDomainExceptionThrow,WarningsNotAsErrors",
                },
                WorkingDirectory = Path.GetDirectoryName(project)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        child.Start();
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await child.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            await Task.WhenAll(output, error);
            throw new InvalidOperationException("Evaluating analyzer wiring exceeded its deadline.");
        }

        await Task.WhenAll(output, error);
        if (child.ExitCode != 0)
            throw new InvalidOperationException("Evaluating analyzer wiring failed for " + Path.GetFileName(project));
        return Parse(await output);
    }

    internal static Evaluation Parse(string output)
    {
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var properties = root.GetProperty("Properties");
        var items = root.GetProperty("Items");
        return new Evaluation(
            items.GetProperty("ProjectReference").EnumerateArray().Select(reference => new Reference(
                reference.GetProperty("FullPath").GetString()!,
                reference.TryGetProperty("OutputItemType", out var type) ? type.GetString()! : "",
                reference.TryGetProperty("ReferenceOutputAssembly", out var assembly) ? assembly.GetString()! : "")).ToArray(),
            bool.Parse(properties.GetProperty("LearnStackAnalyzeDomainExceptionThrow").GetString()!),
            items.GetProperty("CompilerVisibleProperty").EnumerateArray().Any(item =>
                item.GetProperty("Identity").GetString() == "LearnStackAnalyzeDomainExceptionThrow"),
            properties.GetProperty("WarningsNotAsErrors").GetString()!
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
