using FluentAssertions;
using LearnStack.Api.Tenancy;
using Microsoft.AspNetCore.Mvc;
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
    // field constants, IL and attribute arguments covers declarations, aliases
    // and MVC metadata binding without source-level identifier matching.
    private static string[] AssertionNames(TypeDefinition owner, string prefix) =>
        AssertionLiterals(owner).Where(value => value.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string[] AssertionCounterNames(TypeDefinition owner)
    {
        var types = AssertionTypes(owner).ToArray();
        var declarations = types.SelectMany(type => type.Fields).Where(field => field.HasConstant
                && field.Name.EndsWith("CounterName", StringComparison.Ordinal))
            .Select(field => field.Constant).OfType<string>();
        var created = types.SelectMany(type => type.Methods).Where(method => method.HasBody).SelectMany(method => method.Body.Instructions
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
            .Concat(AssertionAttributeProviders(type).SelectMany(provider => provider.CustomAttributes)
                .SelectMany(attribute => attribute.ConstructorArguments
                    .Concat(attribute.Fields.Select(argument => argument.Argument))
                    .Concat(attribute.Properties.Select(argument => argument.Argument)))
                .SelectMany(AssertionAttributeLiterals))
            .Concat(type.NestedTypes.SelectMany(AssertionLiterals));

    private static IEnumerable<TypeDefinition> AssertionTypes(TypeDefinition type) =>
        new[] { type }.Concat(type.NestedTypes.SelectMany(AssertionTypes));

    private static IEnumerable<ICustomAttributeProvider> AssertionAttributeProviders(TypeDefinition type)
    {
        yield return type;
        foreach (var parameter in type.GenericParameters) yield return parameter;
        foreach (var field in type.Fields) yield return field;
        foreach (var property in type.Properties) yield return property;
        foreach (var @event in type.Events) yield return @event;
        foreach (var method in type.Methods)
        {
            yield return method;
            yield return method.MethodReturnType;
            foreach (var parameter in method.Parameters) yield return parameter;
            foreach (var parameter in method.GenericParameters) yield return parameter;
        }
    }

    private static IEnumerable<string> AssertionAttributeLiterals(CustomAttributeArgument argument) => argument.Value switch
    {
        string literal => [literal],
        CustomAttributeArgument nested => AssertionAttributeLiterals(nested),
        CustomAttributeArgument[] items => items.SelectMany(AssertionAttributeLiterals),
        _ => [],
    };

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

    private static void WithCompiledAssertionHeader(ModuleDefinition target, bool property, Action assertion)
    {
        using var compiled = ModuleDefinition.ReadModule(typeof(TenancyConventionTests).Assembly.Location);
        var probe = compiled.GetType(typeof(TenancyConventionTests).FullName).NestedTypes
            .Single(type => type.Name == nameof(AssertionMetadataProbe));
        var attributes = property ? probe.Properties.Single().CustomAttributes
            : probe.Methods.Single(method => method.Name == nameof(AssertionMetadataProbe.Bind)).Parameters.Single().CustomAttributes;
        var source = attributes.Single(attribute => attribute.AttributeType.FullName == typeof(FromHeaderAttribute).FullName);
        var attribute = new CustomAttribute(target.ImportReference(source.Constructor));
        foreach (var argument in source.Properties)
            attribute.Properties.Add(new CustomAttributeNamedArgument(argument.Name,
                new CustomAttributeArgument(target.ImportReference(argument.Argument.Type), argument.Argument.Value)));
        var type = new TypeDefinition("", "AssertionOwnershipProbe", TypeAttributes.NotPublic, target.TypeSystem.Object);
        if (property)
        {
            var bound = new PropertyDefinition("Bound", PropertyAttributes.None, target.TypeSystem.String);
            bound.CustomAttributes.Add(attribute);
            type.Properties.Add(bound);
        }
        else
        {
            var method = new MethodDefinition("Bind", MethodAttributes.Private | MethodAttributes.Static, target.TypeSystem.Void);
            var parameter = new ParameterDefinition("bound", ParameterAttributes.None, target.TypeSystem.String);
            parameter.CustomAttributes.Add(attribute);
            method.Parameters.Add(parameter);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(method);
        }
        target.Types.Add(type);
        try { assertion(); }
        finally { target.Types.Remove(type); }
    }

    private static void AssertAssertionHeaderNames(TypeDefinition owner) => AssertionNames(owner, "X-").Should()
        .BeEquivalentTo(["X-Tenant-Id", "X-Organization-Id"],
            "Fix: preserve both canonical assertion headers from ADR-0036, not merely two arbitrary names");

    private static void AssertAssertionCounterNames(TypeDefinition owner) => AssertionCounterNames(owner).Should()
        .BeEquivalentTo(["learnstack_tenant_assertion_mismatch_total", "learnstack_tenant_assertion_unresolved_total",
            "learnstack_tenant_assertion_audit_suppressed_total"],
            "Fix: retain the complete ADR-0036/0065 assertion counter vocabulary and its sole-writer protection");

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

    private sealed class AssertionMetadataProbe
    {
        [FromHeader(Name = "x-organization-id")]
        public string? Organization { get; set; }

        internal static void Bind([FromHeader(Name = "X-Tenant-Id")] string tenant) { }
    }
}
