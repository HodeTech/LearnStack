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
            var behaviorConstructor = behavior.Methods.Single(method => method.Name == ".ctor");
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

            var field = behavior.Fields.Single(field => field.Name == "_readOnlyRequests");
            field.IsInitOnly = false;
            AssertDirty();
            field.IsInitOnly = true;
            var handle = behavior.Methods.Single(method => method.Name == "Handle");
            var contains = handle.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && target.Name == "Contains" && target.DeclaringType.GetElementType().FullName == "System.Collections.Frozen.FrozenSet`1");
            var originalTarget = contains.Operand;
            contains.Operand = behavior.Module.ImportReference(typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!);
            AssertDirty();
            contains.Operand = originalTarget;

            var freeze = behavior.Methods.Single(method => method.Name == ".ctor").Body.Instructions
                .Single(instruction => instruction.Operand is MethodReference target && target.Name == "ToFrozenSet");
            var originalFreeze = freeze.Operand;
            freeze.Operand = behavior.Module.ImportReference(typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!);
            AssertDirty();
            freeze.Operand = originalFreeze;

            var declaration = ReadOnlyTypes(modules).Single(type => type.FullName == ReadOnlyDeclaration);
            var declaredType = declaration.Fields.Single();
            declaredType.IsInitOnly = false;
            AssertDirty();
            declaredType.IsInitOnly = true;

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
                         behavior.NestedTypes.SelectMany(type => type.Methods).Where(method => method.HasBody)
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
        if (behavior is null || declaration is null || !declaration.IsSealed || declaration.Fields.Any(field => !field.IsInitOnly))
            offenders.Add("Missing sealed immutable read-only declaration or transaction owner");
        var snapshot = behavior?.Fields.SingleOrDefault(field => field.Name == "_readOnlyRequests");
        if (snapshot is null || !snapshot.IsInitOnly || snapshot.FieldType.FullName != "System.Collections.Frozen.FrozenSet`1<System.Type>")
            offenders.Add("TransactionBehavior must freeze the exact Type set once");
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
            .Select(site => ReadOnlyOwner(site.Method)), [ReadOnlyBehavior + "..ctor"], "read-only declaration consumers");
        Check(sites.Where(site => site.Target.DeclaringType.FullName == ReadOnlyRegistration && site.Target.Name == "AddTenantSiteReads")
            .Select(site => ReadOnlyOwner(site.Method)),
            ["LearnStack.Api.Composition.PersistenceCompositionExtensions.AddLearnStackPersistence", "LearnStack.Tools.Seeder.SeedComposition.Build"],
            "shared site-read registration callers");
        Check(sites.Where(site => site.Target.Name == "Contains" && site.Target.DeclaringType.GetElementType().FullName == "System.Collections.Frozen.FrozenSet`1"
                && ReadOnlyOwner(site.Method).StartsWith(ReadOnlyBehavior + ".", StringComparison.Ordinal))
            .Select(site => ReadOnlyOwner(site.Method)), [ReadOnlyBehavior + ".Handle"], "exact Type mode lookup");
        Check(sites.Where(site => site.Target.Name == "ToFrozenSet" && ReadOnlyOwner(site.Method).StartsWith(ReadOnlyBehavior + ".", StringComparison.Ordinal))
            .Select(site => ReadOnlyOwner(site.Method)), [ReadOnlyBehavior + "..ctor"], "immutable Type snapshot");
        var genericUses = sites.Where(site => site.Target is GenericInstanceMethod generic
            && generic.GenericArguments.Any(argument => argument.FullName == ReadOnlyDeclaration)).ToArray();
        Check(genericUses.Where(site => IsReadOnlySingletonRegistration(site.Method, (GenericInstanceMethod)site.Target))
            .Select(site => ReadOnlyOwner(site.Method)), [ReadOnlyRegistration + ".AddTenantSiteReads"], "declaration singleton registration");
        Check(genericUses.Where(site => IsReadOnlySnapshotProjection(site.Method, (GenericInstanceMethod)site.Target))
            .Select(site => ReadOnlyOwner(site.Method)), [ReadOnlyBehavior + "..ctor"], "declaration Type projection");
        foreach (var site in genericUses.Where(site => !IsReadOnlySingletonRegistration(site.Method, (GenericInstanceMethod)site.Target)
                     && !IsReadOnlySnapshotProjection(site.Method, (GenericInstanceMethod)site.Target)))
            offenders.Add("Unexpected declaration registration or acquisition: " + ReadOnlyOwner(site.Method));
        return offenders;

        void Check(IEnumerable<string> actual, string[] expected, string subject)
        {
            if (!actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
                offenders.Add("Unexpected " + subject);
        }
    }

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
        owner.DeclaringType.FullName == ReadOnlyBehavior && owner.Name == ".ctor"
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
