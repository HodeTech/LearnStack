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
        registrations.Should().HaveCount(8);
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
        if (declared.Length != 5 || declared.Distinct().Count() != declared.Length
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

    private static bool Has(SyntaxNode node, params string[] required) => required.All(fragment =>
        Tokens(node).Contains(Tokens(fragment), StringComparison.Ordinal));
    private static string Tokens(SyntaxNode node) => string.Join(" ", node.DescendantTokens().Select(token => token.Text));
    private static string Tokens(string fragment) => string.Join(" ", SyntaxFactory.ParseTokens(fragment)
        .Where(token => !token.IsKind(SyntaxKind.EndOfFileToken)).Select(token => token.Text));
    private sealed record DeniedProof(string Kind, string File, string Method, params string[] Required);
}
