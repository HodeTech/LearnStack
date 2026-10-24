using System.Collections.Immutable;
using LearnStack.SharedKernel.Secrets;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace LearnStack.Analyzers;

/// <summary>
/// Flags sensitive named values in string arguments of directly thrown
/// exceptions (ADR-0032 Amendment 6). This bounded name guard does not trace
/// prior assignments or inspect literal contents and formatting-helper roots.
/// </summary>
/// <remarks>
/// LS0002 runs in all production projects and remains a Warning, including CI,
/// until Phase 03 exit. Both token sets and word matching come from the internal
/// source compiled into this assembly and SharedKernel; no runtime reference is
/// needed. Diagnostic messages never render an expression or its value.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SensitiveExceptionMessageAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "LS0002";

    /// <summary>The human-readable rule name (ADR-0032 Amendment 6).</summary>
    public const string RuleName = "LearnStackException-SensitiveExceptionMessage";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Avoid sensitive values in exception messages (LearnStackException-SensitiveExceptionMessage)",
        messageFormat: "Use a fixed exception message without sensitive values; keep sensitive data out of exception constructor strings",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ADR-0032 Amendment 6 forbids sensitive named values in directly thrown exception strings. "
            + "Use a fixed message and the existing redaction and closed diagnostic boundaries.",
        helpLinkUri: "https://github.com/HodeTech/LearnStack/blob/main/docs/decisions/0032-exception-handling-logging-and-observability.md#amendment-6--sensitive-exception-message-diagnostic-2026-10-25");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        if (context is null) throw new System.ArgumentNullException(nameof(context));
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var exceptionType = start.Compilation.GetTypeByMetadataName("System.Exception");
            if (exceptionType is null) return;
            start.RegisterOperationAction(operation => AnalyzeThrow(operation, exceptionType), OperationKind.Throw);
        });
    }

    private static void AnalyzeThrow(OperationAnalysisContext context, INamedTypeSymbol exceptionType)
    {
        var thrown = (IThrowOperation)context.Operation;
        if (Unwrap(thrown.Exception) is not IObjectCreationOperation creation
            || !IsException(creation.Type, exceptionType))
        {
            return;
        }

        foreach (var argument in creation.Arguments)
        {
            var value = Unwrap(argument.Value);
            if (value is not null && IsStringValue(argument.Value)
                && IsSupportedRoot(value)
                && ContainsSensitiveReference(value))
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, creation.Syntax.GetLocation()));
                return;
            }
        }
    }

    private static bool IsException(ITypeSymbol? type, INamedTypeSymbol exceptionType)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, exceptionType)) return true;
        }

        return false;
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    break;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    break;
                default:
                    return operation;
            }
        }
    }

    private static bool IsStringValue(IOperation operation)
    {
        // A string may be converted to object for a provider-specific constructor,
        // or an object value may be explicitly cast to its string message type.
        while (true)
        {
            if (operation.Type?.SpecialType == SpecialType.System_String) return true;
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    break;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    break;
                default:
                    return false;
            }
        }
    }

    private static bool IsSupportedRoot(IOperation operation) => operation is
        ILocalReferenceOperation or IParameterReferenceOperation or IFieldReferenceOperation
        or IPropertyReferenceOperation or IInterpolatedStringOperation
        || operation is IBinaryOperation
        {
            OperatorKind: BinaryOperatorKind.Add,
            OperatorMethod: null,
            Type.SpecialType: SpecialType.System_String,
        };

    private static bool ContainsSensitiveReference(IOperation operation)
    {
        // These operands name values or deferred bodies without evaluating them.
        if (operation is INameOfOperation or ITypeOfOperation or ILiteralOperation
            or IAnonymousFunctionOperation or ILocalFunctionOperation)
        {
            return false;
        }

        var name = operation switch
        {
            ILocalReferenceOperation local => local.Local.Name,
            IParameterReferenceOperation parameter => parameter.Parameter.Name,
            IFieldReferenceOperation field => field.Field.Name,
            IPropertyReferenceOperation property => property.Property.Name,
            _ => null,
        };

        if (name is not null && SensitiveTokenMatcher.IsSensitive(name)) return true;

        foreach (var child in operation.ChildOperations)
        {
            if (ContainsSensitiveReference(child)) return true;
        }

        return false;
    }
}
