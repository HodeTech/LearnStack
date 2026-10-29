using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LearnStack.Tests.Architecture;

public sealed partial class OperationPolicyOwnershipTests
{
    private static void AssertTenancyCanonicalRecheck()
    {
        var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
            "Modules/Tenancy/LearnStack.Modules.Tenancy.Application/Authorization/TenancyAdministrationAuthorization.cs"));
        TenancyCanonicalRecheckIsConfined(source).Should().BeTrue(
            "Tenancy reuses the canonical authorizer with the same invocation, policy and candidate and no User fence");
        foreach (var (before, after) in new[]
        {
            ("authorizer.AuthorizeAsync(invocation, invocation.Policy, invocation.Candidate!, cancellationToken)",
                "authorizer.AuthorizeAsync(invocations.CurrentInvocation!, invocation.Policy, invocation.Candidate!, cancellationToken)"),
            ("invocation.Policy, invocation.Candidate!", "invocation.Policy, null!"),
            ("invocation, invocation.Policy", "invocation, null!"),
            ("new IdentityMutationLockPlan([], context.TenantId, mode)",
                "new IdentityMutationLockPlan([context.UserId!.Value], context.TenantId, mode)"),
            ("? IdentityTenantLockMode.Exclusive : IdentityTenantLockMode.Shared",
                "? IdentityTenantLockMode.Shared : IdentityTenantLockMode.Shared"),
        })
        {
            var dirty = source.Replace(before, after, StringComparison.Ordinal);
            dirty.Should().NotBe(source);
            TenancyCanonicalRecheckIsConfined(dirty).Should().BeFalse(
                "another candidate, plan or permission input cannot inherit the same-invocation exception");
        }
    }

    private static bool TenancyCanonicalRecheckIsConfined(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var gate = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .SingleOrDefault(type => type.Identifier.ValueText == "TenancyAdministrationAuthorization");
        if (root.ContainsDiagnostics || gate is null) return false;
        var authorization = gate.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression.ToString() == "authorizer.AuthorizeAsync").ToArray();
        var plan = gate.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(creation => creation.Type.ToString() == "IdentityMutationLockPlan").ToArray();
        var mode = gate.Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(method => method.Identifier.ValueText == "AdmitWriteAsync")?
            .DescendantNodes().OfType<VariableDeclaratorSyntax>().SingleOrDefault(variable => variable.Identifier.ValueText == "mode")?.Initializer?.Value;
        return authorization.Length == 1 && authorization[0].Ancestors().OfType<MethodDeclarationSyntax>()
                .Single().Identifier.ValueText == "RecheckAsync"
            && SyntaxFactory.AreEquivalent(authorization[0], SyntaxFactory.ParseExpression(
                "authorizer.AuthorizeAsync(invocation, invocation.Policy, invocation.Candidate!, cancellationToken)"))
            && plan.Length == 1 && SyntaxFactory.AreEquivalent(plan[0], SyntaxFactory.ParseExpression(
                "new IdentityMutationLockPlan([], context.TenantId, mode)"))
            && mode is not null && SyntaxFactory.AreEquivalent(mode, SyntaxFactory.ParseExpression("""
                request is ArchiveOrganizationCommand or SelectDefaultOrganizationCommand or AddManagedTenantLocaleCommand
                    or SetManagedDefaultTenantLocaleCommand or SetTenantLocaleEnabledCommand or RemoveTenantLocaleCommand
                    ? IdentityTenantLockMode.Exclusive : IdentityTenantLockMode.Shared
                """));
    }
}
