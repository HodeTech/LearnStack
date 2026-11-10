using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LearnStack.Tests.Architecture;

public sealed partial class OperationPolicyOwnershipTests
{
    private const string ConsentHttpResponse = """
        policy.Kind == OperationPolicyKind.OwnConsentEntry
            ? http.Features.Get<ConsentSubjectSource>() is { } consent && consent.IsCurrent(http)
                && consent.EffectiveRequestType is { } consentType
                ? ProtectedAdministrationDescriptor.ForRequest(policy.RequestType) is not null
                    ? await ProtectedReplayHttpAdmission.AuthorizeAsync(http, policy, classifier,
                        tenantContext, store, candidates, operations)
                    : await operations().CheckHttpAsync(consentType, http.GetEndpoint()!, http.RequestAborted)
                : LearnStack.SharedKernel.Results.Result.Fail<LearnStack.SharedKernel.Results.None>(TenantContextFactory.Refused)
            : policy.Kind == OperationPolicyKind.InvitationEntry
                ? http.Features.Get<InvitationEntrySource>() is { } invitation && invitation.IsCurrent(http)
                    && invitation.EffectiveRequestType is { } requestType
                    ? await operations().CheckHttpAsync(requestType, http.GetEndpoint()!, http.RequestAborted)
                    : LearnStack.SharedKernel.Results.Result.Fail<LearnStack.SharedKernel.Results.None>(TenantContextFactory.Refused)
                : await ProtectedReplayHttpAdmission.AuthorizeAsync(http, policy, classifier,
                    tenantContext, store, candidates, operations)
        """;

    private static void AssertConsentHttpAuthorization()
    {
        var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, "LearnStack.Api/Authentication/OperationAuthorizationPolicyProvider.cs"));
        ConsentHttpAuthorizationIsConfined(source).Should().BeTrue();
        foreach (var (before, after) in new[]
        {
            ("consent.IsCurrent(http)", "consent.IsCurrent(new DefaultHttpContext())"),
            ("consent.EffectiveRequestType", "policy.RequestType"),
            ("CheckHttpAsync(consentType,", "CheckHttpAsync(policy.RequestType,"),
            ("http.GetEndpoint()!", "new object()"),
            ("http.RequestAborted", "CancellationToken.None"),
            ("ProtectedAdministrationDescriptor.ForRequest(policy.RequestType)", "ProtectedAdministrationDescriptor.ForRequest(consentType)"),
        })
        {
            var dirty = source.Replace(before, after, StringComparison.Ordinal);
            dirty.Should().NotBe(source);
            ConsentHttpAuthorizationIsConfined(dirty).Should().BeFalse("only the captured current consent request reaches the same HTTP endpoint and cancellation owner");
        }
        var replay = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, "LearnStack.Api/Idempotency/ProtectedReplayHttpAdmission.cs"));
        ReplayHttpAuthorizationCallsAreConfined(replay).Should().BeTrue();
        foreach (var (before, after) in new[]
        {
            ("CheckHttpAsync(declared.RequestType,", "CheckHttpAsync(requestType,"),
            ("CheckHttpAsync(requestType,", "CheckHttpAsync(declared.RequestType,"),
            ("endpoint!, http.RequestAborted", "new object(), http.RequestAborted"),
            ("endpoint!, http.RequestAborted", "endpoint!, CancellationToken.None"),
        })
        {
            var dirty = replay.Replace(before, after, StringComparison.Ordinal);
            dirty.Should().NotBe(replay);
            ReplayHttpAuthorizationCallsAreConfined(dirty).Should().BeFalse("fallback admission cannot become selected-receipt authority or change HTTP owner");
        }
    }

    private static bool ConsentHttpAuthorizationIsConfined(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.ContainsDiagnostics) return false;
        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().SingleOrDefault(node => node.Identifier.ValueText == "OperationAuthorizationHandler");
        var method = type?.Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(node => node.Identifier.ValueText == "HandleAsync");
        var response = method?.DescendantNodes().OfType<VariableDeclaratorSyntax>().SingleOrDefault(node => node.Identifier.ValueText == "response")?.Initializer?.Value;
        return response is not null && SyntaxFactory.AreEquivalent(response, SyntaxFactory.ParseExpression(ConsentHttpResponse))
            && method!.DescendantNodes().OfType<InvocationExpressionSyntax>().Count(node => node.Expression.ToString() == "operations().CheckHttpAsync") == 2;
    }

    private static bool ReplayHttpAuthorizationCallsAreConfined(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.ContainsDiagnostics) return false;
        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().SingleOrDefault(node => node.Identifier.ValueText == "ProtectedReplayHttpAdmission");
        var method = type?.Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(node => node.Identifier.ValueText == "AuthorizeAsync");
        if (method is null) return false;
        var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(node => node.Expression.ToString() == "operations().CheckHttpAsync").ToArray();
        return calls.Length == 4 && calls.Count(node => SyntaxFactory.AreEquivalent(node,
                SyntaxFactory.ParseExpression("operations().CheckHttpAsync(declared.RequestType, endpoint!, http.RequestAborted)"))) == 3
            && calls.Count(node => SyntaxFactory.AreEquivalent(node,
                SyntaxFactory.ParseExpression("operations().CheckHttpAsync(requestType, endpoint!, http.RequestAborted)"))) == 1;
    }
}
