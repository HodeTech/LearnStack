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

    // Keep the public composition markers above, but inspect the real bodies
    // behind each marker. A familiar UseLearnStack name is not a leaf exception.
    private static readonly Dictionary<string, string[]> HelperLeaves = new(StringComparer.Ordinal)
    {
        ["MapLearnStackOpenApi"] = [],
        ["UseLearnStackPublicResponsePolicy"] = ["Use"],
        ["MapLearnStackClientErrors"] = ["UseStatusCodePages"],
        ["UseLearnStackCorrelationHeader"] = ["UseMiddleware<CorrelationHeaderMiddleware>"],
        ["UseLearnStackVisitorAdmission"] = ["Use"],
        ["UseLearnStackRequestBodyLimit"] = ["Use"],
        ["UseLearnStackHostClassification"] = ["UseMiddleware<HostClassificationMiddleware>"],
        ["UseLearnStackTenantCredentialAdmission"] = ["UseMiddleware<TenantCredentialAdmissionMiddleware>"],
        ["UseLearnStackTenantSubjectAdmission"] = ["UseMiddleware<TenantSubjectAdmissionMiddleware>"],
        ["UseLearnStackTenantResolution"] = ["UseMiddleware<TenantResolverMiddleware>"],
        ["UseLearnStackTenantAssertions"] = ["UseMiddleware<TenantAssertionMiddleware>"],
        ["UseLearnStackTenantSurfaceAdmission"] = ["UseMiddleware<TenantSurfaceAdmissionMiddleware>"]
    };

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
        var useCall = SyntaxFactory.GlobalStatement(SyntaxFactory.ParseStatement("app.UseCensusPipeline();"));
        var useWrapped = production.InsertNodesBefore(authentication, [useCall]);
        var cleanUseHelper = CSharpSyntaxTree.ParseText("""
            internal static class CensusPipeline
            {
                internal static void UseCensusPipeline(this WebApplication pipeline) { }
            }
            """).GetCompilationUnitRoot();
        Calls(useWrapped, helpers.Append(cleanUseHelper)).Should().Equal(OrderedCalls,
            "a Use prefix does not turn a data-free custom helper into framework middleware");
        var dirtyUseHelper = CSharpSyntaxTree.ParseText("""
            internal static class CensusPipeline
            {
                internal static void UseCensusPipeline(this WebApplication pipeline) => pipeline.UseAuthentication();
            }
            """).GetCompilationUnitRoot();
        Calls(useWrapped, helpers.Append(dirtyUseHelper)).Count(name => name == "UseAuthentication").Should().Be(2,
            "a Use-prefixed custom helper must expose its actual additional framework registration");

        // Mutate the existing real helper, rather than proving only that a
        // previously unknown helper would be traversed.
        foreach (var name in new[] { "UseLearnStackTenantResolution", "MapLearnStackOpenApi" })
        {
            var owner = helpers.Single(root => root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Any(method => method.Identifier.ValueText == name));
            var helper = owner.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.ValueText == name);
            var receiver = helper.ParameterList.Parameters[0].Identifier.ValueText;
            foreach (var alias in new[] { false, true })
            {
                var statements = alias
                    ? new[] { SyntaxFactory.ParseStatement("var alias = " + receiver + ";"), SyntaxFactory.ParseStatement("alias.UseAuthentication();") }
                    : new[] { SyntaxFactory.ParseStatement(receiver + ".UseAuthentication();") };
                var changed = helper.WithBody(helper.Body!.WithStatements(helper.Body.Statements.InsertRange(1, statements)));
                var changedHelpers = helpers.Select(root => root == owner ? root.ReplaceNode(helper, changed) : root);
                var failures = Calls(production, changedHelpers).ToArray();
                failures.Should().Contain(value => value.StartsWith("Unexpected helper middleware: " + name + ":", StringComparison.Ordinal),
                    "adding authentication inside the existing helper must invalidate its real middleware leaf shape: " + name);
                if (alias)
                    failures.Should().Contain(value => value.Contains("Unverifiable application alias:", StringComparison.Ordinal),
                        "a copied application-builder receiver must fail closed instead of hiding its middleware call");
            }
        }

        Inspect(production).Should().Equal(OrderedCalls, "all mutants are immutable copies of the real source");

        IEnumerable<string> Inspect(CompilationUnitSyntax source) => Calls(source, helpers);
    }

    private static GlobalStatementSyntax Statement(CompilationUnitSyntax source, string method) =>
        source.Members.OfType<GlobalStatementSyntax>().Single(statement => statement.DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Any(call => CallName(call) == method));

    private static IEnumerable<string> Calls(CompilationUnitSyntax source, IEnumerable<CompilationUnitSyntax> helpers)
    {
        var methods = helpers.Prepend(source).SelectMany(root => root.DescendantNodes().OfType<MethodDeclarationSyntax>()).ToArray();
        return source.Members.OfType<GlobalStatementSyntax>().SelectMany(statement => Expand(statement, "app", [], markers: true));

        IEnumerable<string> Expand(SyntaxNode body, string receiver, HashSet<MethodDeclarationSyntax> path, bool markers = false)
        {
            // This bounded source proof handles direct receivers and arguments.
            // Copying that receiver is deliberately unverifiable, so an alias
            // cannot silently hide registrations from the inspected sequence.
            foreach (var alias in body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                         .Where(variable => variable.Initializer is { } initializer && IsReceiver(initializer.Value, receiver)))
                yield return "Unverifiable application alias: " + receiver + " -> " + alias.Identifier.ValueText;
            foreach (var alias in body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                         .Where(assignment => IsReceiver(assignment.Right, receiver)))
                yield return "Unverifiable application alias: " + receiver + " -> " + alias.Left;

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
                if (member?.Expression is IdentifierNameSyntax { Identifier.ValueText: "ArgumentNullException" }
                    && name == "ThrowIfNull") continue;
                var candidates = methods.Where(method => method.Identifier.ValueText == name
                    && method.ParameterList.Parameters.Count > (instance ? 0 : appArgument!.Value)
                    && (!instance || method.ParameterList.Parameters[0].Modifiers.Any(SyntaxKind.ThisKeyword))).ToArray();
                // Only framework endpoint leaves are outside this middleware
                // sequence. Our OpenAPI wrapper still has its real body inspected.
                if (candidates.Length == 0 && name is "MapGet" or "MapOpenApi" or "MapScalarApiReference") continue;
                // Resolve source-owned helpers before admitting framework leaves,
                // even if a helper is named UseAuthentication or starts with Use.
                if (candidates.Length == 0 && name is "Use" or "UseMiddleware" or "UseExceptionHandler"
                    or "UseStatusCodePages" or "UseRateLimiter" or "UseAuthentication" or "UseAuthorization" or "MapControllers" or "Run")
                {
                    yield return member?.Name.ToString() ?? name;
                    continue;
                }
                if (candidates.Length != 1 || path.Contains(candidates[0]))
                {
                    yield return "Unverifiable application helper: " + name;
                    continue;
                }
                var helper = candidates[0];
                var parameter = helper.ParameterList.Parameters[instance ? 0 : appArgument!.Value].Identifier.ValueText;
                var registrations = Expand(helper, parameter, new HashSet<MethodDeclarationSyntax>(path) { helper }).ToArray();
                if (markers && HelperLeaves.TryGetValue(name, out var expected))
                {
                    if (!registrations.SequenceEqual(expected, StringComparer.Ordinal))
                        yield return "Unexpected helper middleware: " + name + ": " + string.Join(", ", registrations);
                    else if (expected.Length != 0) yield return name;
                }
                else
                    foreach (var registration in registrations) yield return registration;
            }
        }
    }

    private static bool IsReceiver(ExpressionSyntax expression, string receiver) => expression switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText == receiver,
        ParenthesizedExpressionSyntax parenthesized => IsReceiver(parenthesized.Expression, receiver),
        CastExpressionSyntax cast => IsReceiver(cast.Expression, receiver),
        PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression) => IsReceiver(postfix.Operand, receiver),
        _ => false
    };

    private static string? CallName(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax
    { Expression: IdentifierNameSyntax { Identifier.ValueText: "app" } } member
            ? member.Name.Identifier.ValueText : null;
}
