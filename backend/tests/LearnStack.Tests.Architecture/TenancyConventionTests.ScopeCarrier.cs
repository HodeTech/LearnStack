using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LearnStack.Tests.Architecture;

public sealed partial class TenancyConventionTests
{
    private const string ScopeCarrierFile = "LearnStack.Infrastructure/Persistence/NpgsqlUnitOfWork.cs";
    private const string ScopeCarrierSql = "SELECT set_config('app.tenant_id', @tenant, true), "
        + "set_config('app.organization_id', @organization, true), set_config('app.scope', @scope, true), "
        + "set_config('app.identity_authority_id', '', true), set_config('app.identity_subject_key', '', true)";

    private static string WithoutOperationReadAnnouncement(string relative, string source)
    {
        if (relative != ScopeCarrierFile) return source;
        var syntax = CSharpSyntaxTree.ParseText(source).GetRoot();
        var owners = syntax.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(type =>
            type.Identifier.ValueText == "NpgsqlUnitOfWork"
            && string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(item => item.Name.ToString()))
                == "LearnStack.Infrastructure.Persistence").ToArray();
        if (syntax.ContainsDiagnostics || owners.Length != 1) return source;
        var methods = owners[0].Members.OfType<MethodDeclarationSyntax>().ToArray();
        var core = methods.Where(method => method.Identifier.ValueText == "SetTenantContextCoreAsync").ToArray();
        var forwarders = methods.Where(method => method.Identifier.ValueText == "SetTenantContextAsync").ToArray();
        if (core.Length != 1 || !core[0].Modifiers.Any(SyntaxKind.PrivateKeyword)
            || !core[0].Modifiers.Any(SyntaxKind.AsyncKeyword) || core[0].Modifiers.Any(SyntaxKind.StaticKeyword)
            || !SyntaxFactory.AreEquivalent(core[0].ParameterList, SyntaxFactory.ParseParameterList(
                "(ITenantContext context, OperationReadWitness? readWitness, bool tenantWideRead, CancellationToken cancellationToken)"))
            || forwarders.Length != 2
            || forwarders.Count(method => Forwarder(method,
                "(ITenantContext context, CancellationToken cancellationToken = default)",
                "SetTenantContextCoreAsync(context, null, tenantWideRead: false, cancellationToken)")) != 1
            || forwarders.Count(method => Forwarder(method,
                "(ITenantContext context, OperationReadWitness readWitness, CancellationToken cancellationToken = default)",
                "SetTenantContextCoreAsync(context, readWitness, tenantWideRead: true, cancellationToken)")) != 1)
            return source;
        var calls = core[0].DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression is IdentifierNameSyntax { Identifier.ValueText: "ExecuteAsync" }
                && call.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } sql
                && ConstantScopeSql(sql) is { } value && ScopeSetter().IsMatch(value)).ToArray();
        if (calls.Length != 1 || ConstantScopeSql(calls[0].ArgumentList.Arguments[0].Expression) != ScopeCarrierSql)
            return source;
        var expected = (InvocationExpressionSyntax)SyntaxFactory.ParseExpression(
            "ExecuteAsync(cancellationToken, (\"tenant\", tenant), (\"organization\", organization), (\"scope\", tenantWideRead ? \"tenant\" : string.Empty))");
        var arguments = calls[0].ArgumentList.Arguments.Skip(1).ToArray();
        if (arguments.Length != expected.ArgumentList.Arguments.Count || arguments.Where((argument, index) =>
                !SyntaxFactory.AreEquivalent(argument, expected.ArgumentList.Arguments[index])).Any()) return source;
        var allowedSql = calls[0].ArgumentList.Arguments[0].Expression;
        return source.Remove(allowedSql.SpanStart, allowedSql.Span.Length).Insert(allowedSql.SpanStart, "string.Empty");

        static bool Forwarder(MethodDeclarationSyntax method, string parameters, string expression) =>
            method.Modifiers.Any(SyntaxKind.PublicKeyword) && !method.Modifiers.Any(SyntaxKind.StaticKeyword)
            && method.Body is null && method.ExpressionBody is { } body
            && SyntaxFactory.AreEquivalent(method.ParameterList, SyntaxFactory.ParseParameterList(parameters))
            && SyntaxFactory.AreEquivalent(body.Expression, SyntaxFactory.ParseExpression(expression));
    }

    private static string? ConstantScopeSql(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression)
            && ConstantScopeSql(binary.Left) is { } left && ConstantScopeSql(binary.Right) is { } right => left + right,
        ParenthesizedExpressionSyntax parenthesized => ConstantScopeSql(parenthesized.Expression),
        _ => null,
    };

    private static void AssertOperationScopeCarrierControls()
    {
        var source = SourceText.WithoutComments(File.ReadAllText(Path.Combine(SourceScan.SourceRoot, ScopeCarrierFile)));
        string Scan(string path, string code) => WithoutOperationReadAnnouncement(path, WithoutGlobalAuthorityClears(path, code, "app.scope"));
        ScopeSetter().IsMatch(Scan(ScopeCarrierFile, source)).Should().BeFalse("the reviewed parameterized core is the one admitted carrier");
        foreach (var dirty in new[]
        {
            source.Replace("tenantWideRead: false", "tenantWideRead: true", StringComparison.Ordinal),
            source.Replace("(\"scope\", tenantWideRead ? \"tenant\" : string.Empty)", "(\"scope\", context.CorrelationId)", StringComparison.Ordinal),
            source.Replace("set_config('app.scope', @scope, true)", "set_config('app.scope', 'tenant', true)", StringComparison.Ordinal),
            source.Replace("SetTenantContextCoreAsync", "UnreviewedScopeCoreAsync", StringComparison.Ordinal),
            source + "\ninternal sealed class AnotherScopeWriter { void Run() { Execute(\"SET LOCAL app.scope = 'tenant'\"); } }",
        })
        {
            dirty.Should().NotBe(source, "each dirty control changes an actual reviewed carrier statement");
            ScopeSetter().IsMatch(Scan(ScopeCarrierFile, dirty)).Should().BeTrue("no whole-file scope-setter exemption is admitted");
        }
        ScopeSetter().IsMatch(Scan("Unlisted.cs", source)).Should().BeTrue("copying the carrier into another owner remains forbidden");
    }
}
