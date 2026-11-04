using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LearnStack.Tests.Architecture;

public sealed partial class PersistenceConventionTests
{
    private static void AssertInvitationEntryEnrollment()
    {
        var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
            "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Invitations/InvitationAcceptanceStore.cs"));
        InvitationEntryEnrollmentIsGuarded(source).Should().BeTrue();
        foreach (var before in new[] { "!ReferenceEquals(invocations.CurrentInvocation, witness.Invocation)",
            "!entry.IsInvitationEntryOwner(witness)", "context.Origin != TenantContextOrigin.HostAndInvitationOffer" })
        {
            var dirty = source.Replace(before, "false", StringComparison.Ordinal);
            dirty.Should().NotBe(source);
            InvitationEntryEnrollmentIsGuarded(dirty).Should().BeFalse();
        }
        InvitationEntryEnrollmentIsGuarded(source.Replace("IsActive: true", "IsActive: false", StringComparison.Ordinal)).Should().BeFalse();
        InvitationEntryEnrollmentIsGuarded(source.Replace("ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);", "return;", StringComparison.Ordinal)).Should().BeFalse();
    }
    private static bool InvitationEntryEnrollmentIsGuarded(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var methods = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(type => type.Identifier.ValueText == "InvitationAcceptanceReadStore")
            .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>()).Where(method => method.Identifier.ValueText == "Admit").ToArray();
        return !root.ContainsDiagnostics && methods.Length == 1 && methods[0].Modifiers.Count == 1
            && methods[0].Modifiers.Any(SyntaxKind.PrivateKeyword) && methods[0].ReturnType.ToString() == "void"
            && methods[0].ParameterList.Parameters.Count == 0 && methods[0].Body is { } body
            && SyntaxFactory.AreEquivalent(body, SyntaxFactory.ParseStatement("""
                {
                    if (invocations.CurrentDecision is not { InvitationWitness: { IsActive: true } witness }
                        || !ReferenceEquals(invocations.CurrentInvocation, witness.Invocation)
                        || unit is not IInvitationEntryUnitOfWork entry || !entry.IsInvitationEntryOwner(witness)
                        || context.Origin != TenantContextOrigin.HostAndInvitationOffer)
                        throw new InfrastructureException("Invitation storage requires its exact operation owner.");
                    ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);
                }
                """));
    }
}
