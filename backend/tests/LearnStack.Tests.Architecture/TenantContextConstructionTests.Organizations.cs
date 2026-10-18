using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class TenantContextConstructionTests
{
    private const string OrganizationValidator = "LearnStack.Infrastructure/MultiTenancy/OrganizationScopeValidator.cs";
    private const string OrganizationHistory = "LearnStack.Infrastructure/Jobs/PostgresBackgroundJobControlStore.RecipientHistory.cs";
    private const string OrganizationCompletion = "LearnStack.Infrastructure/Jobs/PostgresBackgroundJobRecipientCompletion.cs";
    private const string OrganizationOccurrences = "LearnStack.Infrastructure/Jobs/PostgresBackgroundJobControlStore.Occurrences.cs";
    private const string OrganizationPointSql = "SELECT status,deleted_at FROM organizations WHERE tenant_id=@tenant AND id=@organization";
    private const string OrganizationPageSql = """
        SELECT tenant_id,id,status,deleted_at FROM organizations
        WHERE ROW(tenant_id,id)>ROW(@afterTenant,@afterOrganization) AND ROW(tenant_id,id)<=ROW(@upperTenant,@upperOrganization)
        ORDER BY tenant_id,id LIMIT @limit
        """;

    private sealed record OrganizationSqlSubject(string Path, string Type, string Member, string Sql);

    private static readonly OrganizationSqlSubject[] OrganizationSqlSubjects =
    [
        new(OrganizationValidator, "LearnStack.Infrastructure.MultiTenancy.OrganizationScopeValidator", "BelongsToTenantAsync",
            "SELECT 1 FROM organizations WHERE tenant_id = @tenant AND id = @organization AND deleted_at IS NULL"),
        new(OrganizationHistory, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "ResolveLockedRecipientAsync", OrganizationPointSql),
        new(OrganizationCompletion, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobRecipientCompletion", "ReadAndLockAsync", OrganizationPointSql),
        new(OrganizationOccurrences, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "OrganizationLifecyclePageSql", OrganizationPageSql),
        new(OrganizationOccurrences, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "FreezeOccurrenceBoundsAsync",
            "SELECT tenant_id,id FROM organizations ORDER BY tenant_id DESC,id DESC LIMIT 1"),
    ];

    private static (string Path, string Code)[] OrganizationSources() =>
        Directory.EnumerateFiles(SourceScan.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(SourceScan.SourceRoot, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .Select(file => (Path.GetRelativePath(SourceScan.SourceRoot, file).Replace('\\', '/'), File.ReadAllText(file))).ToArray();

    private static string[] OrganizationReadOffenders(IEnumerable<(string Path, string Code)> sources)
    {
        var offenders = new List<string>();
        var found = new List<OrganizationSqlSubject>();
        var roots = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
        foreach (var (path, code) in sources)
        {
            // Parse only possible readers and the required owners. Comments do
            // not grant permission and SQL spelling cannot hide an extra reader.
            if (!OrganizationSqlSubjects.Any(subject => subject.Path == path)
                && !Regex.IsMatch(code, "organizations", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            var root = CSharpSyntaxTree.ParseText(code).GetRoot();
            roots.Add(path, root);
            if (root.ContainsDiagnostics) { offenders.Add(path + ": invalid source"); continue; }
            if (root.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>().Any(value => ReadsOrganizations(value.ToString())))
                offenders.Add(path + ": organization SQL must use its closed literal command shape");
            if (root.DescendantNodes().OfType<BinaryExpressionSyntax>().Any(value =>
                    value.IsKind(SyntaxKind.AddExpression) && OrganizationConstant(value) is { } sql && ReadsOrganizations(sql)))
                offenders.Add(path + ": concatenated organization SQL is outside the closed query shapes");
            foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(literal =>
                         literal.IsKind(SyntaxKind.StringLiteralExpression) && ReadsOrganizations(literal.Token.ValueText)))
            {
                var member = literal.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
                var owner = literal.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                var name = member switch
                {
                    MethodDeclarationSyntax method => method.Identifier.ValueText,
                    FieldDeclarationSyntax => literal.Ancestors().OfType<VariableDeclaratorSyntax>().First().Identifier.ValueText,
                    _ => "<unapproved member>",
                };
                found.Add(new(path, owner is null ? "<missing type>" : OrganizationTypeIdentity(owner), name,
                    NormalizeOrganizationSql(literal.Token.ValueText)));
                if (!OrganizationSqlUseIsExact(path, name, literal))
                    offenders.Add(path + ": the literal must be the exact executed SQL argument or page constant");
            }
        }
        var expected = OrganizationSqlSubjects.Select(subject => subject with { Sql = NormalizeOrganizationSql(subject.Sql) }).ToArray();
        foreach (var group in expected.Concat(found).Distinct())
            if (found.Count(subject => subject == group) != expected.Count(subject => subject == group))
                offenders.Add(group.Path + ": changed/missing/additional organization SQL at " + group.Type + "." + group.Member);

        Require(OrganizationValidator, "OrganizationScopeValidator", "BelongsToTenantAsync",
            "read.Parameters.AddWithValue(\"tenant\", tenantId.Value)",
            "read.Parameters.AddWithValue(\"organization\", organizationId.Value)");
        Require(OrganizationHistory, "PostgresBackgroundJobControlStore", "RecipientHistoryCommand",
            "command.Parameters.AddWithValue(\"tenant\", claim.TenantId.Value)",
            "command.Parameters.AddWithValue(\"organization\", NpgsqlDbType.Uuid, (object?)claim.OrganizationId?.Value ?? DBNull.Value)");
        Require(OrganizationCompletion, "PostgresBackgroundJobRecipientCompletion", "CreateCommand",
            "command.Parameters.AddWithValue(\"tenant\", claim.TenantId.Value)",
            "command.Parameters.AddWithValue(\"organization\", NpgsqlDbType.Uuid, (object?)claim.OrganizationId?.Value ?? DBNull.Value)");
        Require(OrganizationOccurrences, "PostgresBackgroundJobControlStore", "ReadLifecyclePageAsync",
            "command.Parameters.AddWithValue(\"afterTenant\", cursor.AfterTenant ?? Guid.Empty)",
            "command.Parameters.AddWithValue(\"upperTenant\", cursor.UpperTenant.Value)",
            "command.Parameters.AddWithValue(\"afterOrganization\", cursor.AfterOrganization ?? Guid.Empty)",
            "command.Parameters.AddWithValue(\"upperOrganization\", cursor.UpperOrganization!.Value)",
            "command.Parameters.AddWithValue(\"limit\", options.FanoutPageSize)",
            "Command(resources, cursor.Organizations ? OrganizationLifecyclePageSql : TenantLifecyclePageSql)");
        return offenders.Distinct(StringComparer.Ordinal).ToArray();

        void Require(string path, string type, string method, params string[] calls)
        {
            var expectedNamespace = path == OrganizationValidator ? "LearnStack.Infrastructure.MultiTenancy." : "LearnStack.Infrastructure.Jobs.";
            var methods = roots.TryGetValue(path, out var root)
                ? root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(item => item.Identifier.ValueText == method
                    && item.Parent is TypeDeclarationSyntax owner && OrganizationTypeIdentity(owner) == expectedNamespace + type).ToArray() : [];
            if (methods.Length != 1) { offenders.Add(path + ": missing unique binding owner " + method); return; }
            var ownerMethod = methods[0];
            var (parameters, returns, visibility, isAsync) = method switch
            {
                "BelongsToTenantAsync" => ("TenantId tenantId, OrganizationId organizationId, CancellationToken cancellationToken = default", "Task<bool>", SyntaxKind.PublicKeyword, true),
                "RecipientHistoryCommand" => ("BackgroundJobControlResources resources, BackgroundJobRecipientClaim claim, string sql", "NpgsqlCommand", SyntaxKind.PrivateKeyword, false),
                "CreateCommand" => ("string sql", "NpgsqlCommand", SyntaxKind.PrivateKeyword, false),
                "ReadLifecyclePageAsync" => ("BackgroundJobControlResources resources, OccurrenceCursor cursor, CancellationToken token", "Task<IReadOnlyList<BackgroundJobLifecycleRoute>>", SyntaxKind.PrivateKeyword, true),
                _ => throw new InvalidOperationException("Unknown organization binding owner."),
            };
            if (ownerMethod.Body is null || ownerMethod.TypeParameterList is not null
                || !SyntaxFactory.AreEquivalent(ownerMethod.ParameterList, SyntaxFactory.ParseParameterList("(" + parameters + ")"))
                || !SyntaxFactory.AreEquivalent(ownerMethod.ReturnType, SyntaxFactory.ParseTypeName(returns))
                || !ownerMethod.Modifiers.Any(token => token.IsKind(visibility))
                || ownerMethod.Modifiers.Any(token => token.IsKind(SyntaxKind.StaticKeyword))
                || ownerMethod.Modifiers.Any(token => token.IsKind(SyntaxKind.AsyncKeyword)) != isAsync)
            { offenders.Add(path + ": changed binding owner signature " + method); return; }
            var actual = ownerMethod.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
            foreach (var call in calls)
                if (actual.Count(item => SyntaxFactory.AreEquivalent(item, SyntaxFactory.ParseExpression(call))
                        && ExecutedBinding(item, ownerMethod)) != 1)
                    offenders.Add(path + ": missing/changed binding " + method + " " + call);

            var bindings = calls.Select(call => SyntaxFactory.ParseExpression(call)).OfType<InvocationExpressionSyntax>()
                .Where(call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AddWithValue" }).ToArray();
            foreach (var expectedBinding in bindings)
            {
                var receiver = ((MemberAccessExpressionSyntax)expectedBinding.Expression).Expression;
                var key = ((LiteralExpressionSyntax)expectedBinding.ArgumentList.Arguments[0].Expression).Token.ValueText;
                var keyWrites = actual.Where(call => call.Expression is MemberAccessExpressionSyntax access
                    && SyntaxFactory.AreEquivalent(access.Expression, receiver)
                    && call.ArgumentList.Arguments.Count != 0
                    && OrganizationConstant(call.ArgumentList.Arguments[0].Expression) == key).ToArray();
                if (keyWrites.Length != 1) offenders.Add(path + ": extra protected parameter binding " + key);
                foreach (var access in ownerMethod.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                             .Where(access => SyntaxFactory.AreEquivalent(access, receiver)))
                    if (access.Parent is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AddWithValue" } add
                        || add.Parent is not InvocationExpressionSyntax invocation
                        || invocation.ArgumentList.Arguments.Count == 0
                        || invocation.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax)
                        offenders.Add(path + ": parameter collection cannot escape or overwrite a protected binding");
            }
        }
    }

    private static bool ExecutedBinding(InvocationExpressionSyntax call, MethodDeclarationSyntax owner)
    {
        if (call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AddWithValue" }
            && (call.Parent is not ExpressionStatementSyntax expression || !ReferenceEquals(expression.Expression, call)))
            return false; // The returned parameter cannot be captured/rebound or conditionally evaluated.
        if (call.Ancestors().TakeWhile(node => !ReferenceEquals(node, owner))
            .Any(node => node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)) return false;
        var statement = call.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (statement?.Parent == owner.Body) return true;
        // Only these two cursor values are conditional; they are bound on the
        // exact branch that selected the organization page, never a local function.
        return owner.Identifier.ValueText == "ReadLifecyclePageAsync"
            && statement?.Parent is BlockSyntax { Parent: IfStatementSyntax branch } block
            && branch.Parent == owner.Body && ReferenceEquals(branch.Statement, block)
            && SyntaxFactory.AreEquivalent(branch.Condition, SyntaxFactory.ParseExpression("cursor.Organizations"));
    }

    private static string? OrganizationConstant(ExpressionSyntax value) => value switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        ParenthesizedExpressionSyntax parentheses => OrganizationConstant(parentheses.Expression),
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression)
            && OrganizationConstant(binary.Left) is { } left && OrganizationConstant(binary.Right) is { } right => left + right,
        _ => null,
    };

    private static string NormalizeOrganizationSql(string sql) => SourceText.WithoutWhitespace(sql).ToLowerInvariant();

    private static bool ReadsOrganizations(string sql) => Regex.IsMatch(sql,
        "\\b(?:from|join)\\s+(?:\"?public\"?\\s*\\.\\s*)?\"?organizations\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool OrganizationSqlUseIsExact(string path, string member, LiteralExpressionSyntax literal)
    {
        if (path == OrganizationOccurrences && member == "OrganizationLifecyclePageSql")
            return literal.Parent is EqualsValueClauseSyntax;
        var sql = literal.ToString();
        var expression = path switch
        {
            OrganizationValidator => "new NpgsqlCommand(" + sql + ", connection, transaction)",
            OrganizationCompletion => "CreateCommand(" + sql + ")",
            OrganizationHistory => "RecipientHistoryCommand(resources, claim, " + sql + ")",
            OrganizationOccurrences => "Command(resources, cursor.Organizations ? " + sql
                + " : \"SELECT id FROM tenants ORDER BY id DESC LIMIT 1\")",
            _ => null,
        };
        if (expression is null) return false;
        var actual = literal.Ancestors().FirstOrDefault(node => node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax);
        return SyntaxFactory.AreEquivalent(actual, SyntaxFactory.ParseExpression(expression));
    }

    private static string OrganizationTypeIdentity(TypeDeclarationSyntax type) => string.Join(".",
        type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(scope => scope.Name.ToString())
            .Concat(type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse().Select(owner => owner.Identifier.ValueText)));

    [Fact]
    public void Organization_reader_census_rejects_missing_keys_extra_readers_and_unbounded_pages()
    {
        var production = OrganizationSources();
        OrganizationReadOffenders(production).Should().BeEmpty("all five real SQL subjects and their bound keys must exist");
        foreach (var subject in OrganizationSqlSubjects)
        {
            var source = production.Single(file => file.Path == subject.Path);
            var root = CSharpSyntaxTree.ParseText(source.Code).GetRoot();
            var literal = root.DescendantNodes().OfType<LiteralExpressionSyntax>().Single(item =>
                item.IsKind(SyntaxKind.StringLiteralExpression)
                && NormalizeOrganizationSql(item.Token.ValueText) == NormalizeOrganizationSql(subject.Sql));
            var dirtySql = subject.Sql.Contains("WHERE tenant_id", StringComparison.Ordinal)
                ? subject.Sql.Replace("tenant_id = @tenant AND ", "", StringComparison.Ordinal)
                    .Replace("tenant_id=@tenant AND ", "", StringComparison.Ordinal)
                : subject.Sql.Replace("tenant_id", "organization_id", StringComparison.Ordinal);
            Dirty(source.Path, source.Code.Remove(literal.SpanStart, literal.Span.Length)
                .Insert(literal.SpanStart, SyntaxFactory.Literal(dirtySql).ToString()));
            Dirty(source.Path, source.Code.Remove(literal.SpanStart, literal.Span.Length)
                .Insert(literal.SpanStart, SyntaxFactory.Literal("SELECT * FROM organizations WHERE id=@organization").ToString()));
            Dirty(source.Path, source.Code.Insert(literal.Span.End, ".Replace(\"tenant_id=@tenant AND \", \"\")"));
            Dirty(source.Path, source.Code.Replace(subject.Member, "RemovedOrganizationSubject", StringComparison.Ordinal));
            Dirty(source.Path, source.Code + "\ninternal sealed class ExtraOrganizationRead { private string Sql() => \"SELECT * FROM organizations\"; }");
        }
        var page = production.Single(file => file.Path == OrganizationOccurrences);
        foreach (var (from, to) in new[]
                 {
                     ("LIMIT @limit", ""), ("ORDER BY tenant_id,id", "ORDER BY id"),
                     ("ROW(@upperTenant,@upperOrganization)", "ROW(@upperTenant,@afterOrganization)"),
                     ("\"limit\", options.FanoutPageSize", "\"limit\", int.MaxValue"),
                 }) Dirty(page.Path, page.Code.Replace(from, to, StringComparison.Ordinal));
        var added = production.Append(("LearnStack.Infrastructure/ExtraOrganizationRead.cs",
            "internal sealed class Extra { string Sql() => \"select * from public.organizations\"; }")).ToArray();
        OrganizationReadOffenders(added).Should().NotBeEmpty();
        foreach (var query in new[]
                 {
                     "\"SELECT o.status FROM tenants t JOIN organizations o ON o.id=@organization\"",
                     "$\"SELECT status FROM organizations WHERE id=@organization\"",
                     "$\"\"\"SELECT status FROM public.organizations WHERE id=@organization\"\"\"",
                     "\"SELECT status FROM \" + (\"organizations WHERE id=@organization\")",
                 })
            OrganizationReadOffenders(production.Append(("LearnStack.Infrastructure/ExtraOrganizationRead.cs",
                "internal sealed class Extra { string Sql() => " + query + "; }"))).Should().NotBeEmpty();
        var history = production.Single(file => file.Path == OrganizationHistory);
        const string TenantBinding = "command.Parameters.AddWithValue(\"tenant\", claim.TenantId.Value);";
        Dirty(history.Path, history.Code.Replace(TenantBinding,
            "void UnusedBinding() { " + TenantBinding + " } command.Parameters.AddWithValue(\"tenant\", Guid.Empty);", StringComparison.Ordinal));
        Dirty(history.Path, history.Code.Replace(TenantBinding, TenantBinding
            + " command.Parameters[\"tenant\"].Value = Guid.Empty;", StringComparison.Ordinal));
        Dirty(history.Path, history.Code.Replace(TenantBinding,
            "var rebound = command.Parameters.AddWithValue(\"tenant\", claim.TenantId.Value); rebound.Value = Guid.Empty;", StringComparison.Ordinal));
        Dirty(history.Path, history.Code.Replace(TenantBinding, "if (false) { " + TenantBinding + " }", StringComparison.Ordinal));
        Dirty(history.Path, history.Code + "\ninternal sealed class SplitReader { string Sql() => \"SELECT status FROM \" + \"organizations WHERE id=@organization\"; }");
        OrganizationReadOffenders(production.Select(file => (file.Path, "// FROM organizations is a harmless comment\n" + file.Code)))
            .Should().BeEmpty("comments and source formatting cannot create or authorize SQL subjects");
        OrganizationReadOffenders(production).Should().BeEmpty("the actual source is restored after every planted copy");

        void Dirty(string path, string code)
        {
            code.Should().NotBe(production.Single(file => file.Path == path).Code);
            OrganizationReadOffenders(production.Select(file => file.Path == path ? (path, code) : file)).Should().NotBeEmpty(
                "the same whole-production detector must reject the changed actual organization reader");
        }
    }
}
