using FluentAssertions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Infrastructure;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class PersistenceConventionTests
{
    private const string ReadOnlyDeclaration = "LearnStack.SharedKernel.Persistence.ExplicitReadOnlyRequest";
    private const string ReadOnlyCatalog = "LearnStack.Application.Persistence.ExplicitReadOnlyRequestCatalog";
    private const string ReadOnlyBehavior = "LearnStack.Application.Pipeline.TransactionBehavior`2";
    private const string ReadOnlyRegistration = "LearnStack.Modules.Tenancy.Infrastructure.TenantSiteRegistration";
    private const string AccountSiteRequest = "LearnStack.Modules.Tenancy.Application.Contracts.Account.GetAccountSiteQuery";

    // The shared persistence seam stays module-independent. Its one additional
    // authenticated read is declared by the owning module, never inferred public.
    private static void AssertExplicitReadOnlyComposition()
    {
        typeof(ExplicitReadOnlyRequest).IsSealed.Should().BeTrue();
        typeof(ExplicitReadOnlyRequest).GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType).Should().Equal(typeof(Type));
        typeof(ExplicitReadOnlyRequest).GetProperties().Should().ContainSingle()
            .Which.SetMethod.Should().BeNull();
        typeof(GetAccountSiteQuery).IsDefined(typeof(LearnStack.SharedKernel.Tenancy.PublicSurfaceAttribute), false)
            .Should().BeFalse("the authenticated read does not acquire anonymous authority");

        var modules = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        try
        {
            ExplicitReadOnlyModeOffenders(modules).Should().BeEmpty(
                "Fix: keep the exact immutable AccountSite declaration in Tenancy and consume it only in the existing transaction owner; API and Seeder call the shared registration");
            var registration = ReadOnlyTypes(modules).Single(type => type.FullName == ReadOnlyRegistration)
                .Methods.Single(method => method.Name == nameof(TenantSiteRegistration.AddTenantSiteReads));
            var construction = registration.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == ReadOnlyDeclaration && target.Name == ".ctor");
            var literal = registration.Body.Instructions[registration.Body.Instructions.IndexOf(construction) - 2];
            var originalType = literal.Operand;
            literal.Operand = registration.Module.ImportReference(typeof(string));
            AssertDirty();
            literal.Operand = originalType;

            var behavior = ReadOnlyTypes(modules).Single(type => type.FullName == ReadOnlyBehavior);
            var catalog = ReadOnlyTypes(modules).Single(type => type.FullName == ReadOnlyCatalog);
            var behaviorConstructor = catalog.Methods.Single(method => method.Name == ".ctor");
            var projection = behaviorConstructor.Body.Instructions.Single(instruction => instruction.Operand is GenericInstanceMethod target
                && target.Name == "Select" && target.GenericArguments.Any(argument => argument.FullName == ReadOnlyDeclaration));
            var singleton = registration.Body.Instructions.Single(instruction => instruction.Operand is GenericInstanceMethod target
                && target.Name == "AddSingleton" && target.GenericArguments.Any(argument => argument.FullName == ReadOnlyDeclaration));
            var originalProjection = projection.Operand;
            projection.Operand = singleton.Operand;
            AssertDirty();
            projection.Operand = originalProjection;
            var originalSingleton = singleton.Operand;
            singleton.Operand = projection.Operand;
            AssertDirty();
            singleton.Operand = originalSingleton;

            var getServices = new GenericInstanceMethod(registration.Module.ImportReference(
                typeof(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions).GetMethods()
                    .Single(method => method.Name == "GetServices" && method.IsGenericMethodDefinition)));
            getServices.GenericArguments.Add(registration.Module.ImportReference(typeof(ExplicitReadOnlyRequest)));
            foreach (var owner in new[] { registration, behaviorConstructor })
            {
                var acquisition = Instruction.Create(OpCodes.Call, getServices);
                owner.Body.Instructions.Add(acquisition);
                AssertDirty();
                owner.Body.Instructions.Remove(acquisition);
                var duplicate = Instruction.Create(OpCodes.Call, (MethodReference)(owner == registration ? singleton.Operand : projection.Operand));
                owner.Body.Instructions.Add(duplicate);
                AssertDirty();
                owner.Body.Instructions.Remove(duplicate);
            }

            var field = catalog.Fields.Single();
            field.IsInitOnly = false;
            AssertDirty();
            field.IsInitOnly = true;
            var handle = behavior.Methods.Single(method => method.Name == "Handle");
            var contains = handle.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && target.Name == "Contains" && target.DeclaringType.GetElementType().FullName == "System.Collections.Frozen.FrozenSet`1");
            var argument = handle.Body.Instructions[handle.Body.Instructions.IndexOf(contains) - 2];
            var originalArgument = argument.Operand;
            argument.Operand = behavior.Module.TypeSystem.String;
            AssertDirty();
            argument.Operand = originalArgument;

            var hardcoded = Instruction.Create(OpCodes.Isinst, registration.Module.ImportReference(typeof(GetAccountSiteQuery)));
            handle.Body.Instructions.Add(hardcoded);
            AssertDirty();
            handle.Body.Instructions.Remove(hardcoded);
            var hardcodedToken = Instruction.Create(OpCodes.Ldtoken, registration.Module.ImportReference(typeof(GetAccountSiteQuery)));
            handle.Body.Instructions.Add(hardcodedToken);
            AssertDirty();
            handle.Body.Instructions.Remove(hardcodedToken);

            var modeBranch = contains.Next;
            var originalBranch = modeBranch.OpCode;
            modeBranch.OpCode = OpCodes.Brfalse_S;
            AssertDirty();
            modeBranch.OpCode = originalBranch;

            var originalTarget = contains.Operand;
            contains.Operand = behavior.Module.ImportReference(typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!);
            AssertDirty();
            contains.Operand = originalTarget;

            var freeze = catalog.Methods.Single(method => method.Name == ".ctor").Body.Instructions
                .Single(instruction => instruction.Operand is MethodReference target && target.Name == "ToFrozenSet");
            var store = behaviorConstructor.Body.Instructions[behaviorConstructor.Body.Instructions.IndexOf(freeze) + 1];
            var originalStore = store.OpCode;
            store.OpCode = OpCodes.Pop;
            AssertDirty();
            store.OpCode = originalStore;

            var originalFreeze = freeze.Operand;
            freeze.Operand = behavior.Module.ImportReference(typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!);
            AssertDirty();
            freeze.Operand = originalFreeze;

            var declaration = ReadOnlyTypes(modules).Single(type => type.FullName == ReadOnlyDeclaration);
            AssertReadOnlySelectionControl(handle, modules);
            AssertReadOnlyShapeControls(declaration, "System.Type", "RequestType", "System.Type");
            AssertReadOnlyShapeControls(catalog, "System.Collections.Frozen.FrozenSet`1<System.Type>", "RequestTypes",
                "System.Collections.Generic.IEnumerable`1<" + ReadOnlyDeclaration + ">");
            var extraCatalogField = new FieldDefinition("ExtraState", FieldAttributes.Private | FieldAttributes.InitOnly, catalog.Module.TypeSystem.String);
            catalog.Fields.Add(extraCatalogField);
            try
            {
                ExplicitReadOnlyModeOffenders(modules).Should().Contain(
                    "The read-only catalog exposes only its immutable exact-type snapshot",
                    "malformed production field populations return the shape diagnostic instead of throwing");
            }
            finally { catalog.Fields.Remove(extraCatalogField); }
            var catalogGetter = catalog.Methods.Single(method => method.Name == "get_RequestTypes");
            var getterField = catalogGetter.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldfld);
            var originalGetterField = getterField.OpCode;
            getterField.OpCode = OpCodes.Ldnull;
            AssertDirty();
            getterField.OpCode = originalGetterField;

            var declaredType = declaration.Fields.Single();
            declaredType.IsInitOnly = false;
            AssertDirty();
            declaredType.IsInitOnly = true;

            var selector = new MethodDefinition("SelectMode", MethodAttributes.Public, declaration.Module.TypeSystem.Boolean);
            declaration.Methods.Add(selector);
            AssertDirty();
            declaration.Methods.Remove(selector);

            var runtimeType = Instruction.Create(OpCodes.Ldtoken, declaration);
            var nonGeneric = registration.Module.ImportReference(typeof(Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions)
                .GetMethods().Single(method => method.Name == "AddSingleton" && !method.IsGenericMethod
                    && method.GetParameters().Select(parameter => parameter.ParameterType)
                        .SequenceEqual(new[] { typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection), typeof(Type), typeof(Type) })));
            var registrationByType = Instruction.Create(OpCodes.Call, nonGeneric);
            registration.Body.Instructions.Add(runtimeType);
            registration.Body.Instructions.Add(registrationByType);
            AssertDirty();
            registration.Body.Instructions.Remove(registrationByType);
            registration.Body.Instructions.Remove(runtimeType);

            foreach (var caller in ReadOnlyTypes(modules).SelectMany(type => type.Methods).Where(method => method.HasBody)
                         .SelectMany(method => method.Body.Instructions)
                         .Where(instruction => instruction.Operand is MethodReference target
                             && target.DeclaringType.FullName == ReadOnlyRegistration
                             && target.Name == nameof(TenantSiteRegistration.AddTenantSiteReads)).ToArray())
            {
                var original = caller.Operand;
                caller.Operand = behavior.Module.ImportReference(typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!);
                AssertDirty();
                caller.Operand = original;
            }

            var module = registration.Module;
            var clean = new TypeDefinition("ReadOnlyProbe", "Unrelated", TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(clean);
            ExplicitReadOnlyModeOffenders(modules).Should().BeEmpty("an unrelated type is a clean control");
            var extra = new MethodDefinition("Run", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
            clean.Methods.Add(extra);
            foreach (var target in new[] { construction.Operand,
                         catalog.NestedTypes.SelectMany(type => type.Methods).Where(method => method.HasBody)
                             .SelectMany(method => method.Body.Instructions).Select(instruction => instruction.Operand)
                             .OfType<MethodReference>().Single(target => target.DeclaringType.FullName == ReadOnlyDeclaration
                                 && target.Name == "get_RequestType"), registration, projection.Operand, singleton.Operand })
            {
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Call, (MethodReference)target));
                AssertDirty();
                extra.Body.Instructions.Clear();
            }
            module.Types.Remove(clean);
            ExplicitReadOnlyModeOffenders(modules).Should().BeEmpty("restoring all production subjects restores the clean verdict");

            void AssertDirty() => ExplicitReadOnlyModeOffenders(modules).Should().NotBeEmpty(
                "a planted production declaration, consumer, exact-type or registration mutation must fail the same predicate");
        }
        finally
        {
            foreach (var module in modules) module.Dispose();
        }
    }

    private static SortedSet<string> ExplicitReadOnlyModeOffenders(IEnumerable<ModuleDefinition> modules)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = ReadOnlyTypes(modules).ToArray();
        var behavior = types.SingleOrDefault(type => type.FullName == ReadOnlyBehavior);
        var declaration = types.SingleOrDefault(type => type.FullName == ReadOnlyDeclaration);
        var catalog = types.SingleOrDefault(type => type.FullName == ReadOnlyCatalog);
        if (behavior is null || declaration is null || !declaration.IsSealed || declaration.Fields.Any(field => !field.IsInitOnly))
            offenders.Add("Missing sealed immutable read-only declaration or transaction owner");
        var snapshot = catalog?.Fields.Count == 1 ? catalog.Fields[0] : null;
        if (snapshot is null || !snapshot.IsInitOnly || snapshot.FieldType.FullName != "System.Collections.Frozen.FrozenSet`1<System.Type>")
            offenders.Add("TransactionBehavior must freeze the exact Type set once");
        var catalogReference = behavior?.Fields.SingleOrDefault(field => field.Name == "_readOnlyRequests");
        if (catalogReference is null || !catalogReference.IsInitOnly || catalogReference.FieldType.FullName != ReadOnlyCatalog)
            offenders.Add("TransactionBehavior must retain its injected immutable catalog");
        var sites = types.SelectMany(type => type.Methods).Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions.Select(instruction => (Method: method, Instruction: instruction)))
            .Where(site => site.Instruction.Operand is MethodReference)
            .Select(site => (site.Method, site.Instruction, Target: (MethodReference)site.Instruction.Operand)).ToArray();
        var constructors = sites.Where(site => site.Target.DeclaringType.FullName == ReadOnlyDeclaration && site.Target.Name == ".ctor").ToArray();
        if (constructors.Length != 1 || ReadOnlyOwner(constructors[0].Method) != ReadOnlyRegistration + ".AddTenantSiteReads")
            offenders.Add("Only Tenancy.AddTenantSiteReads declares an explicit read-only request");
        foreach (var site in constructors)
        {
            var index = site.Method.Body.Instructions.IndexOf(site.Instruction);
            if (index < 2 || site.Method.Body.Instructions[index - 2] is not { OpCode.Code: Code.Ldtoken, Operand: TypeReference literal }
                || literal.FullName != AccountSiteRequest || literal.Scope.Name != "LearnStack.Modules.Tenancy.Application.Contracts"
                || site.Method.Body.Instructions[index - 1].Operand is not MethodReference conversion
                || conversion.FullName != "System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle)")
                offenders.Add("The sole declaration must name the exact AccountSite CLR type literal");
        }
        Check(sites.Where(site => site.Target.DeclaringType.FullName == ReadOnlyDeclaration && site.Target.Name == "get_RequestType")
            .Select(site => ReadOnlyOwner(site.Method)), [ReadOnlyCatalog + "..ctor"], "read-only declaration consumers");
        Check(sites.Where(site => site.Target.DeclaringType.FullName == ReadOnlyRegistration && site.Target.Name == "AddTenantSiteReads")
            .Select(site => ReadOnlyOwner(site.Method)),
            ["LearnStack.Api.Composition.PersistenceCompositionExtensions.AddLearnStackPersistence", "LearnStack.Tools.Seeder.SeedComposition.Build"],
            "shared site-read registration callers");
        Check(sites.Where(site => site.Target.Name == "Contains" && site.Target.DeclaringType.GetElementType().FullName == "System.Collections.Frozen.FrozenSet`1"
                && ReadOnlyOwner(site.Method).StartsWith(ReadOnlyBehavior + ".", StringComparison.Ordinal))
            .Select(site => ReadOnlyOwner(site.Method)), [ReadOnlyBehavior + ".Handle"], "exact Type mode lookup");
        Check(sites.Where(site => site.Target.Name == "ToFrozenSet" && ReadOnlyOwner(site.Method).StartsWith(ReadOnlyCatalog + ".", StringComparison.Ordinal))
            .Select(site => ReadOnlyOwner(site.Method)), [ReadOnlyCatalog + "..ctor"], "immutable Type snapshot");
        var genericUses = sites.Where(site => site.Target is GenericInstanceMethod generic
            && generic.GenericArguments.Any(argument => argument.FullName == ReadOnlyDeclaration)).ToArray();
        Check(genericUses.Where(site => IsReadOnlySingletonRegistration(site.Method, (GenericInstanceMethod)site.Target))
            .Select(site => ReadOnlyOwner(site.Method)), [ReadOnlyRegistration + ".AddTenantSiteReads"], "declaration singleton registration");
        Check(genericUses.Where(site => IsReadOnlySnapshotProjection(site.Method, (GenericInstanceMethod)site.Target))
            .Select(site => ReadOnlyOwner(site.Method)), [ReadOnlyCatalog + "..ctor"], "declaration Type projection");
        foreach (var site in genericUses.Where(site => !IsReadOnlySingletonRegistration(site.Method, (GenericInstanceMethod)site.Target)
                     && !IsReadOnlySnapshotProjection(site.Method, (GenericInstanceMethod)site.Target)))
            offenders.Add("Unexpected declaration registration or acquisition: " + ReadOnlyOwner(site.Method));
        if (declaration is null || !ReadOnlyShape(declaration, "System.Type", "RequestType", "System.Type"))
            offenders.Add("Read-only declarations have exactly one immutable Type property and one constructor");
        if (catalog is null || !ReadOnlyShape(catalog, "System.Collections.Frozen.FrozenSet`1<System.Type>", "RequestTypes",
                "System.Collections.Generic.IEnumerable`1<" + ReadOnlyDeclaration + ">"))
            offenders.Add("The read-only catalog exposes only its immutable exact-type snapshot");
        foreach (var type in types)
            foreach (var method in type.Methods.Where(method => method.HasBody))
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is TypeReference reference && reference.FullName is ReadOnlyDeclaration or ReadOnlyCatalog)
                        offenders.Add("Runtime type registration/acquisition of read-only authority: " + method.FullName);
                    if (ReadOnlyOwner(method).StartsWith(ReadOnlyBehavior + ".", StringComparison.Ordinal)
                        && instruction.OpCode.Code is Code.Ldtoken or Code.Isinst or Code.Castclass
                        && instruction.Operand is TypeReference selected && !IsRequestParameter(selected))
                        offenders.Add("Transaction mode cannot select a concrete request: " + method.FullName);
                }
        foreach (var site in sites.Where(site => site.Target.Name == "Contains"
                     && ReadOnlyOwner(site.Method).StartsWith(ReadOnlyBehavior + ".", StringComparison.Ordinal)))
        {
            var instructions = site.Method.Body.Instructions;
            var index = instructions.IndexOf(site.Instruction);
            if (index < 3 || instructions[index - 2] is not { OpCode.Code: Code.Ldtoken, Operand: TypeReference selected }
                || !IsRequestParameter(selected) || !IsTypeConversion(instructions[index - 1])
                || instructions[index - 3].Operand is not MethodReference getter || getter.FullName !=
                    "System.Collections.Frozen.FrozenSet`1<System.Type> " + ReadOnlyCatalog + "::get_RequestTypes()")
                offenders.Add("Contains must consume the frozen catalog and typeof(TRequest)");
            var branch = site.Instruction.Next;
            if (branch.OpCode.Code is not (Code.Brtrue or Code.Brtrue_S) || branch.Operand is not Instruction readOnly
                || readOnly.OpCode != OpCodes.Ldc_I4_1 || !IsReadOnlyFallback(branch, readOnly))
                offenders.Add("Exact-type membership must select ReadOnly and only the existing admission selects the fallback");
        }
        if (behavior?.Methods.SingleOrDefault(method => method.Name == "Handle") is not { } handle
            || !ReadOnlyModeSelectionMatches(handle))
            offenders.Add("Transaction mode must reach the coordinator only from exact membership or the approved public/job fallback");
        var freezes = sites.Where(site => site.Target.Name == "ToFrozenSet"
            && ReadOnlyOwner(site.Method) == ReadOnlyCatalog + "..ctor").ToArray();
        foreach (var site in freezes)
        {
            var instructions = site.Method.Body.Instructions;
            var index = instructions.IndexOf(site.Instruction);
            if (index < 2 || index + 1 >= instructions.Count
                || instructions[index - 1].OpCode != OpCodes.Ldnull
                || instructions[index - 2].Operand is not GenericInstanceMethod projection
                || !IsReadOnlySnapshotProjection(site.Method, projection)
                || instructions[index + 1] is not { OpCode.Code: Code.Stfld, Operand: FieldReference destination }
                || destination.FullName != snapshot?.FullName)
                offenders.Add("The catalog field must receive the projected Type sequence frozen once");
        }
        foreach (var selector in sites.Where(site => site.Target.DeclaringType.FullName == ReadOnlyDeclaration && site.Target.Name == "get_RequestType"))
        {
            var body = selector.Method.Body.Instructions.Where(instruction => instruction.OpCode != OpCodes.Nop).ToArray();
            if (body.Length != 3 || body[0].OpCode != OpCodes.Ldarg_1 || body[1] != selector.Instruction || body[2].OpCode != OpCodes.Ret)
                offenders.Add("The catalog projection must return each declaration's exact Type");
        }
        Check(sites.Where(site => site.Target.DeclaringType.FullName == ReadOnlyCatalog && site.Target.Name == "get_RequestTypes")
            .Select(site => ReadOnlyOwner(site.Method)), [ReadOnlyBehavior + ".Handle"], "catalog consumers");
        Check(sites.Where(site => site.Target is GenericInstanceMethod generic
                && generic.GenericArguments.Any(argument => argument.FullName == ReadOnlyCatalog))
            .Select(site => site.Target.FullName + "@" + ReadOnlyOwner(site.Method)),
            ["System.Void Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions::TryAddSingleton<" + ReadOnlyCatalog
                + ">(Microsoft.Extensions.DependencyInjection.IServiceCollection)@LearnStack.Application.Pipeline.MediatRPipelineRegistration.AddLearnStackMediatRPipeline"],
            "singleton catalog registration");
        return offenders;

        void Check(IEnumerable<string> actual, string[] expected, string subject)
        {
            if (!actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
                offenders.Add("Unexpected " + subject);
        }
    }

    private static bool ReadOnlyShape(TypeDefinition type, string propertyType, string property, string constructorArgument) =>
        type.IsSealed && type.Fields.Count == 1 && type.Fields[0].IsPrivate && type.Fields[0].IsInitOnly
        && type.Fields[0].FieldType.FullName == propertyType && !type.Fields[0].IsStatic
        && type.Properties.Count == 1 && type.Properties[0].Name == property && type.Properties[0].SetMethod is null
        && type.Properties[0].PropertyType.FullName == propertyType && !type.HasEvents && !type.HasGenericParameters
        && ReturnsOnlyReadOnlyField(type.Properties[0].GetMethod, type.Fields[0])
        && type.Methods.Select(method => method.FullName).Order(StringComparer.Ordinal).SequenceEqual(new[]
        {
            "System.Void " + type.FullName + "::.ctor(" + constructorArgument + ")",
            propertyType + " " + type.FullName + "::get_" + property + "()"
        }.Order(StringComparer.Ordinal));

    private static bool ReturnsOnlyReadOnlyField(MethodDefinition? getter, FieldDefinition field)
    {
        if (getter is not { HasBody: true }) return false;
        var body = getter.Body.Instructions.Where(instruction => instruction.OpCode != OpCodes.Nop).ToArray();
        return body.Length == 3 && body[0].OpCode == OpCodes.Ldarg_0 && body[1].OpCode == OpCodes.Ldfld
            && body[1].Operand is FieldReference returned && returned.FullName == field.FullName && body[2].OpCode == OpCodes.Ret;
    }

    private static bool IsReadOnlyFallback(Instruction branch, Instruction readOnly)
    {
        var token = branch.Next;
        var conversion = token?.Next;
        var instance = conversion?.Next;
        var catalog = instance?.Next;
        var mode = catalog?.Next;
        var end = mode?.Next;
        return token is { OpCode.Code: Code.Ldtoken, Operand: TypeReference type } && IsRequestParameter(type)
            && conversion is not null && IsTypeConversion(conversion) && instance?.OpCode == OpCodes.Ldarg_0
            && catalog is { OpCode.Code: Code.Ldfld, Operand: FieldReference jobs }
            && jobs.FieldType.FullName == "LearnStack.SharedKernel.Jobs.IBackgroundJobRequestCatalog"
            && mode?.Operand is MethodReference fallback && fallback.FullName ==
                "LearnStack.SharedKernel.Persistence.TransactionMode LearnStack.Application.Jobs.BackgroundJobExecutionAdmission::ModeFor(System.Type,LearnStack.SharedKernel.Jobs.IBackgroundJobRequestCatalog)"
            && end is { OpCode.Code: Code.Br or Code.Br_S, Operand: Instruction selected } && selected == readOnly.Next;
    }

    private static bool IsRequestParameter(TypeReference type) => type is GenericParameter
    { Type: GenericParameterType.Type, Position: 0, Name: "TRequest" } parameter
        && parameter.Owner is TypeReference owner && owner.GetElementType().FullName == ReadOnlyBehavior;

    private static bool IsTypeConversion(Instruction instruction) => instruction.Operand is MethodReference target
        && target.FullName == "System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle)";

    private static bool IsReadOnlySingletonRegistration(MethodDefinition owner, GenericInstanceMethod target) =>
        owner.DeclaringType.FullName == ReadOnlyRegistration && owner.Name == "AddTenantSiteReads"
        && target.DeclaringType.FullName == "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions"
        && target.DeclaringType.Scope.Name == "Microsoft.Extensions.DependencyInjection.Abstractions"
        && target.Name == "AddSingleton" && !target.HasThis
        && target.GenericArguments.Select(argument => argument.FullName).SequenceEqual([ReadOnlyDeclaration])
        && target.Parameters.Count == 2
        && target.Parameters[0].ParameterType.FullName == "Microsoft.Extensions.DependencyInjection.IServiceCollection"
        && IsMethodTypeParameter(target.Parameters[1].ParameterType, 0)
        && target.ReturnType.FullName == "Microsoft.Extensions.DependencyInjection.IServiceCollection";

    private static bool IsReadOnlySnapshotProjection(MethodDefinition owner, GenericInstanceMethod target) =>
        owner.DeclaringType.FullName == ReadOnlyCatalog && owner.Name == ".ctor"
        && target.DeclaringType.FullName == "System.Linq.Enumerable" && target.DeclaringType.Scope.Name == "System.Linq"
        && target.Name == "Select" && !target.HasThis
        && target.GenericArguments.Select(argument => argument.FullName).SequenceEqual([ReadOnlyDeclaration, "System.Type"])
        && target.Parameters.Count == 2
        && IsMethodGenericType(target.Parameters[0].ParameterType, "System.Collections.Generic.IEnumerable`1", [0])
        && IsMethodGenericType(target.Parameters[1].ParameterType, "System.Func`2", [0, 1])
        && IsMethodGenericType(target.ReturnType, "System.Collections.Generic.IEnumerable`1", [1]);

    private static bool IsMethodGenericType(TypeReference type, string name, int[] positions) =>
        type is GenericInstanceType generic && generic.ElementType.FullName == name
        && generic.GenericArguments.Count == positions.Length
        && generic.GenericArguments.Select((argument, index) => IsMethodTypeParameter(argument, positions[index])).All(matches => matches);

    private static bool IsMethodTypeParameter(TypeReference type, int position) =>
        type is GenericParameter { Type: GenericParameterType.Method } parameter && parameter.Position == position;

    private static string ReadOnlyOwner(MethodDefinition method)
    {
        var owner = method.DeclaringType;
        while (owner.DeclaringType is { } parent) owner = parent;
        return owner.FullName + "." + (method.Name.StartsWith("<.ctor>", StringComparison.Ordinal) ? ".ctor" : method.Name);
    }

    private static IEnumerable<TypeDefinition> ReadOnlyTypes(IEnumerable<ModuleDefinition> modules) => modules.SelectMany(module => module.Types)
        .SelectMany(Expand);

    private static IEnumerable<TypeDefinition> Expand(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Expand));
}
