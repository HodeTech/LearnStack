using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.SharedKernel.Permissions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OperationPermissionConventionTests
{
    private const string PairFile = "LearnStack.Tests.Integration/Database/RoleGrantCommandIntegrationTests.Registry.cs";
    private const string PairMethod = "Every_assignable_pair_requires_a_legal_delegation_target_with_committed_denial_and_no_refused_mutation";
    private const string ReleasedPairMethod = "AssertReleasedAssignablePairAsync";
    private const string PairOwnerFile = "LearnStack.Tests.Integration/Database/RoleGrantCommandIntegrationTests.cs";
    private const string HarnessFile = "LearnStack.Tests.Integration/Database/RoleGrantPipelineHarness.cs";
    private static readonly DeniedProof[] PublicDeniedProofs =
    [
        new("tenancy.public_site.read", "LearnStack.Tests.Integration/Database/PublicSiteHttpTests.cs",
            "Unknown_and_platform_hosts_keep_their_generic_refusals", "fixture.ClientFor", "client.SendAsync", "HttpStatusCode.NotFound"),
        new("education.public_course.read", "LearnStack.Tests.Integration/Database/PublicEducationHttpTests.cs",
            "Course_draft_branch_foreign_and_unknown_are_uniformly_hidden", "fixture.ClientFor", "client.GetAsync", "HttpStatusCode.NotFound"),
        new("education.public_lesson.read", "LearnStack.Tests.Integration/Database/PublicEducationHttpTests.cs",
            "Lesson_masks_draft_parent_draft_restricted_scope_wrong_parent_and_foreign_rows", "fixture.ClientFor", "client.GetAsync", "HttpStatusCode.NotFound"),
    ];
    private static readonly DeniedProof[] OtherDeniedProofs =
    [
        new("human capability", "LearnStack.Tests.Integration/Database/IdentityAuthorizationHttpTests.cs",
            "Coverage_capability_and_store_failure_are_distinct_and_never_dispatch_the_directory",
            "SendAsync", "HttpStatusCode.Forbidden", "\"identity.authorization.deny\""),
        new("role administration", "LearnStack.Tests.Integration/Database/RoleGrantCommandIntegrationTests.cs",
            "Organization_actor_and_orgless_reissue_never_promote_Organization_grants_to_Tenant_role_administration",
            "new ChangeRoleGrantsCommand", "branch.Sender.Send", "Assert.Equal(\"forbidden\""),
        new("missing delegation authority", "LearnStack.Tests.Integration/Database/RoleGrantCommandIntegrationTests.cs",
            "Removed_profile_authority_cannot_self_restore_through_change_or_v1_adoption_and_kernel_cannot_be_removed",
            "new ChangeRoleGrantsCommand", "new AdoptRoleGrantManifestCommand", "Assert.Equal(\"forbidden\""),
        new("seed", "LearnStack.Tests.Integration/SeedOperationSourceTests.cs",
            "Actual_seed_composition_captures_the_exact_finite_manifest_and_closes_it_with_its_scope",
            "SeedComposition.Build", "source.Capture", "descriptor == null", "!descriptor.IsActive"),
        new("delivery", "LearnStack.Tests.Unit/Infrastructure/Messaging/DeliveryOperationSourceTests.cs",
            "Exact_validated_handler_binding_admits_only_its_command_and_cannot_be_installed_twice",
            "new DeliveryOperationSource", "uninstalled.Capture", "Should().BeNull", "Throw<InvalidOperationException>"),
        new("job", "LearnStack.Tests.Unit/Application/Jobs/TrustedJobOperationSourceTests.cs",
            "Exact_framework_request_captures_its_existing_live_owner_without_readmission",
            "new BackgroundJobOperationSource", "source.Capture", "Should().BeNull", "descriptor.IsActive.Should().BeFalse"),
    ];

    [Fact]
    public void Permission_Registry_Has_DeniedTest()
    {
        var catalog = Catalog();
        catalog.TryGet(typeof(ChangeRoleGrantsCommand), out var consumer).Should().BeTrue();
        consumer.Kind.Should().Be(OperationPolicyKind.Protected);
        var registrations = catalog.Definitions.ToArray();
        registrations.Should().HaveCount(11);
        var files = PublicDeniedProofs.Concat(OtherDeniedProofs).Select(proof => proof.File)
            .Append(PairFile).Append(HarnessFile).Distinct(StringComparer.Ordinal).ToDictionary(file => file,
                file => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(RepositoryPaths.RepoRoot(), "backend/tests", file))).GetCompilationUnitRoot(),
                StringComparer.Ordinal);
        DeniedProofOffenders(registrations, files).Should().BeEmpty(
            "the actual assignable pairs have real delegation-target positive/denied cases; virtual and finite classes retain their distinct negative owners");
        foreach (var registration in registrations)
            DeniedProofOffenders(registrations.Where(item => item != registration).ToArray(), files).Should().NotBeEmpty(
                "removing a real definition cannot leave a vacuous proof map");
        var extra = new PermissionRegistration(new(PermissionScope.Tenant, "identity.unproved.write"), true);
        DeniedProofOffenders([.. registrations, extra], files).Should().Contain("assignable pairs");

        var pair = FindProof(files[PairFile], PairMethod)!;
        foreach (var data in pair.AttributeLists.SelectMany(list => list.Attributes).Where(attribute => attribute.Name.ToString() == "InlineData"))
        {
            var dirty = new Dictionary<string, CompilationUnitSyntax>(files, StringComparer.Ordinal)
            { [PairFile] = files[PairFile].RemoveNode(data, SyntaxRemoveOptions.KeepNoTrivia)! };
            DeniedProofOffenders(registrations, dirty).Should().Contain("assignable pairs", "each exact scoped test case is required");
        }
        foreach (var proof in PublicDeniedProofs.Concat(OtherDeniedProofs).Append(new("assignable", PairFile, PairMethod)))
        {
            var method = FindProof(files[proof.File], proof.Method)!;
            var dirty = new Dictionary<string, CompilationUnitSyntax>(files, StringComparer.Ordinal)
            { [proof.File] = files[proof.File].RemoveNode(method, SyntaxRemoveOptions.KeepNoTrivia)! };
            DeniedProofOffenders(registrations, dirty).Should().NotBeEmpty("removing an executing owner must break its cross-reference");
        }
        var denialAssertion = pair.DescendantNodes().OfType<ExpressionStatementSyntax>()
            .Single(statement => Tokens(statement).Contains(Tokens("Assert.Equal(\"forbidden\", refused.Error!.Code)"), StringComparison.Ordinal));
        var withoutAssertion = new Dictionary<string, CompilationUnitSyntax>(files, StringComparer.Ordinal)
        { [PairFile] = files[PairFile].RemoveNode(denialAssertion, SyntaxRemoveOptions.KeepNoTrivia)! };
        DeniedProofOffenders(registrations, withoutAssertion).Should().Contain("assignable consumer assertions");

        foreach (var name in new[] { ReleasedPairMethod, "SendReleasedAsync", "ReadReleasedRoleAsync",
                     "ReleasedPairAuditAsync", "InstallReleasedPairLifecycleAsync" })
        {
            var method = FindDeniedProofHelper(files[PairFile], name)!;
            var dirty = new Dictionary<string, CompilationUnitSyntax>(files, StringComparer.Ordinal)
            { [PairFile] = files[PairFile].RemoveNode(method, SyntaxRemoveOptions.KeepNoTrivia)! };
            DeniedProofOffenders(registrations, dirty).Should().Contain("actual released pair evidence");
        }
        var released = FindDeniedProofHelper(files[PairFile], ReleasedPairMethod)!;
        foreach (var fragment in new[] { "var adoption = await SendReleasedAsync", "Assert.True(adoption.IsSuccess)",
                     "Assert.Equal(release.Version, adopted.AdoptedVersion)", "var positive = await SendReleasedAsync",
                     "Assert.True(positive.IsSuccess)", "var refused = await SendReleasedAsync",
                     "Assert.Equal(\"forbidden\", refused.Error!.Code)", "Assert.Equal(before.Version, after.Version)",
                     "Assert.Equal(before.Grants, after.Grants)", "var denial = await ReleasedPairAuditAsync",
                     "Assert.Equal(\"denied\", denial.Outcome)" })
        {
            var statement = released.Body!.Statements.Single(statement => Has(statement, fragment));
            var dirty = new Dictionary<string, CompilationUnitSyntax>(files, StringComparer.Ordinal)
            { [PairFile] = files[PairFile].RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)! };
            DeniedProofOffenders(registrations, dirty).Should().Contain("actual released pair evidence",
                "real adoption, positive mutation, refusal and committed observations are each required");
        }
        var releaseBranch = pair.Body!.Statements.OfType<IfStatementSyntax>().Single();
        var disabledRelease = new Dictionary<string, CompilationUnitSyntax>(files, StringComparer.Ordinal)
        { [PairFile] = files[PairFile].ReplaceNode(releaseBranch.Condition, SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)) };
        DeniedProofOffenders(registrations, disabledRelease).Should().Contain("released pair dispatch");
        var adoptionStatement = released.Body!.Statements.Single(statement => Has(statement, "var adoption = await SendReleasedAsync"));
        var positiveStatement = released.Body.Statements.Single(statement => Has(statement, "var positive = await SendReleasedAsync"));
        var reorderedBody = released.Body.WithStatements(SyntaxFactory.List(released.Body.Statements.Select(statement =>
            ReferenceEquals(statement, adoptionStatement) ? positiveStatement : ReferenceEquals(statement, positiveStatement) ? adoptionStatement : statement)));
        var adoptionAfterPositive = new Dictionary<string, CompilationUnitSyntax>(files, StringComparer.Ordinal)
        { [PairFile] = files[PairFile].ReplaceNode(released.Body, reorderedBody) };
        DeniedProofOffenders(registrations, adoptionAfterPositive).Should().Contain("actual released pair evidence",
            "first adoption must execute before the delegated positive mutation");
    }

    private static List<string> DeniedProofOffenders(PermissionRegistration[] registrations,
        Dictionary<string, CompilationUnitSyntax> files)
    {
        var errors = new List<string>();
        var pair = FindProof(files[PairFile], PairMethod);
        var declared = pair?.AttributeLists.SelectMany(list => list.Attributes)
            .Where(attribute => attribute.Name.ToString() == "InlineData")
            .Select(attribute => attribute.ArgumentList?.Arguments)
            .Where(arguments => arguments is { Count: 2 })
            .Select(arguments => (Scope: arguments!.Value[0].Expression.ToString().Replace("PermissionScope.", "", StringComparison.Ordinal),
                Key: (arguments.Value[1].Expression as LiteralExpressionSyntax)?.Token.ValueText ?? ""))
            .ToArray() ?? [];
        var actual = registrations.Where(item => item.Assignable)
            .Select(item => (Scope: item.Definition.Scope.ToString(), Key: item.Definition.Key)).ToArray();
        if (declared.Length != 8 || declared.Distinct().Count() != declared.Length
            || !declared.ToHashSet().SetEquals(actual) || actual.Length != declared.Length) errors.Add("assignable pairs");
        var virtualPairs = registrations.Where(item => !item.Assignable).Select(item => item.Definition).ToArray();
        if (virtualPairs.Length != 3 || !virtualPairs.Select(item => item.Key).ToHashSet(StringComparer.Ordinal)
                .SetEquals(PublicDeniedProofs.Select(proof => proof.Kind))
            || virtualPairs.Any(item => item.Scope != PermissionScope.Tenant || !item.IsActive)) errors.Add("virtual public pairs");
        if (pair is null || !Has(pair,
                "new PermissionDefinition(permissionScope, permissionKey)", "new ChangeRoleGrantsCommand",
                "Assert.True(positive.IsSuccess)", "Assert.Equal(\"forbidden\", refused.Error!.Code)",
                "Assert.Equal(before.Version, after.Version)", "Assert.Equal(before.Grants, after.Grants)",
                "AuditAsync(correlation, \"identity.authorization.deny\")", "Assert.Equal(\"denied\", denial.Outcome)",
                "GetProperty(\"denial_reason\")", "SendAsync")) errors.Add("assignable consumer assertions");
        // The mapped pair case uses the real pipeline/reader/app-role store, not an affirmative evaluator double.
        if (!Has(files[HarnessFile], "ApplicationDataSource.Build", "new IdentityAuthorizationReadSource",
                "new AuthorizationEvidenceSource", "services.AddIdentityAuthorizationReader()",
                "services.AddIdentityContextualFoundation()", "services.AddLearnStackOperationAuthorization()",
                "services.AddLearnStackMediatRPipeline")) errors.Add("actual pair consumer harness");
        if (!Has(files[PairOwnerFile], "RoleGrantPipelineHarness.CreateAsync(fixture.AppConnectionString",
                "await using var harness = await CreateAsync(correlation)", "return await harness.Sender.Send(request)",
                "new NpgsqlCommand", "ScopeAsync(app, transaction, SchemaFixture.TenantA)",
                "Assert.False(await rows.ReadAsync())")) errors.Add("actual pair dispatch and committed evidence");
        var releaseBranch = pair?.Body?.Statements.OfType<IfStatementSyntax>().SingleOrDefault();
        if (releaseBranch is null || Tokens(releaseBranch.Condition) != Tokens("IdentityRoleRelease.Version2.TenantAdminDelta.Contains(pair)")
            || Tokens(releaseBranch.Statement) != Tokens("{ await AssertReleasedAssignablePairAsync(pair); return; }"))
            errors.Add("released pair dispatch");
        if (!HasOrderedProofStatements(FindDeniedProofHelper(files[PairFile], ReleasedPairMethod),
                "await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Membership.Postgres)",
                "await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString)",
                "await InstallReleasedPairLifecycleAsync(database.AppConnectionString)",
                "var release = IdentityRoleRelease.Version2",
                "var administrator = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)",
                "var initial = await ReadReleasedRoleAsync(database.AppConnectionString, administrator)",
                "Assert.Equal(1, initial.AdoptedVersion)",
                "Assert.Equal(IdentityFoundationManifest.Version1.Fingerprint, initial.Fingerprint)",
                "Assert.DoesNotContain(pair.Scope + \":\" + pair.Key, initial.Grants)",
                "var adoption = await SendReleasedAsync(database.AppConnectionString, new AdoptRoleGrantManifestCommand(administrator, initial.Version, release.Version, release.Fingerprint)",
                "Assert.True(adoption.IsSuccess)",
                "var adopted = await ReadReleasedRoleAsync(database.AppConnectionString, administrator)",
                "Assert.Equal(initial.Version + 1, adopted.Version)", "Assert.Equal(release.Version, adopted.AdoptedVersion)",
                "Assert.Equal(release.Fingerprint, adopted.Fingerprint)",
                "Assert.Equal(initial.Grants.Concat(release.TenantAdminDelta.Select(definition => definition.Scope + \":\" + definition.Key)).Order(StringComparer.Ordinal), adopted.Grants)",
                "var adoptionAudit = await ReleasedPairAuditAsync(database.AppConnectionString, correlation + \"-adopt\", \"identity.role.adopt_manifest\")",
                "Assert.Equal(\"success\", adoptionAudit.Outcome)",
                "Assert.Equal(\"Must:SecurityEvent:Role:\" + administrator.ToString(\"D\"), adoptionAudit.Classification)",
                "var matchingRole = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Instructor, PermissionScope.Tenant)",
                "var matchingBefore = await ReadReleasedRoleAsync(database.AppConnectionString, matchingRole)",
                "var positive = await SendReleasedAsync(database.AppConnectionString, new ChangeRoleGrantsCommand(matchingRole, matchingBefore.Version, [pair]), correlation + \"-positive\")",
                "Assert.True(positive.IsSuccess)",
                "var matchingAfter = await ReadReleasedRoleAsync(database.AppConnectionString, matchingRole)",
                "Assert.Equal(matchingBefore.Version + 1, matchingAfter.Version)",
                "Assert.Equal([pair.Scope + \":\" + pair.Key], matchingAfter.Grants)",
                "var success = await ReleasedPairAuditAsync(database.AppConnectionString, correlation + \"-positive\", \"identity.role.change_grants\")",
                "Assert.Equal(\"success\", success.Outcome)",
                "Assert.Equal(\"Must:SecurityEvent:Role:\" + matchingRole.ToString(\"D\"), success.Classification)",
                "var oppositeRole = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.OrgInstructor, PermissionScope.Organization)",
                "var before = await ReadReleasedRoleAsync(database.AppConnectionString, oppositeRole)",
                "var refused = await SendReleasedAsync(database.AppConnectionString, new ChangeRoleGrantsCommand(oppositeRole, before.Version, [pair]), correlation)",
                "Assert.Equal(\"forbidden\", refused.Error!.Code)",
                "var after = await ReadReleasedRoleAsync(database.AppConnectionString, oppositeRole)",
                "Assert.Equal(before.Version, after.Version)", "Assert.Equal(before.AdoptedVersion, after.AdoptedVersion)",
                "Assert.Equal(before.Fingerprint, after.Fingerprint)", "Assert.Equal(before.Grants, after.Grants)",
                "var denial = await ReleasedPairAuditAsync(database.AppConnectionString, correlation, \"identity.authorization.deny\")",
                "Assert.Equal(\"denied\", denial.Outcome)", "Assert.Equal(\"Must:SecurityEvent::\", denial.Classification)",
                "using var metadata = JsonDocument.Parse(denial.Metadata)",
                "Assert.Equal(\"delegation\", metadata.RootElement.GetProperty(\"denial_reason\").GetString())",
                "var authority = metadata.RootElement.GetProperty(\"authorization\")",
                "Assert.Equal(AdminMembershipA.ToString(\"D\"), Assert.Single(authority.GetProperty(\"memberships\").EnumerateArray()).GetString())",
                "Assert.Equal(administrator.ToString(\"D\"), Assert.Single(authority.GetProperty(\"roles\").EnumerateArray()).GetString())")
            || !HasOrderedProofStatements(FindDeniedProofHelper(files[PairFile], "SendReleasedAsync"),
                "await using var harness = await RoleGrantPipelineHarness.CreateAsync(connectionString, Authority, TenantId.From(SchemaFixture.TenantA), UserId.From(AdminA), correlation)",
                "return await harness.Sender.Send(request)")
            || !HasCommittedReleasedRead(FindDeniedProofHelper(files[PairFile], "ReadReleasedRoleAsync"), "role_permission_grants", "adopted_release_version")
            || !HasCommittedReleasedRead(FindDeniedProofHelper(files[PairFile], "ReleasedPairAuditAsync"), "FROM audit_log WHERE correlation_id=@correlation AND operation=@operation")
            || FindDeniedProofHelper(files[PairFile], "InstallReleasedPairLifecycleAsync") is not { } lifecycle
            || !Has(lifecycle, "SchemaQueries.AssertApplicationRoleAsync(app)", "ScopeAsync(app, transaction, SchemaFixture.TenantA)",
                "await transaction.CommitAsync()")
            || !new[] { "INSERT INTO public.tenants", "INSERT INTO public.organizations" }.All(sql => lifecycle.DescendantNodes()
                .OfType<LiteralExpressionSyntax>().Any(literal => literal.Token.ValueText.Contains(sql, StringComparison.Ordinal))))
            errors.Add("actual released pair evidence");
        foreach (var proof in PublicDeniedProofs.Concat(OtherDeniedProofs))
        {
            var method = FindProof(files[proof.File], proof.Method);
            if (method is null || !Has(method, proof.Required)) errors.Add("proof: " + proof.Kind);
        }
        return errors;
    }

    private static MethodDeclarationSyntax? FindProof(CompilationUnitSyntax root, string name) =>
        root.DescendantNodes().OfType<MethodDeclarationSyntax>().SingleOrDefault(method => method.Identifier.ValueText == name
            && method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => attribute.Name.ToString() is "Fact" or "Theory")
            && !method.AttributeLists.SelectMany(list => list.Attributes).SelectMany(attribute => attribute.ArgumentList?.Arguments ?? [])
                .Any(argument => argument.NameEquals?.Name.Identifier.ValueText == "Skip"));

    private static MethodDeclarationSyntax? FindDeniedProofHelper(CompilationUnitSyntax root, string name) =>
        root.DescendantNodes().OfType<MethodDeclarationSyntax>().SingleOrDefault(method => method.Identifier.ValueText == name);

    private static bool HasOrderedProofStatements(MethodDeclarationSyntax? method, params string[] required)
    {
        if (method?.Body is not { } body) return false;
        var statements = body.Statements.ToArray();
        if (statements.Any(statement => statement is not (LocalDeclarationStatementSyntax or ExpressionStatementSyntax or ReturnStatementSyntax))
            || statements.Take(Math.Max(0, statements.Length - 1)).Any(statement => statement is ReturnStatementSyntax)) return false;
        var cursor = 0;
        foreach (var fragment in required)
        {
            // Executing top-level statements, not a matching string or a disabled branch.
            while (cursor < statements.Length && (statements[cursor] is not (LocalDeclarationStatementSyntax or ExpressionStatementSyntax or ReturnStatementSyntax)
                       || !Has(statements[cursor], fragment))) cursor++;
            if (cursor++ == statements.Length) return false;
        }
        return true;
    }

    private static bool HasCommittedReleasedRead(MethodDeclarationSyntax? method, params string[] sql)
    {
        if (!HasOrderedProofStatements(method, "await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString)",
                "await SchemaQueries.AssertApplicationRoleAsync(app)", "await using var transaction = await app.BeginTransactionAsync()",
                "await ScopeAsync(app, transaction, SchemaFixture.TenantA)", "await using var command = new NpgsqlCommand",
                "await using var rows = await command.ExecuteReaderAsync()", "Assert.True(await rows.ReadAsync())",
                "Assert.False(await rows.ReadAsync())", "return result")) return false;
        var command = method!.Body!.Statements.OfType<LocalDeclarationStatementSyntax>()
            .Single(statement => Has(statement, "await using var command = new NpgsqlCommand"));
        return command.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Any(literal => sql.All(fragment => literal.Token.ValueText.Contains(fragment, StringComparison.Ordinal)));
    }

    private static bool Has(SyntaxNode node, params string[] required) => required.All(fragment =>
        Tokens(node).Contains(Tokens(fragment), StringComparison.Ordinal));
    private static string Tokens(SyntaxNode node) => string.Join(" ", node.DescendantTokens().Select(token => token.Text));
    private static string Tokens(string fragment) => string.Join(" ", SyntaxFactory.ParseTokens(fragment)
        .Where(token => !token.IsKind(SyntaxKind.EndOfFileToken)).Select(token => token.Text));
    private sealed record DeniedProof(string Kind, string File, string Method, params string[] Required);
}
