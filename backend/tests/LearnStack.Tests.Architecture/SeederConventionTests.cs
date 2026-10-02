using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>P02d-2 G15 caller fence and G20(a) literal-source proof, not the later branch guard.</summary>
public sealed class SeederConventionTests
{
    [Fact]
    public void Seeder_Does_Not_Call_Tenant_Context_Setters()
    {
        var files = Directory.GetFiles(Path.Combine(RepositoryPaths.BackendSrc(), "LearnStack.Tools.Seeder"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj")).ToArray();
        files.Should().NotBeEmpty("the caller fence must scan the production seeder; Fix: restore its source discovery");
        files.Select(Path.GetFileName).Should().Contain("SeedRunner.cs").And.Contain("SeedComposition.cs");
        files.SelectMany(path => ForbiddenReferences(File.ReadAllText(path)).Select(reference => $"{Path.GetFileName(path)}: {reference}"))
            .Should().BeEmpty("seed verification must use contextual ISender queries; Fix: remove private announcements/transactions and direct setter references");
    }

    [Fact]
    public void Seeder_Caller_Fence_Catches_Calls_Method_Groups_And_Sql_But_Admits_Dispatch()
    {
        foreach (var statement in new[]
        {
            "await unit.SetTenantContextAsync(context);", "var announce = unit.SetTenantContextAsync;",
            "await connection.BeginTransactionAsync();", "var begin = connection.BeginTransaction;",
            "accessor.Current = context;", "var sql = \"SELECT set_config('app.tenant_id', @tenant, true)\";",
            "var sql = \"SET LOCAL app.organization_id = 'x'\";",
            "var sql = $\"SELECT set_config('app.scope', '{scope}', true)\";",
            "var sql = \"SELECT set_config(\" + \"'app.resolving_host', @host, true)\";",
        })
            ForbiddenReferences($"class Probe {{ void Run() {{ {statement} }} }}").Should().NotBeEmpty($"the planted caller must fail: {statement}; Fix: keep method groups and composed SQL visible");
        ForbiddenReferences("""
            class Probe {
              void Run() {
                // unit.SetTenantContextAsync(context); accessor.Current = context;
                var text = "SetTenantContextAsync";
                var context = new SeedTenantContext(tenant, null);
                var accessor = new StaticTenantContextAccessor(context);
                sender.Send(new GetTenantSeedStateQuery());
              }
            }
            """).Should().BeEmpty("comments/plain diagnostics and trusted construction/dispatch do not announce database authority");
    }

    [Fact]
    public void Seed_Literal_Source_Is_Complete_And_Readable()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryPaths.BackendSrc(), "LearnStack.Tools.Seeder", "SeedData.cs"));
        var literals = SeedLiterals(source);
        literals.Should().NotBeEmpty("the later genericity guard needs an actual declaration, not a second identity list");
        SeedData.All.Should().NotBeEmpty();
        SeedData.All.Should().OnlyContain(tenant => tenant.Curriculum != null);
        using var declared = JsonDocument.Parse(JsonSerializer.Serialize(SeedData.All));
        var values = StringValues(declared.RootElement).ToHashSet(StringComparer.Ordinal);
        values.Should().NotBeEmpty();
        literals.Should().Contain(values, "every declared identity/body/label is read from SeedData itself; Fix: expand the literal reader rather than copy missing demo literals");
        SeedLiterals("""class SeedData { const string Host = "new-showcase.invalid"; const string Body = "{\"name\":\"A fresh label\"}"; }""")
            .Should().Contain("new-showcase.invalid").And.Contain("A fresh label");
        foreach (var malformed in new[] { "class Different {}", "class SeedData {}", "class SeedData { const string Broken =" })
        {
            var read = () => SeedLiterals(malformed);
            read.Should().Throw<InvalidOperationException>("unreadable/missing/empty declaration must fail closed");
        }
    }

    internal static HashSet<string> SeedLiterals(string source)
    {
        var root = Parse(source);
        var declarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(type => type.Identifier.ValueText == "SeedData").ToArray();
        if (declarations.Length != 1) throw new InvalidOperationException("Expected exactly one SeedData declaration.");
        var result = declarations[0].DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(literal => literal.Token.ValueText).Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (result.Count == 0) throw new InvalidOperationException("SeedData contains no readable literals.");
        foreach (var literal in result.ToArray())
        {
            try
            {
                using var json = JsonDocument.Parse(literal);
                result.UnionWith(StringValues(json.RootElement));
            }
            catch (JsonException) { /* Ordinary non-JSON literals remain in the result. */ }
        }
        return result;
    }

    private static IEnumerable<string> StringValues(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString() ?? string.Empty],
        JsonValueKind.Array => element.EnumerateArray().SelectMany(StringValues),
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => StringValues(property.Value)),
        _ => [],
    };

    private static SyntaxNode Parse(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException("Cannot scan malformed seeder source.");
        return tree.GetRoot();
    }

    private static IEnumerable<string> ForbiddenReferences(string source)
    {
        var root = Parse(source);
        foreach (var name in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            if (name.Identifier.ValueText is "SetTenantContextAsync" or "BeginTransactionAsync" or "BeginTransaction")
                yield return name.Identifier.ValueText;
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            if (assignment.Left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Current" })
                yield return "ambient accessor assignment";
        foreach (var expression in root.DescendantNodes().OfType<ExpressionSyntax>())
        {
            var text = ConstantText(expression);
            if (text is not null && (Regex.IsMatch(text, @"set_config\s*\(\s*['""\s]*app\.", RegexOptions.IgnoreCase)
                || Regex.IsMatch(text, @"\bSET\s+(?:LOCAL\s+|SESSION\s+)?app\.", RegexOptions.IgnoreCase)))
                yield return "database context SQL";
        }
    }
    private static string? ConstantText(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        InterpolatedStringExpressionSyntax interpolation => string.Concat(interpolation.Contents.OfType<InterpolatedStringTextSyntax>().Select(text => text.TextToken.ValueText)),
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression)
            && ConstantText(binary.Left) is { } left && ConstantText(binary.Right) is { } right => left + right,
        _ => null,
    };
}
