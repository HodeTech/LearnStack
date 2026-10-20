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
        var helpers = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.cs", SearchOption.AllDirectories)
            .Where(file => file != path && !Path.GetRelativePath(Path.GetDirectoryName(path)!, file)
                .Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetCompilationUnitRoot()).ToArray();
        Inspect(production).Should().Equal(OrderedCalls,
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
            Inspect(reordered).Should().NotEqual(OrderedCalls,
                "swapping real production registrations {0}/{1} must fail the same order predicate", first, second);
        }

        var authentication = Statement(production, "UseAuthentication");
        Inspect(production.RemoveNode(authentication, Microsoft.CodeAnalysis.SyntaxRemoveOptions.KeepNoTrivia)!)
            .Should().NotEqual(OrderedCalls, "missing authentication must not be a vacuous success");
        Inspect(production.AddMembers(authentication)).Should().NotEqual(OrderedCalls,
            "a duplicate insertion must not silently activate a second pipeline position");
        var helperCall = SyntaxFactory.GlobalStatement(SyntaxFactory.ParseStatement("app.ConfigureCensusPipeline();"));
        var wrapped = production.InsertNodesBefore(authentication, [helperCall]);
        var cleanHelper = CSharpSyntaxTree.ParseText("""
            internal static class CensusPipeline
            {
                internal static void ConfigureCensusPipeline(this WebApplication pipeline) { }
            }
            """).GetCompilationUnitRoot();
        Calls(wrapped, helpers.Append(cleanHelper)).Should().Equal(OrderedCalls,
            "a reachable helper without middleware is a clean control");
        var dirtyHelper = CSharpSyntaxTree.ParseText("""
            internal static class CensusPipeline
            {
                internal static void ConfigureCensusPipeline(this WebApplication pipeline)
                    => CensusNested.Configure(pipeline);
            }
            internal static class CensusNested
            {
                internal static void Configure(WebApplication application) => application.UseAuthentication();
            }
            """).GetCompilationUnitRoot();
        Calls(wrapped, helpers.Append(dirtyHelper)).Should().NotEqual(OrderedCalls,
            "an extra middleware in reachable extension/static helpers must alter the same production sequence");
        Calls(wrapped, helpers).Should().NotEqual(OrderedCalls,
            "an unresolved application helper cannot establish that composition is unchanged");
        Inspect(production).Should().Equal(OrderedCalls, "all mutants are immutable copies of the real source");

        IEnumerable<string> Inspect(CompilationUnitSyntax source) => Calls(source, helpers);
    }

    private static GlobalStatementSyntax Statement(CompilationUnitSyntax source, string method) =>
        source.Members.OfType<GlobalStatementSyntax>().Single(statement => statement.DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Any(call => CallName(call) == method));

    private static IEnumerable<string> Calls(CompilationUnitSyntax source, IEnumerable<CompilationUnitSyntax> helpers)
    {
        var methods = helpers.Prepend(source).SelectMany(root => root.DescendantNodes().OfType<MethodDeclarationSyntax>()).ToArray();
        return source.Members.OfType<GlobalStatementSyntax>().SelectMany(statement => Expand(statement, "app", []));

        IEnumerable<string> Expand(SyntaxNode body, string receiver, HashSet<MethodDeclarationSyntax> path)
        {
            foreach (var call in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var member = call.Expression as MemberAccessExpressionSyntax;
                var instance = member?.Expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == receiver;
                var appArgument = call.ArgumentList.Arguments.Select((argument, index) => (argument, index))
                    .Where(pair => pair.argument.Expression is IdentifierNameSyntax name && name.Identifier.ValueText == receiver)
                    .Select(pair => (int?)pair.index).FirstOrDefault();
                if (!instance && appArgument is null) continue;
                var name = member?.Name.Identifier.ValueText ?? (call.Expression as IdentifierNameSyntax)?.Identifier.ValueText;
                if (name is null) { yield return "Unverifiable application composition"; continue; }
                if (name.StartsWith("Use", StringComparison.Ordinal) || name is "MapLearnStackClientErrors" or "MapControllers" or "Run")
                {
                    yield return name;
                    continue;
                }
                // Endpoint composition is checked by the independent complete IL
                // census. These existing mappings are not middleware positions.
                if (instance && name is "MapGet" or "MapLearnStackOpenApi") continue;
                var candidates = methods.Where(method => method.Identifier.ValueText == name
                    && method.ParameterList.Parameters.Count > (instance ? 0 : appArgument!.Value)
                    && (!instance || method.ParameterList.Parameters[0].Modifiers.Any(SyntaxKind.ThisKeyword))).ToArray();
                if (candidates.Length != 1 || path.Contains(candidates[0]))
                {
                    yield return "Unverifiable application helper: " + name;
                    continue;
                }
                var helper = candidates[0];
                var parameter = helper.ParameterList.Parameters[instance ? 0 : appArgument!.Value].Identifier.ValueText;
                foreach (var registration in Expand(helper, parameter, new HashSet<MethodDeclarationSyntax>(path) { helper }))
                    yield return registration;
            }
        }
    }

    private static string? CallName(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax
    { Expression: IdentifierNameSyntax { Identifier.ValueText: "app" } } member
            ? member.Name.Identifier.ValueText : null;
}
