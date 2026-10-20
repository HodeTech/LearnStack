using LearnStack.Api.Tenancy;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class TenancyConventionTests
{
    private const string AssertionMetricOwner = "LearnStack.Api.Tenancy.LoggingTenantAssertionRecorder";
    private const string AssertionAuditOwner = "LearnStack.Api.Tenancy.AuditingTenantAssertionRecorder";
    private const string AssertionHeaderOwner = "LearnStack.Api.Tenancy.TenantAssertionMiddleware";
    private const string AssertionCatalogueOwner = "LearnStack.Modules.Tenancy.Application.Audit.TenancyAuditCatalogSource";

    // Const references are inlined into ldstr at the consumer. Reading both
    // field constants and IL therefore covers declarations, aliases and every
    // compiled spelling without relying on source-level identifier matching.
    private static string[] AssertionNames(TypeDefinition owner, string prefix) =>
        AssertionLiterals(owner).Where(value => value.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string[] AssertionCounterNames(TypeDefinition owner)
    {
        var declarations = owner.Fields.Where(field => field.HasConstant
                && field.Name.EndsWith("CounterName", StringComparison.Ordinal))
            .Select(field => field.Constant).OfType<string>();
        var created = owner.Methods.Where(method => method.HasBody).SelectMany(method => method.Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference target && target.Name == "CreateCounter"
                && target.DeclaringType.FullName == "System.Diagnostics.Metrics.Meter")
            .Select(instruction => EndpointComposition.ArgumentProducer(method, instruction, 0) is
            { OpCode.Code: Code.Ldstr, Operand: string literal } ? literal
                : throw new InvalidOperationException("Assertion counter creation must retain a verifiable constant name.")));
        return declarations.Concat(created).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> AssertionLiterals(TypeDefinition type) =>
        type.Fields.Where(field => field.HasConstant).Select(field => field.Constant).OfType<string>()
            .Concat(type.Methods.Where(method => method.HasBody).SelectMany(method => method.Body.Instructions)
                .Where(instruction => instruction.OpCode == OpCodes.Ldstr).Select(instruction => instruction.Operand).OfType<string>())
            .Concat(type.NestedTypes.SelectMany(AssertionLiterals));

    private static string[] AssertionNameOffenders(IEnumerable<ModuleDefinition> modules, string[] names,
        string[] exemptOwners, StringComparer? comparer = null) => modules.SelectMany(module => module.Types)
        .Where(type => !exemptOwners.Any(owner => type.FullName == owner && type.Module.Assembly.Name.Name ==
            (owner == AssertionCatalogueOwner ? "LearnStack.Modules.Tenancy.Application" : "LearnStack.Api")))
        .SelectMany(type => AssertionLiterals(type).Where(literal => names.Contains(literal, comparer ?? StringComparer.Ordinal))
            .Distinct(comparer ?? StringComparer.Ordinal).Select(literal => type.FullName + ": " + literal))
        .Order(StringComparer.Ordinal).ToArray();

    private static void WithCompiledAssertionLiteral(ModuleDefinition target, string probe, Action assertion)
    {
        using var compiled = ModuleDefinition.ReadModule(typeof(TenancyConventionTests).Assembly.Location);
        var method = compiled.GetType(typeof(TenancyConventionTests).FullName).NestedTypes
            .Single(type => type.Name == nameof(AssertionLiteralProbe)).Methods.Single(method => method.Name == probe);
        var literal = method.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldstr).Operand as string
            ?? throw new InvalidOperationException("A compiled const-reference control must emit its actual string literal.");
        WithAssertionLiteral(target, literal, assertion);
    }

    private static void WithAssertionLiteral(ModuleDefinition target, string literal, Action assertion)
    {
        var type = new TypeDefinition("", "AssertionOwnershipProbe", TypeAttributes.NotPublic, target.TypeSystem.Object);
        var method = new MethodDefinition("Read", MethodAttributes.Private | MethodAttributes.Static, target.TypeSystem.String);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, literal));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        target.Types.Add(type);
        try { assertion(); }
        finally { target.Types.Remove(type); }
    }

    private static void WithAssertionProduction(Action<ModuleDefinition[]> assertion)
    {
        var modules = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        try { assertion(modules); }
        finally { foreach (var module in modules) module.Dispose(); }
    }

    private static class AssertionLiteralProbe
    {
        internal static string MismatchCounter() => LoggingTenantAssertionRecorder.MismatchCounterName;
        internal static string UnresolvedCounter() => LoggingTenantAssertionRecorder.UnresolvedCounterName;
        internal static string SuppressionCounter() => LoggingTenantAssertionRecorder.SuppressedCounterName;
        internal static string RejectOperation() => AuditingTenantAssertionRecorder.RejectOperation;
        internal static string BurstOperation() => AuditingTenantAssertionRecorder.BurstOperation;
        internal static string TenantHeader() => TenantAssertionMiddleware.TenantHeaderName;
        internal static string OrganizationHeader() => TenantAssertionMiddleware.OrganizationHeaderName;
        internal static string LowercaseTenantHeader() => "x-tenant-id";
        internal static string LowercaseOrganizationHeader() => "x-organization-id";
    }
}
