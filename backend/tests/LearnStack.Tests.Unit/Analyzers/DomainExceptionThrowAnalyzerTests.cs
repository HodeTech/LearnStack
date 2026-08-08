using System.Collections.Immutable;
using FluentAssertions;
using LearnStack.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace LearnStack.Tests.Unit.Analyzers;

/// <summary>
/// Locks the review-4 H1 regression: the analyzer's Roslyn diagnostic id
/// must be a valid identifier (<c>LS0001</c>) so reporting succeeds instead
/// of crashing with <c>AD0001</c> ("not a valid identifier"). Runs the real
/// <see cref="DomainExceptionThrowAnalyzer"/> over synthetic compilations.
/// </summary>
public sealed class DomainExceptionThrowAnalyzerTests
{
    private const string DomainExceptionShim = """
        namespace LearnStack.SharedKernel.Errors
        {
            public class DomainException : System.Exception
            {
                public DomainException(string message) : base(message) { }
            }
        }
        """;

    [Fact]
    public void DiagnosticId_Is_A_Valid_Roslyn_Identifier()
    {
        // Roslyn requires ids to be valid identifiers (no hyphens). The
        // human-readable rule name keeps the hyphenated form.
        DomainExceptionThrowAnalyzer.DiagnosticId.Should().Be("LS0001");
        DomainExceptionThrowAnalyzer.DiagnosticId.Should().MatchRegex("^[A-Za-z][A-Za-z0-9]*$");
        DomainExceptionThrowAnalyzer.RuleName.Should().Be("LearnStackException-DomainExceptionThrow");
    }

    [Fact]
    public async Task Reports_LS0001_On_Throw_New_DomainException()
    {
        const string source = DomainExceptionShim + """
            namespace Sample
            {
                public sealed class Aggregate
                {
                    public void Mutate() =>
                        throw new LearnStack.SharedKernel.Errors.DomainException("boom");
                }
            }
            """;

        var diagnostics = await RunAnalyzerAsync(source);

        // The key assertion: a real diagnostic with the valid id is produced —
        // NOT an AD0001 analyzer crash.
        diagnostics.Should().ContainSingle(d => d.Id == "LS0001");
        diagnostics.Should().NotContain(d => d.Id == "AD0001");
    }

    [Fact]
    public async Task Does_Not_Report_When_No_DomainException_Is_Thrown()
    {
        const string source = DomainExceptionShim + """
            namespace Sample
            {
                public sealed class Handler
                {
                    public string Handle() => "ok"; // returns a value, throws nothing
                }
            }
            """;

        var diagnostics = await RunAnalyzerAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "LS0001");
        diagnostics.Should().NotContain(d => d.Id == "AD0001");
    }

    private static async Task<ImmutableArray<Diagnostic>> RunAnalyzerAsync(string source)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: "AnalyzerSample",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new DomainExceptionThrowAnalyzer()));

        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }
}
