using FluentAssertions;
using Xunit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuditConventionTests
{
    private const string InvitationTransportSqlPath = "LearnStack.Infrastructure/Email/InvitationSmtpProviderDataSource.cs";
    private static readonly string[] InvitationTransportCallArguments =
        ["(claim,receiptId,evidenceId,state,false,deadline.Token)", "(claim,receiptId,evidenceId,state,true,deadline.Token)"];

    // This private Npgsql transport helper happens to share EF's ExecuteSql
    // prefix. Remove only its exact declaration and two local calls from the
    // textual sweep; any EF API inside this very file remains an offender.
    private static bool OnlyInvitationTransportSqlHelper(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.ContainsDiagnostics) return false;
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == "ExecuteSqlAsync").ToArray();
        if (methods.Length != 1 || methods[0] is not
            {
                Parent: ClassDeclarationSyntax
                {
                    Identifier.ValueText: "Owner",
                    Parent: ClassDeclarationSyntax { Identifier.ValueText: "InvitationSmtpProviderDataSource" }
                }
            } method
            || method.Modifiers.ToString() != "private async" || method.ReturnType.ToString() != "Task<ProviderRecord>"
            || !SyntaxFactory.AreEquivalent(method.ParameterList, SyntaxFactory.ParseParameterList(
                "(Guid claim, Guid receiptId, Guid evidenceId, string? state, bool verify, CancellationToken token)"))) return false;
        var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression is IdentifierNameSyntax { Identifier.ValueText: "ExecuteSqlAsync" }).ToArray();
        if (calls.Length != 2 || calls.Any(call => call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText != "CallAsync")
            || !calls.Select(call => SourceText.WithoutWhitespace(call.ArgumentList.ToString())).Order(StringComparer.Ordinal)
                .SequenceEqual(InvitationTransportCallArguments.Order(StringComparer.Ordinal), StringComparer.Ordinal)) return false;
        var tokens = calls.Select(call => ((IdentifierNameSyntax)call.Expression).Identifier).Append(method.Identifier);
        foreach (var token in tokens.OrderByDescending(token => token.SpanStart))
            source = source.Remove(token.SpanStart, token.Span.Length);
        var code = SourceText.WithoutWhitespace(SourceText.WithoutComments(source));
        return !SetBasedWriteApis.Any(api => code.Contains(api, StringComparison.Ordinal));
    }

    [Fact]
    public void Invitation_transport_helper_does_not_exempt_set_based_Ef_writes()
    {
        var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, InvitationTransportSqlPath));
        OnlyInvitationTransportSqlHelper(source).Should().BeTrue();
        foreach (var replacement in new[] { "rows.ExecuteSqlRawAsync();", "rows.ExecuteUpdateAsync();", "rows.ExecuteDeleteAsync();" })
            OnlyInvitationTransportSqlHelper(source.Replace("_commitIssued = false;", replacement, StringComparison.Ordinal))
                .Should().BeFalse("the transport file is subject to the same EF write sweep");
        OnlyInvitationTransportSqlHelper(source.Replace("ExecuteSqlAsync(claim, receiptId, evidenceId, state, true, deadline.Token)",
            "ExecuteSqlAsync(claim, receiptId, evidenceId, state, false, deadline.Token)", StringComparison.Ordinal)).Should().BeFalse();
        OnlyInvitationTransportSqlHelper(source.Replace("private async Task<ProviderRecord> ExecuteSqlAsync", "public async Task<ProviderRecord> ExecuteSqlAsync",
            StringComparison.Ordinal)).Should().BeFalse();
        OnlyInvitationTransportSqlHelper(source.Replace("AddBinding(command);", "ExecuteSqlAsync(claim, receiptId, evidenceId, state, false, token);",
            StringComparison.Ordinal)).Should().BeFalse("a third local call cannot borrow the two exact transport paths");
    }
}
