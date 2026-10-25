using System.Diagnostics;
using System.IO.Enumeration;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LearnStack.Tests.Architecture;

/// <summary>Reads the compiler's evaluated references and rule scope, including imports.</summary>
internal static class AnalyzerProjectWiring
{
    private static readonly char[] WarningSeparators = [';', ','];
    private static readonly string[] ExecutionProperties = ["RunAnalyzers", "RunAnalyzersDuringBuild"];
    internal sealed record Reference(string FullPath, string OutputItemType, string ReferenceOutputAssembly);
    internal sealed record Evaluation(Reference[] References, bool DomainRule, bool VisibleScope, string[] Exemptions,
        string[] Suppressions, bool RunsAnalyzers)
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
                    "-getProperty:LearnStackAnalyzeDomainExceptionThrow,WarningsNotAsErrors,NoWarn,RunAnalyzers,RunAnalyzersDuringBuild",
                },
                WorkingDirectory = Path.GetDirectoryName(project)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        child.StartInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";
        child.StartInfo.Environment["CI"] = "true";
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
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            properties.GetProperty("NoWarn").GetString()!
                .Split(WarningSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            !ExecutionProperties.Any(name =>
                properties.GetProperty(name).GetString()!.Equals("false", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Evaluation on one OS cannot prove an analyzer reference is unconditional.</summary>
    internal static bool HasUnconditionalReference(XDocument props)
    {
        var references = props.Descendants("ProjectReference").Where(element =>
            ((string?)element.Attribute("Include"))?.Replace('\\', '/')
                .EndsWith("/LearnStack.Analyzers/LearnStack.Analyzers.csproj", StringComparison.Ordinal) == true).ToArray();
        return references.Length == 1
            && (string?)references[0].Attribute("OutputItemType") == "Analyzer"
            && (string?)references[0].Attribute("ReferenceOutputAssembly") == "false"
            && !references[0].AncestorsAndSelf().Any(element =>
                element.Attribute("Condition") is not null || element.Name.LocalName is "Choose" or "When" or "Otherwise");
    }

    /// <summary>Also inspects settings inside targets that evaluation alone would not execute.</summary>
    internal static bool SuppressesSensitiveRule(XDocument build) => build.Descendants().Any(element =>
        element.Name.LocalName == "NoWarn" && Regex.IsMatch(element.Value, @"\bLS0002\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        || element.Name.LocalName is "RunAnalyzers" or "RunAnalyzersDuringBuild"
            && element.Value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)
        || ChangesAnalyzerItem(element));

    private static bool ChangesAnalyzerItem(XElement element)
    {
        var selector = element.Name.LocalName switch
        {
            "Analyzer" => element.Attribute("Remove"),
            "ProjectReference" => element.Attribute("Remove") ?? element.Attribute("Update"),
            _ => null,
        };
        var fileName = element.Name.LocalName == "Analyzer" ? "LearnStack.Analyzers.dll" : "LearnStack.Analyzers.csproj";
        return selector is not null
            && selector.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(pattern =>
                {
                    var selectedName = Path.GetFileName(pattern.Replace('\\', '/'));
                    return selectedName.Contains("$(", StringComparison.Ordinal) || selectedName.Contains("@(", StringComparison.Ordinal)
                        || FileSystemName.MatchesSimpleExpression(selectedName, fileName, ignoreCase: true);
                });
    }

    internal static bool SuppressesSensitiveRule(string editorConfig) => Regex.IsMatch(editorConfig,
        @"^\s*dotnet_(?:diagnostic\.LS0002|analyzer_diagnostic(?:\.category-Security)?)\.severity\s*=\s*(?:none|silent|suggestion)\s*(?:[#;].*)?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
