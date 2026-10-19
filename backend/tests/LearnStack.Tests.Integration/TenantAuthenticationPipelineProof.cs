using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LearnStack.Tests.Integration;

/// <summary>Complements the actual-host behavioral proof with mutations of its real composition source.</summary>
internal static class TenantAuthenticationPipelineProof
{
    private static readonly string[] OrderedCalls =
    [
        "UseLearnStackPublicResponsePolicy", "UseExceptionHandler", "MapLearnStackClientErrors",
        "UseLearnStackCorrelationHeader", "UseRateLimiter", "UseLearnStackVisitorAdmission",
        "UseLearnStackRequestBodyLimit", "UseLearnStackHostClassification", "UseAuthentication",
        "UseLearnStackTenantCredentialAdmission", "UseLearnStackTenantSubjectAdmission",
        "UseLearnStackTenantResolution", "UseLearnStackTenantAssertions", "UseAuthorization",
        "UseLearnStackTenantSurfaceAdmission", "MapControllers", "Run"
    ];

    internal static void AssertProductionOrderAndPlantedReorders()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LearnStack.slnx")))
            directory = directory.Parent;
        var path = Path.Combine(directory?.FullName
            ?? throw new InvalidOperationException("Cannot qualify HTTP composition without its production source."),
            "src", "LearnStack.Api", "Program.cs");
        var production = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetCompilationUnitRoot();
        Calls(production).Should().Equal(OrderedCalls,
            "the actual host must explicitly install the complete ADR-0064 sequence once, with no extra middleware");

        foreach (var (first, second) in new[]
                 {
                     ("UseLearnStackHostClassification", "UseAuthentication"),
                     ("UseAuthentication", "UseLearnStackTenantCredentialAdmission"),
                     ("UseLearnStackTenantCredentialAdmission", "UseLearnStackTenantResolution"),
                     ("UseLearnStackTenantSubjectAdmission", "UseLearnStackTenantResolution"),
                     ("UseLearnStackTenantResolution", "UseLearnStackTenantAssertions"),
                     ("UseLearnStackTenantAssertions", "UseAuthorization"),
                     ("UseLearnStackTenantSurfaceAdmission", "MapControllers")
                 })
        {
            var left = Statement(production, first);
            var right = Statement(production, second);
            var reordered = production.ReplaceNodes([left, right],
                (original, _) => original == left ? right : left);
            Calls(reordered).Should().NotEqual(OrderedCalls,
                "swapping real production registrations {0}/{1} must fail the same order predicate", first, second);
        }

        var authentication = Statement(production, "UseAuthentication");
        Calls(production.RemoveNode(authentication, Microsoft.CodeAnalysis.SyntaxRemoveOptions.KeepNoTrivia)!)
            .Should().NotEqual(OrderedCalls, "missing authentication must not be a vacuous success");
        Calls(production.AddMembers(authentication)).Should().NotEqual(OrderedCalls,
            "a duplicate insertion must not silently activate a second pipeline position");
        Calls(production).Should().Equal(OrderedCalls, "all mutants are immutable copies of the real source");
    }

    private static GlobalStatementSyntax Statement(CompilationUnitSyntax source, string method) =>
        source.Members.OfType<GlobalStatementSyntax>().Single(statement => statement.DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Any(call => CallName(call) == method));

    private static IEnumerable<string> Calls(CompilationUnitSyntax source) => source.Members
        .OfType<GlobalStatementSyntax>().SelectMany(statement => statement.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()).Select(CallName).OfType<string>()
        .Where(name => name.StartsWith("Use", StringComparison.Ordinal)
            || name is "MapLearnStackClientErrors" or "MapControllers" or "Run");

    private static string? CallName(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax
    { Expression: IdentifierNameSyntax { Identifier.ValueText: "app" } } member
            ? member.Name.Identifier.ValueText : null;
}
