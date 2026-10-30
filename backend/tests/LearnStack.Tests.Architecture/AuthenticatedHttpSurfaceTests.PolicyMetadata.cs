using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticatedHttpSurfaceTests
{
    private const string PolicyType = "LearnStack.SharedKernel.Permissions.OperationPolicy";
    private const string CustomizationPolicies = "LearnStack.Modules.Customization.Application.Authorization.CustomizationOperationPolicySource";
    private const string CustomizationAudit = "LearnStack.Modules.Customization.Application.Audit.CustomizationAuditCatalogSource";

    // Exclude only literal handler type arguments in the two finite policy
    // registrations and their two existing Off audit declarations.
    // Replacing the instruction's MethodSpec, rather than its shared TypeReference,
    // preserves every field/constructor/local/other operand naming the same handler.
    private static TypeReference[] HandlerReferences(TypeDefinition type, Dictionary<string, TypeDefinition> map)
    {
        var sites = Il.Methods(type).Where(pair => pair.Definition.HasBody).SelectMany(pair =>
            pair.Definition.Body.Instructions.Where(instruction => instruction.Operand is GenericInstanceMethod generic
                && generic.GenericArguments.Select((_, index) => index).Any(index =>
                    IsHandlerTypeMetadata(type, pair.Definition, instruction, index, map)))
                .Select(instruction => (Instruction: instruction, Original: (GenericInstanceMethod)instruction.Operand,
                    Method: pair.Definition))).ToArray();
        var controllerTokens = Il.Methods(type).Where(pair => pair.Definition.HasBody).SelectMany(pair =>
            pair.Definition.Body.Instructions.Where(instruction => IsControllerTypeMetadata(type, pair.Definition, instruction))
                .Select(instruction => (Instruction: instruction, Original: instruction.Operand))).ToArray();
        try
        {
            foreach (var site in sites)
            {
                var original = site.Original;
                var metadata = new GenericInstanceMethod(original.ElementMethod);
                for (var index = 0; index < original.GenericArguments.Count; index++)
                    metadata.GenericArguments.Add(IsHandlerTypeMetadata(type, site.Method, site.Instruction, index, map)
                        ? type.Module.TypeSystem.Object : original.GenericArguments[index]);
                site.Instruction.Operand = metadata;
            }
            foreach (var token in controllerTokens) token.Instruction.Operand = type.Module.TypeSystem.Object;
            return HttpReferences(type, map).ToArray();
        }
        finally
        {
            foreach (var site in sites) site.Instruction.Operand = site.Original;
            foreach (var token in controllerTokens) token.Instruction.Operand = token.Original;
        }
    }

    private static bool IsHandlerTypeMetadata(TypeDefinition owner, MethodDefinition method, Instruction instruction,
        int argument, Dictionary<string, TypeDefinition> map)
    {
        if (argument == 0 && owner.FullName == CustomizationAudit && method.Name == "Describe"
            && instruction.OpCode == OpCodes.Callvirt && instruction.Operand is GenericInstanceMethod
            { Name: "Off", GenericArguments.Count: 1, Parameters.Count: 0 } off
            && Key(off.DeclaringType) == "LearnStack.SharedKernel:LearnStack.SharedKernel.Audit.IAuditCatalogBuilder"
            && Key(off.GenericArguments[0]) is "LearnStack.Modules.Customization.Application:LearnStack.Modules.Customization.Application.IntegrationEvents.InitializeContentTypeDefaultHandler"
                or "LearnStack.Modules.Customization.Application:LearnStack.Modules.Customization.Application.IntegrationEvents.InitializeTaxonomyDefaultHandler")
            return DataOnlyAuditRegistration(map);
        if (argument != 1 || owner.FullName != CustomizationPolicies || method.Name != "get_Policies"
            || instruction.OpCode != OpCodes.Call || instruction.Operand is not GenericInstanceMethod
            { Name: "ForEventInitializer", GenericArguments.Count: 2, Parameters.Count: 1 } factory
            || Key(factory.DeclaringType) != "LearnStack.SharedKernel:" + PolicyType
            || instruction.Previous is not { OpCode.Code: Code.Ldstr, Operand: string id }) return false;
        var prefix = "LearnStack.Modules.Customization.Application";
        var contract = "LearnStack.Modules.Customization.Application.Contracts:LearnStack.Modules.Customization.Application.Contracts.Customization.";
        var handler = prefix + ":" + prefix + ".IntegrationEvents.";
        var pair = (Key(factory.GenericArguments[0]), Key(factory.GenericArguments[1]), id);
        return (pair == (contract + "InitializeTenantContentTypeDefaultCommand", handler + "InitializeContentTypeDefaultHandler", "customization.default.initialize_content_type")
                || pair == (contract + "InitializeTenantLevelTaxonomyDefaultCommand", handler + "InitializeTaxonomyDefaultHandler", "customization.default.initialize_taxonomy"))
            && DataOnlyPolicyFactory(map);
    }

    private static bool DataOnlyAuditRegistration(Dictionary<string, TypeDefinition> map)
    {
        const string builderName = "LearnStack.Infrastructure.Audit.AuditCatalogBuilder";
        const string contract = "LearnStack.SharedKernel.Audit.IAuditCatalogBuilder";
        const string registration = builderName + "/Registration";
        if (!map.TryGetValue("LearnStack.Infrastructure.Audit:" + builderName, out var builder)
            || map.Values.Count(type => type.Interfaces.Any(item => item.InterfaceType.FullName == contract)) != 1) return false;
        var off = builder.Methods.SingleOrDefault(method => method.Name == "Off");
        if (off is not { HasBody: true, GenericParameters.Count: 1, Parameters.Count: 0 }) return false;
        var genericUses = off.Body.Instructions.Where(instruction => instruction.Operand is GenericParameter).ToArray();
        if (genericUses.Length != 1 || genericUses[0].OpCode != OpCodes.Ldtoken) return false;
        var bodies = new[] { off, builder.Methods.Single(method => method.Name == "Claim"),
            builder.Methods.Single(method => method.Name == "Source"),
            builder.NestedTypes.Single(type => type.Name == "Registration").Methods.Single(method => method.Name == ".ctor" && method.Parameters.Count == 2),
            builder.NestedTypes.Single(type => type.Name == "Registration").Methods.Single(method => method.Name == "get_Entries") };
        foreach (var body in bodies)
            foreach (var instruction in body.Body.Instructions.Where(instruction => instruction.Operand is MethodReference))
            {
                var target = (MethodReference)instruction.Operand;
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj)) return false;
                if ((target.DeclaringType.FullName, target.Name) is ("System.Type", "GetTypeFromHandle")
                    or ("System.Object", ".ctor") or ("System.InvalidOperationException", ".ctor")
                    or ("System.Reflection.MemberInfo", "get_Name") or ("System.String", "Equals") or ("System.String", "Concat")
                    or ("LearnStack.SharedKernel.Audit.IAuditCatalogSource", "get_ModuleName")) continue;
                if (target.DeclaringType.FullName == builderName && target.Name is "Source" or "Claim"
                    || target.DeclaringType.FullName == registration && target.Name is ".ctor" or "get_Entries"
                    || target.DeclaringType.FullName == "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler"
                        && target.Name is ".ctor" or "AppendFormatted" or "AppendLiteral" or "ToStringAndClear"
                    || target.DeclaringType.FullName == "System.Collections.Generic.List`1<LearnStack.SharedKernel.Audit.AuditCatalogEntry>"
                        && target.Name is ".ctor" or "get_Count"
                    || target.DeclaringType.FullName is "System.Collections.Generic.Dictionary`2<System.Type,LearnStack.Infrastructure.Audit.AuditCatalogBuilder/Registration>"
                        or "System.Collections.Generic.Dictionary`2<System.Type,System.String>" && target.Name is "TryGetValue" or "set_Item") continue;
                return false;
            }
        var moduleName = map.Values.Single(type => type.FullName == CustomizationAudit).Methods.Single(method => method.Name == "get_ModuleName");
        return moduleName.Body.Instructions.Count == 2 && moduleName.Body.Instructions[0].Operand as string == "customization"
            && moduleName.Body.Instructions[1].OpCode == OpCodes.Ret;
    }

    private static bool DataOnlyPolicyFactory(Dictionary<string, TypeDefinition> map)
    {
        if (!map.TryGetValue("LearnStack.SharedKernel:" + PolicyType, out var policy)) return false;
        var factories = policy.Methods.Where(method => method.Name == "ForEventInitializer").ToArray();
        var constructors = policy.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
        if (factories.Length != 1 || constructors.Length != 1) return false;
        var factory = factories[0];
        if (!factory.IsStatic || factory.GenericParameters.Count != 2 || factory.Parameters.Count != 1
            || factory.Parameters[0].ParameterType.FullName != "System.String" || factory.ReturnType.FullName != PolicyType
            || !constructors[0].IsPrivate || !factory.HasBody || !constructors[0].HasBody) return false;
        var tokens = factory.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldtoken)
            .Select(instruction => instruction.Operand).OfType<GenericParameter>().Select(parameter => parameter.Position).Order().ToArray();
        if (!tokens.SequenceEqual([0, 1])) return false;
        var factoryCalls = factory.Body.Instructions.Where(instruction => instruction.Operand is MethodReference).ToArray();
        if (factoryCalls.Length != 4 || factoryCalls.Any(instruction => instruction.Operand is not MethodReference target
                || !(instruction.OpCode == OpCodes.Call && target.DeclaringType.FullName == "System.Type" && target.Name == "GetTypeFromHandle"
                    || instruction.OpCode == OpCodes.Call && target.DeclaringType.FullName == "System.Runtime.InteropServices.ImmutableCollectionsMarshal"
                        && target.Name == "AsImmutableArray"
                    || instruction.OpCode == OpCodes.Newobj && target.FullName == constructors[0].FullName))) return false;
        return DataOnlyPolicyConstructor(constructors[0], map);
    }

    // The exemption is for two literal metadata registrations, never for OperationPolicy
    // as a whole. Follow only this constructor's clause comparisons and generated
    // predicates; every reached body, captured field and metadata getter stays data-only.
    private static bool DataOnlyPolicyConstructor(MethodDefinition constructor, Dictionary<string, TypeDefinition> map)
    {
        const string clause = "LearnStack.SharedKernel.Permissions.OperationPolicyClause";
        const string permission = "LearnStack.SharedKernel.Permissions.PermissionDefinition";
        const string source = "LearnStack.SharedKernel.Permissions.AuthorizationSourceKind";
        var pending = new Queue<MethodDefinition>();
        var visited = new HashSet<MethodDefinition>();
        pending.Enqueue(constructor);

        bool Closure(TypeReference type) => map.TryGetValue(Key(type), out var definition)
            && definition.DeclaringType?.FullName == PolicyType && definition.IsNestedPrivate && definition.IsSealed
            && definition.Name.StartsWith("<>c", StringComparison.Ordinal)
            && definition.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        bool Array(TypeReference type) => type is GenericInstanceType { GenericArguments.Count: 1 } generic
            && generic.ElementType.FullName == "System.Collections.Immutable.ImmutableArray`1"
            && generic.GenericArguments[0].FullName is clause or permission or source;
        bool Predicate(TypeReference type) => type is GenericInstanceType { GenericArguments.Count: 2 } generic
            && generic.ElementType.FullName == "System.Func`2" && generic.GenericArguments[1].FullName == "System.Boolean"
            && generic.GenericArguments[0].FullName is "System.Char" or clause or permission or source;
        bool Data(TypeReference type) => type.FullName is "System.Type" or "System.String" or "System.Boolean" or "System.Char"
            or clause or permission or source or "LearnStack.SharedKernel.Permissions.OperationPolicyKind"
            or "LearnStack.SharedKernel.Permissions.OperationReadScope" or "LearnStack.SharedKernel.Permissions.PermissionScope"
            || Array(type) || Predicate(type) || Closure(type);
        bool AddClosure(TypeReference reference)
        {
            var type = map[Key(reference)];
            if (type.Fields.Any(field => !Data(field.FieldType))) return false;
            foreach (var initializer in type.Methods.Where(method => method.IsConstructor && method.IsStatic)) pending.Enqueue(initializer);
            return true;
        }
        bool Getter(MethodReference reference)
        {
            if (!map.TryGetValue(Key(reference.DeclaringType), out var owner)
                || owner.Methods.SingleOrDefault(method => method.FullName == reference.FullName) is not { HasBody: true } getter) return false;
            var body = getter.Body.Instructions.Where(instruction => instruction.OpCode != OpCodes.Nop).ToArray();
            return body is [{ OpCode.Code: Code.Ldarg_0 }, { OpCode.Code: Code.Ldfld, Operand: FieldReference field }, { OpCode.Code: Code.Ret }]
                && field.DeclaringType.FullName == owner.FullName && Data(field.FieldType)
                && owner.Fields.Any(candidate => candidate.FullName == field.FullName && candidate.IsPrivate && candidate.IsInitOnly);
        }
        while (pending.TryDequeue(out var body))
        {
            if (!visited.Add(body)) continue;
            if (!body.HasBody) return false;
            foreach (var instruction in body.Body.Instructions)
            {
                if (instruction.OpCode.Code is Code.Calli or Code.Jmp or Code.Ldtoken or Code.Ldvirtftn) return false;
                if (instruction.Operand is FieldReference field)
                {
                    if (Closure(field.DeclaringType))
                    {
                        if (!AddClosure(field.DeclaringType) || !Data(field.FieldType)) return false;
                    }
                    else if (!(field.DeclaringType.FullName == PolicyType && Data(field.FieldType)
                            && constructor.DeclaringType.Fields.Any(candidate => candidate.FullName == field.FullName && candidate.IsPrivate && candidate.IsInitOnly))
                        && !(Array(field.DeclaringType) && field.Name == "Empty" && instruction.OpCode == OpCodes.Ldsfld)) return false;
                }
                if (instruction.Operand is not MethodReference target) continue;
                if (Closure(target.DeclaringType))
                {
                    if (!AddClosure(target.DeclaringType)
                        || map[Key(target.DeclaringType)].Methods.SingleOrDefault(method => method.FullName == target.FullName) is not { HasBody: true } helper
                        || !(helper.IsConstructor && helper.Parameters.Count == 0 && instruction.OpCode == OpCodes.Newobj
                            || instruction.OpCode == OpCodes.Ldftn && helper.ReturnType.FullName == "System.Boolean" && helper.Parameters.Count == 1
                                && helper.Parameters[0].ParameterType.FullName is "System.Char" or clause or permission or source
                                && (helper.Name.StartsWith("<.ctor>b__", StringComparison.Ordinal)
                                    || helper.Name.StartsWith("<SameRequirements>b__", StringComparison.Ordinal)))) return false;
                    pending.Enqueue(helper);
                    continue;
                }
                if (target.DeclaringType.FullName == PolicyType && target.Name == "SameRequirements")
                {
                    var comparison = constructor.DeclaringType.Methods.SingleOrDefault(method => method.FullName == target.FullName);
                    if (instruction.OpCode != OpCodes.Call || comparison is not { IsPrivate: true, IsStatic: true, Parameters.Count: 2 }
                        || comparison.ReturnType.FullName != "System.Boolean" || comparison.Parameters.Any(parameter => parameter.ParameterType.FullName != clause)) return false;
                    pending.Enqueue(comparison);
                    continue;
                }
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj)) return false;
                if ((target.DeclaringType.FullName, target.Name) is (PolicyType, "get_Alternatives")
                    or (clause, "get_RequiredPermissions") or (clause, "get_ReadScope")
                    or (permission, "get_Scope") or (permission, "get_Key"))
                {
                    if (!Getter(target)) return false;
                    continue;
                }
                if ((target.DeclaringType.FullName, target.Name) is ("System.Object", ".ctor")
                    or ("System.ArgumentNullException", "ThrowIfNull") or ("System.ArgumentException", "ThrowIfNullOrWhiteSpace")
                    or ("System.ArgumentException", ".ctor") or ("System.Type", "get_IsAbstract") or ("System.Type", "get_ContainsGenericParameters")
                    or ("System.String", "get_Length") or ("System.String", "op_Equality")) continue;
                if (target is GenericInstanceMethod generic && generic.GenericArguments.Count == 1)
                {
                    var argument = generic.GenericArguments[0].FullName;
                    if (target.DeclaringType.FullName == "System.Enum" && target.Name == "IsDefined"
                        && argument is "LearnStack.SharedKernel.Permissions.OperationPolicyKind" or "LearnStack.SharedKernel.Permissions.OperationReadScope") continue;
                    if (target.DeclaringType.FullName is "System.Linq.Enumerable" or "System.Linq.ImmutableArrayExtensions"
                        && (target.Name == "Any" && argument is "System.Char" or clause or permission or source
                            || target.Name == "All" && argument == permission
                            || target.Name == "Count" && argument is clause or source
                            || target.Name == "Distinct" && argument == source)) continue;
                }
                if (Predicate(target.DeclaringType) && target.Name == ".ctor" && instruction.OpCode == OpCodes.Newobj
                    || Array(target.DeclaringType) && target.Name is "get_IsDefault" or "get_IsDefaultOrEmpty" or "get_Length" or "get_Item" or "Contains") continue;
                return false;
            }
        }
        return true;
    }

    private static void AssertPolicyMetadataControls(ModuleDefinition[] modules, CensusFacts facts)
    {
        var map = Types(modules).ToDictionary(Key, StringComparer.Ordinal);
        var source = map.Values.Single(type => type.FullName == CustomizationPolicies);
        var handler = map.Values.Single(type => type.FullName == "LearnStack.Modules.Customization.Application.IntegrationEvents.InitializeContentTypeDefaultHandler");
        var policies = source.Methods.Single(method => method.Name == "get_Policies");
        void Dirty() => Violations(modules, facts).Should().Contain(value => value.StartsWith("Nested HTTP dispatch/helper handler:", StringComparison.Ordinal));
        var field = new FieldDefinition("ActualHandler", FieldAttributes.Private, source.Module.ImportReference(handler));
        source.Fields.Add(field);
        try { Dirty(); } finally { source.Fields.Remove(field); }
        var local = new VariableDefinition(source.Module.ImportReference(handler));
        policies.Body.Variables.Add(local);
        try { Dirty(); } finally { policies.Body.Variables.Remove(local); }
        var constructor = source.Methods.Single(method => method.IsConstructor);
        var argument = new ParameterDefinition("actualHandler", ParameterAttributes.None, source.Module.ImportReference(handler));
        constructor.Parameters.Add(argument);
        try { Dirty(); } finally { constructor.Parameters.Remove(argument); }
        var handlerConstructor = handler.Methods.Single(method => method.IsConstructor);
        var invocation = Instruction.Create(OpCodes.Newobj, source.Module.ImportReference(handlerConstructor));
        policies.Body.Instructions.Add(invocation);
        try { Dirty(); } finally { policies.Body.Instructions.Remove(invocation); }
        var audit = map.Values.Single(type => type.FullName == CustomizationAudit);
        var describe = audit.Methods.Single(method => method.Name == "Describe");
        var auditField = new FieldDefinition("ActualHandler", FieldAttributes.Private, audit.Module.ImportReference(handler));
        audit.Fields.Add(auditField);
        try { Dirty(); } finally { audit.Fields.Remove(auditField); }
        var auditLocal = new VariableDefinition(audit.Module.ImportReference(handler));
        describe.Body.Variables.Add(auditLocal);
        try { Dirty(); } finally { describe.Body.Variables.Remove(auditLocal); }
        var auditArgument = new ParameterDefinition("actualHandler", ParameterAttributes.None, audit.Module.ImportReference(handler));
        var auditConstructor = audit.Methods.Single(method => method.IsConstructor);
        auditConstructor.Parameters.Add(auditArgument);
        try { Dirty(); } finally { auditConstructor.Parameters.Remove(auditArgument); }
        foreach (var (owner, method) in new[] { (source, policies), (audit, describe) })
            foreach (var opcode in new[] { OpCodes.Callvirt, OpCodes.Ldftn })
            {
                var execute = Instruction.Create(opcode, owner.Module.ImportReference(handler.Methods.Single(item => item.Name == "HandleAsync")));
                method.Body.Instructions.Add(execute);
                try { Dirty(); } finally { method.Body.Instructions.Remove(execute); }
            }
        var policy = map.Values.Single(type => type.FullName == PolicyType);
        var factory = policy.Methods.Single(method => method.Name == "ForEventInitializer");
        var delegated = Instruction.Create(OpCodes.Newobj, policy.Module.ImportReference(handlerConstructor));
        factory.Body.Instructions.Add(delegated);
        try { Dirty(); } finally { factory.Body.Instructions.Remove(delegated); }
        // Reaching a new clause helper must not turn the metadata exemption into a
        // broad policy allowlist: both a named comparison and its generated predicate
        // must lose the exemption if either starts acquiring a handler.
        var comparison = policy.Methods.Single(method => method.Name == "SameRequirements");
        var predicate = policy.NestedTypes.SelectMany(type => type.Methods)
            .First(method => method.Name.StartsWith("<SameRequirements>b__", StringComparison.Ordinal));
        foreach (var helper in new[] { comparison, predicate })
        {
            DataOnlyPolicyFactory(map).Should().BeTrue();
            var executedComparison = Instruction.Create(OpCodes.Newobj, helper.Module.ImportReference(handlerConstructor));
            helper.Body.Instructions.Add(executedComparison);
            try
            {
                DataOnlyPolicyFactory(map).Should().BeFalse("metadata comparisons cannot acquire a handler");
                Dirty();
            }
            finally { helper.Body.Instructions.Remove(executedComparison); }
        }
        var permissionsGetter = map.Values.Single(type => type.FullName == "LearnStack.SharedKernel.Permissions.OperationPolicyClause")
            .Methods.Single(method => method.Name == "get_RequiredPermissions");
        var executedGetter = Instruction.Create(OpCodes.Newobj, permissionsGetter.Module.ImportReference(handlerConstructor));
        permissionsGetter.Body.Instructions.Add(executedGetter);
        try
        {
            DataOnlyPolicyFactory(map).Should().BeFalse("an approved metadata getter must still be an immutable field read");
            Dirty();
        }
        finally { permissionsGetter.Body.Instructions.Remove(executedGetter); }
        var off = map.Values.Single(type => type.FullName == "LearnStack.Infrastructure.Audit.AuditCatalogBuilder")
            .Methods.Single(method => method.Name == "Off");
        var executedRegistration = Instruction.Create(OpCodes.Newobj, off.Module.ImportReference(handlerConstructor));
        off.Body.Instructions.Add(executedRegistration);
        try { Dirty(); } finally { off.Body.Instructions.Remove(executedRegistration); }
        Violations(modules, facts).Should().BeEmpty("removing actual handler acquisition restores the metadata-only edge");
    }
}
