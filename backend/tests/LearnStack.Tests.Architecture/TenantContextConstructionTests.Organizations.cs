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
    private const string OrganizationIdentityLifecycle = "Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/Persistence/IdentityScopeLifecycleReader.cs";
    private const string OrganizationSelfDiscovery = "Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/Persistence/PreContextSelfDiscoveryScopeReader.cs";
    private const string OrganizationOriginDdl = "Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/Persistence/SeedAdministrationOriginDdl.cs";
    private static readonly string[] OrganizationOriginInstallers = ["Install", "InstallQualifiedCompletion"];
    private const string OrganizationOriginCompositeSelect = """
        SELECT 1 FROM public.organizations AS organization
        WHERE organization.tenant_id=OLD.id AND organization.id=OLD.default_organization_id
            AND organization.status='Active' AND organization.deleted_at IS NULL AND organization.deleted_by IS NULL
        """;
    private const string OrganizationOriginDeclarations = """
        var declarations = string.Join(",", SeedAdministrationOriginManifest.All.Select(declaration =>
            $"('{declaration.TenantId.Value:D}'::uuid,'{declaration.TenantSlug}','{declaration.Host}',"
            + $"{IdentityAdvisoryLockKey.ForTenant(declaration.TenantId).ToString(CultureInfo.InvariantCulture)}::bigint)"));
        """;
    private const string OrganizationPointSql = "SELECT status,deleted_at FROM organizations WHERE tenant_id=@tenant AND id=@organization";
    private const string OrganizationPageSql = """
        SELECT tenant_id,id,status,deleted_at FROM organizations
        WHERE ROW(tenant_id,id)>ROW(@afterTenant,@afterOrganization) AND ROW(tenant_id,id)<=ROW(@upperTenant,@upperOrganization)
        ORDER BY tenant_id,id LIMIT @limit
        """;
    private const string OrganizationIdentityLifecycleSql = """
        SELECT t.id IS NOT NULL AND t.deleted_at IS NULL,
               t.id IS NOT NULL AND t.deleted_at IS NULL AND t.status IN ('Trial', 'Active'),
               o.id IS NOT NULL,
               o.id IS NOT NULL AND o.deleted_at IS NULL AND o.status = 'Active',
               current_setting('transaction_isolation') = 'read committed',
               current_setting('transaction_read_only') = 'on',
               NULLIF(current_setting('app.tenant_id', true), '')::uuid = @tenant
                 AND NULLIF(current_setting('app.organization_id', true), '')::uuid IS NOT DISTINCT FROM @organization
                 AND NULLIF(current_setting('app.scope', true), '') IS NULL
        FROM (VALUES (1)) AS query_anchor(value)
        LEFT JOIN public.tenants AS t ON t.id = @tenant
        LEFT JOIN public.organizations AS o
          ON o.tenant_id = t.id AND o.id = @organization
        """;
    private const string OrganizationSelfDiscoverySql = """
        SELECT t.display_name,o.display_name,h.host,
            current_user='learnstack_app'
                AND current_setting('transaction_read_only')='on'
                AND current_setting('transaction_isolation')='read committed'
                AND NULLIF(current_setting('app.tenant_id',true),'')::uuid=@tenant
                AND NULLIF(current_setting('app.organization_id',true),'')::uuid IS NOT DISTINCT FROM @organization
                AND NULLIF(current_setting('app.scope',true),'') IS NULL
                AND NULLIF(current_setting('app.resolving_host',true),'') IS NULL
                AND NULLIF(current_setting('app.identity_authority_id',true),'') IS NULL
                AND NULLIF(current_setting('app.identity_subject_key',true),'') IS NULL
        FROM public.tenants t
        LEFT JOIN public.organizations o ON o.tenant_id=t.id AND o.id=@organization
        JOIN public.platform_host_to_tenant h ON h.tenant_id=t.id AND h.is_active
            AND (h.organization_id IS NULL OR h.organization_id=@organization)
        WHERE t.id=@tenant AND t.deleted_at IS NULL AND t.status IN ('Trial','Active')
            AND (@organization IS NULL OR (o.id IS NOT NULL AND o.deleted_at IS NULL AND o.status='Active'))
        ORDER BY (h.organization_id IS NOT NULL) DESC,h.host
        LIMIT 1
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
        new(OrganizationIdentityLifecycle, "LearnStack.Modules.Tenancy.Infrastructure.Persistence.IdentityScopeLifecycleReader",
            "LifecycleSql", OrganizationIdentityLifecycleSql),
        new(OrganizationSelfDiscovery, "LearnStack.Modules.Tenancy.Infrastructure.Persistence.PreContextSelfDiscoveryScopeReader",
            "LabelsSql", OrganizationSelfDiscoverySql),
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
        var foundInstallers = new List<string>();
        var roots = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
        foreach (var (path, code) in sources)
        {
            // Parse only possible readers and the required owners. Comments do
            // not grant permission and SQL spelling cannot hide an extra reader.
            if (path != OrganizationOriginDdl && !OrganizationSqlSubjects.Any(subject => subject.Path == path)
                && !Regex.IsMatch(code, "organizations", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            var root = CSharpSyntaxTree.ParseText(code).GetRoot();
            roots.Add(path, root);
            if (root.ContainsDiagnostics) { offenders.Add(path + ": invalid source"); continue; }
            var migrationArguments = OrganizationMigrationSqlArguments(path, root, foundInstallers);
            if (root.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>().Any(value =>
                    ReadsOrganizations(value.ToString()) && !migrationArguments.Contains(value.SpanStart)))
                offenders.Add(path + ": organization SQL must use its closed executed command shape");
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
        foreach (var installer in OrganizationOriginInstallers)
            if (foundInstallers.Count(foundInstaller => foundInstaller == installer) != 1)
                offenders.Add(OrganizationOriginDdl + ": missing/changed exact migration SQL owner " + installer);
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
        Require(OrganizationIdentityLifecycle, "IdentityScopeLifecycleReader", "ReadAsync",
            "command.Parameters.AddWithValue(\"tenant\", tenantId.Value)",
            "command.Parameters.AddWithValue(\"organization\", NpgsqlDbType.Uuid, organizationId is { } org ? org.Value : DBNull.Value)",
            "command.ExecuteReaderAsync(cancellationToken)");
        Require(OrganizationIdentityLifecycle, "IdentityScopeLifecycleReader", "ReadAuthorizationAsync",
            "command.Parameters.AddWithValue(\"tenant\", candidate.TenantId.Value)",
            "command.Parameters.AddWithValue(\"organization\", NpgsqlDbType.Uuid, candidate.OrganizationId is { } organization ? organization.Value : DBNull.Value)",
            "command.ExecuteReaderAsync(cancellationToken)");
        Require(OrganizationSelfDiscovery, "PreContextSelfDiscoveryScopeReader", "ReadAsync",
            "deadline.CancelAfter(TimeSpan.FromSeconds(5))",
            "self.BeginSelfDiscoveryTransactionAsync(witness, deadline.Token)",
            "command.Parameters.AddWithValue(\"tenant\", witness.TenantId.Value)",
            "command.Parameters.AddWithValue(\"organization\", NpgsqlDbType.Uuid, witness.OrganizationId is { } org ? org.Value : DBNull.Value)",
            "command.ExecuteReaderAsync(deadline.Token)",
            "self.IsSelfDiscoveryOwner(witness)",
            "frame.CompleteAsync(deadline.Token)");
        if (roots.TryGetValue(OrganizationIdentityLifecycle, out var lifecycleRoot)
            && lifecycleRoot.DescendantTokens().Count(token => token.IsKind(SyntaxKind.IdentifierToken)
                && token.ValueText == "LifecycleSql") != 3)
            offenders.Add(OrganizationIdentityLifecycle + ": the SQL constant must have exactly its declaration and two executed argument uses");
        if (roots.TryGetValue(OrganizationSelfDiscovery, out var selfRoot)
            && selfRoot.DescendantTokens().Count(token => token.IsKind(SyntaxKind.IdentifierToken)
                && token.ValueText == "LabelsSql") != 2)
            offenders.Add(OrganizationSelfDiscovery + ": the SQL constant must have exactly its declaration and one executed argument use");
        return offenders.Distinct(StringComparer.Ordinal).ToArray();

        void Require(string path, string type, string method, params string[] calls)
        {
            var expectedNamespace = path switch
            {
                OrganizationValidator => "LearnStack.Infrastructure.MultiTenancy.",
                OrganizationIdentityLifecycle or OrganizationSelfDiscovery => "LearnStack.Modules.Tenancy.Infrastructure.Persistence.",
                _ => "LearnStack.Infrastructure.Jobs.",
            };
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
                "ReadAsync" when path == OrganizationSelfDiscovery => ("OwnMembershipScopeWitness witness, CancellationToken cancellationToken = default", "Task<OwnMembershipScopeLabel?>", SyntaxKind.PublicKeyword, true),
                "ReadAsync" => ("TenantId tenantId, OrganizationId? organizationId, CancellationToken cancellationToken = default", "Task<IdentityScopeLifecycle>", SyntaxKind.PublicKeyword, true),
                "ReadAuthorizationAsync" => ("IUnitOfWork child, AuthorizationCandidate candidate, CancellationToken cancellationToken = default", "Task<IdentityScopeLifecycle>", SyntaxKind.PublicKeyword, true),
                _ => throw new InvalidOperationException("Unknown organization binding owner."),
            };
            if (ownerMethod.Body is null || ownerMethod.TypeParameterList is not null
                || !SyntaxFactory.AreEquivalent(ownerMethod.ParameterList, SyntaxFactory.ParseParameterList("(" + parameters + ")"))
                || !SyntaxFactory.AreEquivalent(ownerMethod.ReturnType, SyntaxFactory.ParseTypeName(returns))
                || !ownerMethod.Modifiers.Any(token => token.IsKind(visibility))
                || ownerMethod.Modifiers.Any(token => token.IsKind(SyntaxKind.StaticKeyword))
                || ownerMethod.Modifiers.Any(token => token.IsKind(SyntaxKind.AsyncKeyword)) != isAsync)
            { offenders.Add(path + ": changed binding owner signature " + method); return; }
            if (path == OrganizationIdentityLifecycle)
            {
                var construction = method == "ReadAsync"
                    ? "new NpgsqlCommand(LifecycleSql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)transaction) { CommandTimeout = 10 }"
                    : "new NpgsqlCommand(LifecycleSql, (NpgsqlConnection)child.Connection, (NpgsqlTransaction)transaction) { CommandTimeout = 5 }";
                var statement = SyntaxFactory.ParseStatement(method == "ReadAsync"
                    ? "await using var command = " + construction + ";"
                    : "command = " + construction + ";");
                var creations = ownerMethod.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                    .Where(item => SyntaxFactory.AreEquivalent(item.Type, SyntaxFactory.ParseTypeName("NpgsqlCommand"))).ToArray();
                if (creations.Length != 1 || !SyntaxFactory.AreEquivalent(creations[0], SyntaxFactory.ParseExpression(construction))
                    || !SyntaxFactory.AreEquivalent(creations[0].Ancestors().OfType<StatementSyntax>().FirstOrDefault(), statement)
                    || !ExecutedBinding(creations[0], ownerMethod))
                    offenders.Add(path + ": the shared SQL must be the exact executed command argument in " + method);
            }
            if (path == OrganizationSelfDiscovery)
            {
                const string Construction = "new NpgsqlCommand(LabelsSql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = 5 }";
                var creations = ownerMethod.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                    .Where(item => SyntaxFactory.AreEquivalent(item.Type, SyntaxFactory.ParseTypeName("NpgsqlCommand"))).ToArray();
                if (creations.Length != 1 || !SyntaxFactory.AreEquivalent(creations[0], SyntaxFactory.ParseExpression(Construction))
                    || creations[0].Parent is not EqualsValueClauseSyntax
                    {
                        Parent: VariableDeclaratorSyntax { Identifier.ValueText: "command", Parent: VariableDeclarationSyntax declaration }
                    }
                    || declaration.Variables.Count != 1
                    || declaration.Parent is not UsingStatementSyntax commandOwner
                    || !commandOwner.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)
                    || !ExecutedBinding(creations[0], ownerMethod))
                    offenders.Add(path + ": LabelsSql must be the one executed await-using common-owner command");
            }
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

    // Migration SQL is installed by learnstack_migration, not a runtime reader.
    // Recognize only the two executed argument spans; the containing DDL file and
    // every other organization-bearing literal/interpolation remain in the census.
    private static HashSet<int> OrganizationMigrationSqlArguments(string path, SyntaxNode root, List<string> found)
    {
        var accepted = new HashSet<int>();
        if (path != OrganizationOriginDdl) return accepted;
        var owners = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(owner =>
            OrganizationTypeIdentity(owner) == "LearnStack.Modules.Tenancy.Infrastructure.Persistence.SeedAdministrationOriginDdl").ToArray();
        if (owners.Length != 1 || owners[0].TypeParameterList is not null || owners[0].Modifiers.Count != 2
            || !owners[0].Modifiers.Any(SyntaxKind.PublicKeyword) || !owners[0].Modifiers.Any(SyntaxKind.StaticKeyword)
            || owners[0].Parent is not BaseNamespaceDeclarationSyntax) return accepted;
        foreach (var name in OrganizationOriginInstallers)
        {
            var methods = owners[0].Members.OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == name).ToArray();
            if (methods.Length != 1) continue;
            var method = methods[0];
            if (method.TypeParameterList is not null || method.Modifiers.Count != 2
                || !method.Modifiers.Any(SyntaxKind.PublicKeyword) || !method.Modifiers.Any(SyntaxKind.StaticKeyword)
                || !SyntaxFactory.AreEquivalent(method.ReturnType, SyntaxFactory.ParseTypeName("void"))
                || !SyntaxFactory.AreEquivalent(method.ParameterList, SyntaxFactory.ParseParameterList("(MigrationBuilder migration)"))
                || method.Body is not { Statements.Count: 3 } body
                || !SyntaxFactory.AreEquivalent(body.Statements[0], SyntaxFactory.ParseStatement("ArgumentNullException.ThrowIfNull(migration);"))
                || !SyntaxFactory.AreEquivalent(body.Statements[1], SyntaxFactory.ParseStatement(OrganizationOriginDeclarations))
                || body.Statements[2] is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call }
                || !SyntaxFactory.AreEquivalent(call.Expression, SyntaxFactory.ParseExpression("migration.Sql"))
                || call.ArgumentList.Arguments.Count != 1
                || call.ArgumentList.Arguments[0] is not { NameColon: null, Expression: InterpolatedStringExpressionSyntax argument } parameter
                || !parameter.RefKindKeyword.IsKind(SyntaxKind.None)
                || !OrganizationMigrationSqlIsExact(name, argument)) continue;
            accepted.Add(argument.SpanStart);
            found.Add(name);
        }
        return accepted;
    }

    private static bool OrganizationMigrationSqlIsExact(string owner, InterpolatedStringExpressionSyntax argument)
    {
        var holes = argument.Contents.OfType<InterpolationSyntax>().ToArray();
        if (holes.Length != 3 || holes.Any(hole => hole.AlignmentClause is not null)
            || !SyntaxFactory.AreEquivalent(holes[0].Expression, SyntaxFactory.ParseExpression("declarations"))
            || holes[0].FormatClause is not null
            || holes.Skip(1).Any(hole => !SyntaxFactory.AreEquivalent(hole.Expression, SyntaxFactory.ParseExpression("UserId.SystemActor.Value"))
                || hole.FormatClause?.FormatStringToken.ValueText != "D")) return false;
        var sql = string.Concat(argument.Contents.Select(part => part is InterpolatedStringTextSyntax text
            ? text.TextToken.ValueText : "{fixed-pin}"));
        // Comments cannot lend the executed guard an absent key or INVOKER clause.
        sql = Regex.Replace(sql, @"--[^\r\n]*|/\*[\s\S]*?\*/", "", RegexOptions.CultureInvariant);
        if (Regex.Matches(sql, @"\borganizations\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Count != 1) return false;
        var normalized = NormalizeOrganizationSql(sql);
        var composite = NormalizeOrganizationSql("OR NOT EXISTS (" + OrganizationOriginCompositeSelect + ")");
        if (Regex.Matches(normalized, Regex.Escape(composite), RegexOptions.CultureInvariant).Count != 1) return false;
        var header = owner == "Install" ? "CREATE FUNCTION" : "CREATE OR REPLACE FUNCTION";
        string[] guard =
        [
            header + " public.fn_tenancy_seed_administration_origin_guard() RETURNS trigger LANGUAGE plpgsql VOLATILE SECURITY INVOKER SET search_path = pg_catalog",
            "IF TG_TABLE_SCHEMA <> 'public' OR TG_TABLE_NAME <> 'tenants' THEN",
            "OR current_setting('transaction_isolation')<>'read committed' OR OLD.id IS DISTINCT FROM NULLIF(current_setting('app.tenant_id',true),'')::uuid OR NULLIF(current_setting('app.organization_id',true),'') IS NOT NULL THEN",
        ];
        return guard.All(fragment => normalized.Contains(NormalizeOrganizationSql(fragment), StringComparison.Ordinal))
            && !normalized.Contains("securitydefiner", StringComparison.Ordinal);
    }

    private static bool ExecutedBinding(SyntaxNode call, MethodDeclarationSyntax owner)
    {
        if (call is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AddWithValue" } }
            && (call.Parent is not ExpressionStatementSyntax expression || !ReferenceEquals(expression.Expression, call)))
            return false; // The returned parameter cannot be captured/rebound or conditionally evaluated.
        if (call.Ancestors().TakeWhile(node => !ReferenceEquals(node, owner))
            .Any(node => node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)) return false;
        var statement = call.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (statement?.Parent == owner.Body) return true;
        // Each exact reader translates store failures around one unconditional
        // command. Its bindings execute in that root try body, never its catch,
        // a nested conditional or a deferred helper.
        if (owner.Identifier.ValueText is "ReadAsync" or "ReadAuthorizationAsync" && owner.Parent is TypeDeclarationSyntax lifecycle
            && OrganizationTypeIdentity(lifecycle) is "LearnStack.Modules.Tenancy.Infrastructure.Persistence.IdentityScopeLifecycleReader"
                or "LearnStack.Modules.Tenancy.Infrastructure.Persistence.PreContextSelfDiscoveryScopeReader"
            && statement?.Parent is BlockSyntax { Parent: TryStatementSyntax guarded } tryBody
            && guarded.Parent == owner.Body && ReferenceEquals(guarded.Block, tryBody)) return true;
        // The single captured-scope label command owns an await-using block
        // inside that root try. No conditional/local helper can justify a key.
        if (owner.Identifier.ValueText == "ReadAsync" && owner.Parent is TypeDeclarationSyntax labels
            && OrganizationTypeIdentity(labels) == "LearnStack.Modules.Tenancy.Infrastructure.Persistence.PreContextSelfDiscoveryScopeReader"
            && statement?.Parent is BlockSyntax { Parent: UsingStatementSyntax commandOwner } commandBody
            && ReferenceEquals(commandOwner.Statement, commandBody)
            && commandOwner.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)
            && commandOwner.Parent is BlockSyntax { Parent: TryStatementSyntax commandTry } outerTry
            && ReferenceEquals(commandTry.Block, outerTry) && commandTry.Parent == owner.Body)
            return true;
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
        if (path == OrganizationIdentityLifecycle && member == "LifecycleSql"
            || path == OrganizationSelfDiscovery && member == "LabelsSql")
            return literal.Parent is EqualsValueClauseSyntax
            {
                Parent: VariableDeclaratorSyntax
                { Parent: VariableDeclarationSyntax declaration } variable
            }
                && variable.Identifier.ValueText == member
                && declaration.Variables.Count == 1
                && SyntaxFactory.AreEquivalent(declaration.Type, SyntaxFactory.ParseTypeName("string"))
                && declaration.Parent is FieldDeclarationSyntax field
                && field.Modifiers.Count == 2
                && field.Modifiers.Any(token => token.IsKind(SyntaxKind.PrivateKeyword))
                && field.Modifiers.Any(token => token.IsKind(SyntaxKind.ConstKeyword));
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
        OrganizationReadOffenders(production).Should().BeEmpty("all seven runtime SQL subjects and both exact migration owners must exist");
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
        var origin = production.Single(file => file.Path == OrganizationOriginDdl);
        var originRoot = CSharpSyntaxTree.ParseText(origin.Code).GetRoot();
        foreach (var installer in OrganizationOriginInstallers)
        {
            var method = originRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(item => item.Identifier.ValueText == installer);
            var statement = (ExpressionStatementSyntax)method.Body!.Statements[2];
            var call = (InvocationExpressionSyntax)statement.Expression;
            var argument = (InterpolatedStringExpressionSyntax)call.ArgumentList.Arguments[0].Expression;
            Dirty(origin.Path, originRoot.RemoveNode(method, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString());
            Dirty(origin.Path, originRoot.ReplaceNode(statement,
                SyntaxFactory.ParseStatement("var unusedSql = " + argument + ";")).ToFullString());
            foreach (var (from, to) in new[]
                     {
                         ("organization.tenant_id=OLD.id AND ", ""),
                         (" AND organization.id=OLD.default_organization_id", ""),
                         ("organization.status='Active'", "organization.status='Suspended'"),
                         ("AND organization.deleted_by IS NULL)", "AND organization.deleted_by IS NULL OR true)"),
                         ("RETURN NEW;", "PERFORM 1 FROM public.organizations; RETURN NEW;"),
                         ("{{declarations}}", "{{declarations + \"SELECT 1 FROM public.organizations\"}}"),
                         ("UserId.SystemActor.Value:D", "UserId.SystemActor.Value:N"),
                         ("SECURITY INVOKER", "SECURITY DEFINER"),
                         ("current_setting('app.tenant_id',true)", "current_setting('app.other_tenant',true)"),
                     })
            {
                var changed = argument.ToString().Replace(from, to, StringComparison.Ordinal);
                changed.Should().NotBe(argument.ToString(), "each planted fault must change the actual migration argument");
                Dirty(origin.Path, origin.Code.Remove(argument.SpanStart, argument.Span.Length).Insert(argument.SpanStart, changed));
            }
        }
        Dirty(origin.Path, origin.Code.Replace("public static class SeedAdministrationOriginDdl",
            "public static class UnreviewedOriginDdl", StringComparison.Ordinal));
        Dirty(origin.Path, origin.Code.Replace("public static void Install(", "private static void Install(", StringComparison.Ordinal));
        Dirty(origin.Path, origin.Code.Replace("SeedAdministrationOriginManifest.All", "UnreviewedDeclarations.All", StringComparison.Ordinal));
        Dirty(origin.Path, origin.Code + "\ninternal static class ExtraOriginSql { const string Sql = \"SELECT 1 FROM public.organizations\"; }");
        OrganizationReadOffenders(production.Append(("Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/Persistence/UnlistedOriginDdl.cs",
            origin.Code))).Should().NotBeEmpty("a copied installer in another file has no organization SQL allowance");
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
        var identity = production.Single(file => file.Path == OrganizationIdentityLifecycle);
        var identityRoot = CSharpSyntaxTree.ParseText(identity.Code).GetRoot();
        var lifecycleField = identityRoot.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Single(field => field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "LifecycleSql"));
        Dirty(identity.Path, identity.Code.Remove(lifecycleField.SpanStart, lifecycleField.Span.Length));
        Dirty(identity.Path, identity.Code.Remove(lifecycleField.SpanStart, lifecycleField.Span.Length)
            + "\ninternal static class UnusedLifecycleSql { " + lifecycleField + " }");
        Dirty(identity.Path, identity.Code.Replace("private const string LifecycleSql", "public const string LifecycleSql", StringComparison.Ordinal));
        Dirty(identity.Path, identity.Code.Replace("private const string LifecycleSql", "private readonly string LifecycleSql", StringComparison.Ordinal));
        foreach (var method in identityRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
                     .Where(method => method.Identifier.ValueText is "ReadAsync" or "ReadAuthorizationAsync"))
        {
            var creation = method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Single(item => item.Type.ToString() == "NpgsqlCommand");
            var statement = creation.Ancestors().OfType<StatementSyntax>().First();
            Dirty(identity.Path, identityRoot.ReplaceNode(statement,
                SyntaxFactory.ParseStatement("void UnusedCommand() { " + statement + " } ")).ToFullString());
            // Keep exactly two references: a constant mentioned by a deferred
            // helper cannot justify another SQL argument on the live command.
            var wrongArgument = identityRoot.ReplaceNode(creation, SyntaxFactory.ParseExpression(
                creation.ToString().Replace("LifecycleSql", "string.Empty", StringComparison.Ordinal)));
            var changedMethod = wrongArgument.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(item => item.Identifier.ValueText == method.Identifier.ValueText);
            Dirty(identity.Path, wrongArgument.ReplaceNode(changedMethod, changedMethod.WithBody(changedMethod.Body!
                .AddStatements(SyntaxFactory.ParseStatement("void UnusedSql() { _ = LifecycleSql; }")))).ToFullString());
            if (method.Identifier.ValueText != "ReadAuthorizationAsync") continue;
            foreach (var binding in method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                         .Where(call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AddWithValue" }))
            {
                var bindingStatement = binding.Ancestors().OfType<StatementSyntax>().First();
                Dirty(identity.Path, identityRoot.RemoveNode(bindingStatement, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString());
                Dirty(identity.Path, identityRoot.ReplaceNode(bindingStatement,
                    SyntaxFactory.ParseStatement("void UnusedBinding() { " + bindingStatement + " } ")).ToFullString());
                Dirty(identity.Path, identityRoot.ReplaceNode(bindingStatement,
                    SyntaxFactory.ParseStatement("if (false) { " + bindingStatement + " } ")).ToFullString());
            }
        }
        const string IdentityTenantBinding = "command.Parameters.AddWithValue(\"tenant\", tenantId.Value);";
        const string IdentityOrganizationBinding = "command.Parameters.AddWithValue(\"organization\", NpgsqlDbType.Uuid, organizationId is { } org\n                ? org.Value : DBNull.Value);";
        foreach (var binding in new[] { IdentityTenantBinding, IdentityOrganizationBinding })
        {
            identity.Code.Should().Contain(binding, "the mutation must reach the actual lifecycle reader's protected binding");
            Dirty(identity.Path, identity.Code.Replace(binding, "", StringComparison.Ordinal));
            Dirty(identity.Path, identity.Code.Replace(binding, "void UnusedBinding() { " + binding + " }", StringComparison.Ordinal));
            Dirty(identity.Path, identity.Code.Replace(binding, "if (false) { " + binding + " }", StringComparison.Ordinal));
        }
        foreach (var (from, to) in new[]
                 {
                     ("o.tenant_id = t.id AND ", ""), ("LEFT JOIN public.tenants AS t ON t.id = @tenant", "LEFT JOIN public.tenants AS t ON t.id IS NOT NULL"),
                     ("\"tenant\", tenantId.Value", "\"tenant\", Guid.Empty"),
                     ("? org.Value : DBNull.Value", "? Guid.Empty : DBNull.Value"),
                     ("OrganizationId? organizationId", "OrganizationId organizationId"),
                     ("Task<IdentityScopeLifecycle>", "Task<object>"),
                     ("(NpgsqlConnection)unit.Connection", "unit.Connection"),
                     ("(NpgsqlTransaction)transaction", "transaction"),
                     ("CommandTimeout = 10", "CommandTimeout = 20"),
                     ("CommandTimeout = 5", "CommandTimeout = 20"),
                     ("\"tenant\", candidate.TenantId.Value", "\"tenant\", Guid.Empty"),
                     ("? organization.Value : DBNull.Value", "? Guid.Empty : DBNull.Value"),
                     ("IUnitOfWork child, AuthorizationCandidate candidate", "IUnitOfWork child, object candidate"),
                     ("(NpgsqlConnection)child.Connection", "child.Connection"),
                     ("command.ExecuteReaderAsync(cancellationToken)", "command.ExecuteReaderAsync(CancellationToken.None)"),
                 }) Dirty(identity.Path, identity.Code.Replace(from, to, StringComparison.Ordinal));
        Dirty(identity.Path, identity.Code.Replace(IdentityTenantBinding, IdentityTenantBinding
            + " command.Parameters[\"tenant\"].Value = Guid.Empty;", StringComparison.Ordinal));
        Dirty(identity.Path, identity.Code.Replace(IdentityTenantBinding,
            "var rebound = command.Parameters.AddWithValue(\"tenant\", tenantId.Value); rebound.Value = Guid.Empty;", StringComparison.Ordinal));
        var self = production.Single(file => file.Path == OrganizationSelfDiscovery);
        var selfRoot = CSharpSyntaxTree.ParseText(self.Code).GetRoot();
        var labelsField = selfRoot.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Single(field => field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "LabelsSql"));
        Dirty(self.Path, self.Code.Remove(labelsField.SpanStart, labelsField.Span.Length));
        Dirty(self.Path, self.Code.Remove(labelsField.SpanStart, labelsField.Span.Length)
            + "\ninternal static class UnusedLabelsSql { " + labelsField + " }");
        Dirty(self.Path, self.Code.Replace("private const string LabelsSql", "public const string LabelsSql", StringComparison.Ordinal));
        Dirty(self.Path, self.Code.Replace("private const string LabelsSql", "private readonly string LabelsSql", StringComparison.Ordinal));
        var labelMethod = selfRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "ReadAsync");
        foreach (var binding in labelMethod.DescendantNodes().OfType<InvocationExpressionSyntax>()
                     .Where(call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AddWithValue" }))
        {
            var bindingStatement = binding.Ancestors().OfType<StatementSyntax>().First();
            Dirty(self.Path, selfRoot.RemoveNode(bindingStatement, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString());
            Dirty(self.Path, selfRoot.ReplaceNode(bindingStatement,
                SyntaxFactory.ParseStatement("void UnusedBinding() { " + bindingStatement + " } ")).ToFullString());
            Dirty(self.Path, selfRoot.ReplaceNode(bindingStatement,
                SyntaxFactory.ParseStatement("if (false) { " + bindingStatement + " } ")).ToFullString());
        }
        var labelConstruction = labelMethod.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(item => item.Type.ToString() == "NpgsqlCommand");
        var labelStatement = labelConstruction.Ancestors().OfType<StatementSyntax>().First();
        Dirty(self.Path, selfRoot.ReplaceNode(labelStatement,
            SyntaxFactory.ParseStatement("void UnusedCommand() { " + labelStatement + " } ")).ToFullString());
        var wrongLabelsArgument = selfRoot.ReplaceNode(labelConstruction, SyntaxFactory.ParseExpression(
            labelConstruction.ToString().Replace("LabelsSql", "string.Empty", StringComparison.Ordinal)));
        var wrongLabelMethod = wrongLabelsArgument.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "ReadAsync");
        Dirty(self.Path, wrongLabelsArgument.ReplaceNode(wrongLabelMethod, wrongLabelMethod.WithBody(wrongLabelMethod.Body!
            .AddStatements(SyntaxFactory.ParseStatement("void UnusedSql() { _ = LabelsSql; }")))).ToFullString());
        foreach (var (from, to) in new[]
                 {
                     ("o.tenant_id=t.id AND ", ""), ("WHERE t.id=@tenant", "WHERE t.id IS NOT NULL"),
                     ("\"tenant\", witness.TenantId.Value", "\"tenant\", Guid.Empty"),
                     ("? org.Value : DBNull.Value", "? Guid.Empty : DBNull.Value"),
                     ("OwnMembershipScopeWitness witness", "TenantId witness"),
                     ("Task<OwnMembershipScopeLabel?>", "Task<object>"),
                     ("(NpgsqlConnection)unit.Connection", "unit.Connection"),
                     ("(NpgsqlTransaction)unit.Transaction!", "unit.Transaction"),
                     ("CommandTimeout = 5", "CommandTimeout = 20"),
                     ("deadline.CancelAfter(TimeSpan.FromSeconds(5))", "deadline.CancelAfter(TimeSpan.FromSeconds(20))"),
                     ("self.BeginSelfDiscoveryTransactionAsync(witness, deadline.Token)", "unit.BeginTransactionAsync(TransactionMode.ReadWrite, deadline.Token)"),
                     ("self.IsSelfDiscoveryOwner(witness)", "self.IsSelfDiscoveryOwner(null!)"),
                     ("command.ExecuteReaderAsync(deadline.Token)", "command.ExecuteReaderAsync(CancellationToken.None)"),
                     ("frame.CompleteAsync(deadline.Token)", "frame.CompleteAsync(CancellationToken.None)"),
                 }) Dirty(self.Path, self.Code.Replace(from, to, StringComparison.Ordinal));
        const string SelfTenantBinding = "command.Parameters.AddWithValue(\"tenant\", witness.TenantId.Value);";
        Dirty(self.Path, self.Code.Replace(SelfTenantBinding, SelfTenantBinding
            + " command.Parameters[\"tenant\"].Value = Guid.Empty;", StringComparison.Ordinal));
        Dirty(self.Path, self.Code.Replace(SelfTenantBinding,
            "var rebound = command.Parameters.AddWithValue(\"tenant\", witness.TenantId.Value); rebound.Value = Guid.Empty;", StringComparison.Ordinal));
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
