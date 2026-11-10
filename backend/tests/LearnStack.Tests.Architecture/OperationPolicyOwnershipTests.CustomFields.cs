using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LearnStack.Tests.Architecture;

public sealed partial class OperationPolicyOwnershipTests
{
    private const string DefinitionEvaluator = "LearnStack.Modules.Customization.Application.CustomFields.CustomFieldDefinitionAdmission";
    private const string OwnConsentEvaluator = "LearnStack.Modules.Tenancy.Application.FieldConsents.OwnFieldConsentAuthorization";
    private const string ConsentReceiptEvaluator = "LearnStack.Modules.Tenancy.Application.FieldConsents.ReadOwnGrantedFieldConsentReceiptQueryHandler";
    private const string ConsentReadEvaluator = "LearnStack.Modules.Tenancy.Application.FieldConsents.GetOwnMembershipFieldConsentQueryHandler";
    private static readonly (string Type, string Method)[] CustomFieldRecheckSites =
    [
        (DefinitionEvaluator, "RecheckAsync"), (OwnConsentEvaluator, "AdmitGrantAsync"),
        (ConsentReceiptEvaluator, "RecheckAsync"), (ConsentReadEvaluator, "AuthorizedAsync"),
    ];

    private static void AssertCustomFieldCanonicalRechecks()
    {
        foreach (var (path, type, method, call) in new[]
        {
            ("Customization/LearnStack.Modules.Customization.Application/CustomFields/CustomFieldDefinitionAdmission.cs", "CustomFieldDefinitionAdmission", "RecheckAsync",
                "authorizer.AuthorizeAsync(invocation, invocation.Policy, invocation.Candidate!, token)"),
            ("Tenancy/LearnStack.Modules.Tenancy.Application/FieldConsents/OwnFieldConsentAuthorization.cs", "OwnFieldConsentAuthorization", "AdmitGrantAsync",
                "authorizer.AuthorizeAsync(invocation, invocation.Policy, invocation.Candidate!, cancellationToken)"),
            ("Tenancy/LearnStack.Modules.Tenancy.Application/FieldConsents/FieldConsentReceiptQueryHandlers.cs", "ReadOwnGrantedFieldConsentReceiptQueryHandler", "RecheckAsync",
                "authorizer.AuthorizeAsync(invocation, invocation.Policy, invocation.Candidate!, cancellationToken)"),
            ("Tenancy/LearnStack.Modules.Tenancy.Application/FieldConsents/GetOwnMembershipFieldConsentQueryHandler.cs", "GetOwnMembershipFieldConsentQueryHandler", "AuthorizedAsync",
                "authorizer.AuthorizeAsync(invocation, policy, candidate, cancellationToken)"),
        })
        {
            var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, "Modules/" + path));
            CustomFieldRecheckIsConfined(source, type, method, call).Should().BeTrue(type);
            var decision = type == "CustomFieldDefinitionAdmission" ? "decision" : "fresh";
            foreach (var (before, after) in new[]
            {
                (call, call.Replace("(invocation,", "(invocations.CurrentInvocation!,", StringComparison.Ordinal)),
                (call, call.Replace("invocation.Policy", "null!", StringComparison.Ordinal).Replace(", policy,", ", null!,", StringComparison.Ordinal)),
                (call, call.Replace("invocation.Candidate!", "null!", StringComparison.Ordinal).Replace(", candidate,", ", null!,", StringComparison.Ordinal)),
                ("ReferenceEquals(" + decision + ".Invocation, invocation)", "ReferenceEquals(" + decision + ".Invocation, invocations.CurrentInvocation)"),
            })
            {
                if (!source.Contains(before, StringComparison.Ordinal)) continue;
                var dirty = source.Replace(before, after, StringComparison.Ordinal);
                dirty.Should().NotBe(source);
                CustomFieldRecheckIsConfined(dirty, type, method, call).Should().BeFalse(type + " keeps its same-invocation recheck");
            }
        }
    }

    private static bool CustomFieldRecheckIsConfined(string source, string typeName, string methodName, string expected)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.ContainsDiagnostics) return false;
        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().SingleOrDefault(node => node.Identifier.ValueText == typeName);
        if (type is null) return false;
        var calls = type.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(node => node.Expression.ToString() == "authorizer.AuthorizeAsync").ToArray();
        if (calls.Length != 1 || calls[0].Ancestors().OfType<MethodDeclarationSyntax>().Single().Identifier.ValueText != methodName
            || !SyntaxFactory.AreEquivalent(calls[0], SyntaxFactory.ParseExpression(expected))) return false;
        var method = calls[0].Ancestors().OfType<MethodDeclarationSyntax>().Single();
        var decision = typeName == "CustomFieldDefinitionAdmission" ? "decision" : "fresh";
        var sameInvocation = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(node => node.Expression.ToString() == "ReferenceEquals" && node.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() == decision + ".Invocation")
            .ToArray();
        if (sameInvocation.Length != 1 || !SyntaxFactory.AreEquivalent(sameInvocation[0],
                SyntaxFactory.ParseExpression("ReferenceEquals(" + decision + ".Invocation, invocation)"))) return false;
        if (typeName != "GetOwnMembershipFieldConsentQueryHandler") return true;
        // The one local-alias form still obtains policy and candidate from the
        // canonical live invocation, never a submitted or reconstructed authority.
        return method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(node => SyntaxFactory.AreEquivalent(node,
                SyntaxFactory.ParseExpression("ReferenceEquals(policy, invocation.Policy)")))
            && method.DescendantNodes().OfType<IsPatternExpressionSyntax>().Any(node => SyntaxFactory.AreEquivalent(node,
                SyntaxFactory.ParseExpression("invocation.Candidate is not { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate")));
    }
}
