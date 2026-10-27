using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Permissions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LearnStack.Tests.Architecture;

public sealed partial class PersistenceConventionTests
{
    private const string ReadScopeContextFile = "LearnStack.Infrastructure/Persistence/TenantQueryFilters.cs";
    private const string ReadScopeRegistrationFile = "LearnStack.Infrastructure/Persistence/ModuleDbContextRegistration.cs";

    private static void AssertOperationReadScopeAttachment()
    {
        var view = typeof(IOperationReadScopeView).GetProperties().Should().ContainSingle().Which;
        view.Name.Should().Be(nameof(IOperationReadScopeView.IsTenantWideRead));
        view.PropertyType.Should().Be<bool>();
        view.SetMethod.Should().BeNull();
        var parameter = typeof(TenantScopedDbContext).GetProperty(nameof(TenantScopedDbContext.CurrentTenantWideRead))!;
        parameter.PropertyType.Should().Be<bool>();
        parameter.SetMethod.Should().BeNull();
        (parameter.GetMethod!.IsVirtual && !parameter.GetMethod.IsFinal).Should().BeFalse(
            "a module cannot override the live-owner filter carrier, including a final interface implementation");
        SourceScan.FilesContaining(SourceScan.SourceRoot, "AttachOperationReadScope", except: null)
            .Should().BeEquivalentTo([ReadScopeContextFile, ReadScopeRegistrationFile],
                "only the base declaration and common registration may name the attachment seam");
        var context = SourceText.WithoutComments(File.ReadAllText(Path.Combine(SourceScan.SourceRoot, ReadScopeContextFile)));
        var registration = SourceText.WithoutComments(File.ReadAllText(Path.Combine(SourceScan.SourceRoot, ReadScopeRegistrationFile)));
        OperationReadAttachmentIsClosed(context, registration).Should().BeTrue();
        foreach (var dirty in new[]
        {
            context.Replace("_operationReadScope?.IsTenantWideRead == true", "true", StringComparison.Ordinal),
            context.Replace("internal void AttachOperationReadScope", "public void AttachOperationReadScope", StringComparison.Ordinal),
            context.Replace("if (_operationReadScope is not null)", "if (false)", StringComparison.Ordinal),
        })
        {
            dirty.Should().NotBe(context);
            OperationReadAttachmentIsClosed(dirty, registration).Should().BeFalse("the attachment is internal, one-shot and read from its actual owner");
        }
        foreach (var dirty in new[]
        {
            registration.Replace("scoped.AttachOperationReadScope(readScope);", "", StringComparison.Ordinal),
            registration.Replace("unitOfWork is IOperationReadScopeView readScope",
                "provider.GetRequiredService<IOperationReadScopeView>() is IOperationReadScopeView readScope", StringComparison.Ordinal),
            registration + "\ninternal class ForeignReadAttachment { void Bind(TenantScopedDbContext context, IOperationReadScopeView scope) { context.AttachOperationReadScope(scope); } }",
        })
        {
            dirty.Should().NotBe(registration);
            OperationReadAttachmentIsClosed(context, dirty).Should().BeFalse("the existing factory attaches only its own unit's live view");
        }
    }

    private static bool OperationReadAttachmentIsClosed(string context, string registration)
    {
        var syntax = CSharpSyntaxTree.ParseText(context).GetRoot();
        var contexts = syntax.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(type => type.Identifier.ValueText == nameof(TenantScopedDbContext)).ToArray();
        if (syntax.ContainsDiagnostics || contexts.Length != 1) return false;
        var methods = contexts[0].Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == "AttachOperationReadScope").ToArray();
        var parameters = contexts[0].Members.OfType<PropertyDeclarationSyntax>()
            .Where(property => property.Identifier.ValueText == nameof(TenantScopedDbContext.CurrentTenantWideRead)).ToArray();
        if (methods.Length != 1 || methods[0].Modifiers.Count != 1 || !methods[0].Modifiers.Any(SyntaxKind.InternalKeyword)
            || !SyntaxFactory.AreEquivalent(methods[0].ParameterList, SyntaxFactory.ParseParameterList("(IOperationReadScopeView scope)"))
            || !SyntaxFactory.AreEquivalent(methods[0].Body, SyntaxFactory.ParseStatement("""
                { ArgumentNullException.ThrowIfNull(scope);
                  if (_operationReadScope is not null)
                    throw new InvalidOperationException("The context's operation read owner is already attached.");
                  _operationReadScope = scope; }
                """))
            || parameters.Length != 1 || parameters[0].AccessorList is not null || parameters[0].ExpressionBody is not { } expression
            || !SyntaxFactory.AreEquivalent(expression.Expression, SyntaxFactory.ParseExpression("_operationReadScope?.IsTenantWideRead == true")))
            return false;
        var fields = contexts[0].Members.OfType<FieldDeclarationSyntax>().Where(field =>
            field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "_operationReadScope")).ToArray();
        if (fields.Length != 1 || fields[0].Modifiers.Count != 1 || !fields[0].Modifiers.Any(SyntaxKind.PrivateKeyword)
            || fields[0].Declaration.Type.ToString() != "IOperationReadScopeView?"
            || contexts[0].DescendantNodes().OfType<IdentifierNameSyntax>().Count(name => name.Identifier.ValueText == "_operationReadScope") != 3)
            return false;
        var registry = CSharpSyntaxTree.ParseText(registration).GetRoot();
        var attachments = registry.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
            call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AttachOperationReadScope" }).ToArray();
        if (registry.ContainsDiagnostics || attachments.Length != 1
            || attachments[0].Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText != "AddModuleDbContext"
            || attachments[0].Parent?.Parent is not IfStatementSyntax guard
            || !SyntaxFactory.AreEquivalent(guard, SyntaxFactory.ParseStatement("""
                if (context is TenantScopedDbContext scoped && unitOfWork is IOperationReadScopeView readScope)
                    scoped.AttachOperationReadScope(readScope);
                """))) return false;
        var enrollments = guard.Parent!.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
            call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "UseTransaction" }).ToArray();
        return enrollments.Length == 1 && guard.Span.End < enrollments[0].SpanStart;
    }
}
