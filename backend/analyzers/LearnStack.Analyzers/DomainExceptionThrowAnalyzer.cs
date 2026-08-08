using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace LearnStack.Analyzers;

/// <summary>
/// Rule "LearnStackException-DomainExceptionThrow" (Roslyn diagnostic id
/// <see cref="DiagnosticId"/> = <c>LS0001</c>) — flags every
/// <c>throw new DomainException(...)</c> in Domain / Application code per
/// ADR-0032 § Sub-decision 4. Expected business-rule violations return
/// <c>Result.Fail(business_rule_violation, ...)</c>; the exception is
/// reserved for programmer errors.
/// </summary>
/// <remarks>
/// <para>
/// The Roslyn diagnostic id MUST be a valid identifier (letters/digits, no
/// hyphens) — Roslyn throws <c>AD0001</c> at report time otherwise. The
/// human-readable rule name <c>LearnStackException-DomainExceptionThrow</c>
/// (ADR-0032 / Standards 21 naming convention) is carried in the title and
/// help text; the wire-level id is <c>LS0001</c>. See ADR-0032 Amendment 1.
/// </para>
/// <para>
/// Severity: <see cref="DiagnosticSeverity.Warning"/> in Phase 02a. Per
/// ADR-0032 the severity escalates to <see cref="DiagnosticSeverity.Error"/>
/// after Phase 03 exit when every existing call site has been migrated.
/// Until then <c>LS0001</c> is listed in <c>WarningsNotAsErrors</c>
/// (Directory.Build.props) so a legitimate aggregate-invariant throw does
/// not break CI under <c>TreatWarningsAsErrors</c> before the documented
/// escalation point.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DomainExceptionThrowAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "LS0001";

    /// <summary>The human-readable rule name (ADR-0032 / Standards 21).</summary>
    public const string RuleName = "LearnStackException-DomainExceptionThrow";

    private static readonly LocalizableString Title =
        "Avoid throwing DomainException for expected business-rule violations (LearnStackException-DomainExceptionThrow)";

    private static readonly LocalizableString MessageFormat =
        "DomainException is reserved for programmer errors. " +
        "Return Result.Fail(business_rule_violation, ...) instead.";

    private static readonly LocalizableString Description =
        "ADR-0032 § Sub-decision 4 reserves DomainException for aggregate-invariant " +
        "violations that signal a programming mistake. Expected business-rule " +
        "violations are an outcome — return Result.Fail(business_rule_violation, ...) " +
        "from the domain method.";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: "Design",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description,
        helpLinkUri: "https://github.com/cemililik/LearnStack/blob/main/docs/decisions/0032-exception-handling-logging-and-observability.md");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        if (context is null) throw new System.ArgumentNullException(nameof(context));
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeThrowExpression, SyntaxKind.ThrowExpression);
        context.RegisterSyntaxNodeAction(AnalyzeThrowStatement, SyntaxKind.ThrowStatement);
    }

    private static void AnalyzeThrowStatement(SyntaxNodeAnalysisContext context)
    {
        var node = (ThrowStatementSyntax)context.Node;
        if (node.Expression is null) return;
        InspectThrown(context, node.Expression);
    }

    private static void AnalyzeThrowExpression(SyntaxNodeAnalysisContext context)
    {
        var node = (ThrowExpressionSyntax)context.Node;
        InspectThrown(context, node.Expression);
    }

    private static void InspectThrown(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        if (expression is not ObjectCreationExpressionSyntax creation)
        {
            return;
        }

        var typeInfo = context.SemanticModel.GetTypeInfo(creation, context.CancellationToken);
        var symbol = typeInfo.Type;
        if (symbol is null)
        {
            return;
        }

        if (!IsDomainException(symbol))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
    }

    private static bool IsDomainException(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.Name == "DomainException" &&
                current.ContainingNamespace?.ToDisplayString() == "LearnStack.SharedKernel.Errors")
            {
                return true;
            }
        }

        return false;
    }
}
