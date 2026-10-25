using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace LearnStack.Tests.Unit.Analyzers;

/// <summary>Compiler-clean samples and isolated source recompilation for analyzer proofs.</summary>
internal static class AnalyzerCompilation
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("The test host has no platform assembly references."))
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var roslyn = new[]
        {
            typeof(DiagnosticAnalyzer).Assembly.Location,
            typeof(CSharpCompilation).Assembly.Location,
        };
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);

        return [.. platform.Where(path => Path.GetDirectoryName(path) == runtimeDirectory)
            .Concat(roslyn).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))];
    });

    internal static CSharpCompilation Create(params (string Source, string Path)[] sources)
        => CreateWithSettings(References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable), sources);

    internal static CSharpCompilation CreateWithSettings(
        IEnumerable<MetadataReference> references,
        CSharpCompilationOptions options,
        params (string Source, string Path)[] sources)
    {
        var compilation = CSharpCompilation.Create(
            "AnalyzerProof_" + Guid.NewGuid().ToString("N"),
            sources.Select(source => CSharpSyntaxTree.ParseText(
                source.Source, new CSharpParseOptions(LanguageVersion.Latest), source.Path)),
            references,
            options);

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Should().BeEmpty("an analyzer proof must compile cleanly before its diagnostic is trusted");
        return compilation;
    }

    internal static (MetadataReference[] References, CSharpCompilationOptions Options) EvaluatedAnalyzerSettings(string project)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                ArgumentList =
                {
                    "msbuild", project, "-target:ResolveReferences,CoreCompile", "-getItem:ReferencePath",
                    "-getProperty:NoWarn",
                    "-property:SkipCompilerExecution=true",
                    "-property:Configuration=Release", "-nologo", "-verbosity:quiet",
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(project)!,
            },
        };
        process.StartInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new InvalidOperationException("Resolving the analyzer's actual reference framework timed out.");
        }

        Task.WaitAll(output, error);
        process.ExitCode.Should().Be(0, "the analyzer's real references must resolve: {0}", error.Result);
        using var result = JsonDocument.Parse(output.Result);
        var references = result.RootElement.GetProperty("Items").GetProperty("ReferencePath").EnumerateArray()
            .Select(item => (MetadataReference)MetadataReference.CreateFromFile(item.GetProperty("FullPath").GetString()!))
            .ToArray();
        references.Should().NotBeEmpty();
        // Clone compilation follows the actual analyzer project's SDK binding
        // policy. Behavioral samples above retain their unsuppressed compiler gate.
        var noWarn = result.RootElement.GetProperty("Properties").GetProperty("NoWarn").GetString()!
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(id => int.TryParse(id, out var number) ? "CS" + number.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) : id)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, _ => ReportDiagnostic.Suppress, StringComparer.Ordinal);
        return (references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable, specificDiagnosticOptions: noWarn));
    }

    internal static async Task<ImmutableArray<Diagnostic>> RunAsync(
        string source,
        DiagnosticAnalyzer analyzer,
        AnalyzerOptions? options = null,
        string path = "Sample.cs")
    {
        var diagnostics = await Create((source, path))
            .WithAnalyzers(ImmutableArray.Create(analyzer), options ?? new AnalyzerOptions([]))
            .GetAnalyzerDiagnosticsAsync();
        diagnostics.Should().NotContain(diagnostic => diagnostic.Id == "AD0001");
        return diagnostics;
    }

    internal static Assembly Load(CSharpCompilation compilation, AssemblyLoadContext context)
    {
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        emitted.Diagnostics.Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Should().BeEmpty("the source mutation must emit a clean assembly");
        emitted.Success.Should().BeTrue();
        stream.Position = 0;
        return context.LoadFromStream(stream);
    }

    internal static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CLAUDE.md"))) return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate the repository for the source propagation proof.");
    }

    internal sealed class DomainScopeOptions(string value) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new ScopeOption(value);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;

        private sealed class ScopeOption(string value) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string result)
            {
                result = key == "build_property.LearnStackAnalyzeDomainExceptionThrow" ? value : string.Empty;
                return key == "build_property.LearnStackAnalyzeDomainExceptionThrow";
            }
        }
    }
}
