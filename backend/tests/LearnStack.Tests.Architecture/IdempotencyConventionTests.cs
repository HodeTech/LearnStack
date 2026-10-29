using FluentAssertions;
using LearnStack.Application.Authorization;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Education.Application.Authorization;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Permissions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>ADR-0079: a receipt identifies the original target and authorizes a new read, never a mutation.</summary>
public sealed class IdempotencyConventionTests
{
    private const string Dispatcher = "LearnStack.Api.Idempotency.ProtectedReceiptReplayDispatcher";
    private static readonly Type[] Receipts =
    [
        typeof(ReadMembershipReceiptQuery), typeof(ReadMemberProfileReceiptQuery), typeof(ReadOwnMembershipProfileReceiptQuery),
        typeof(ReadRoleReceiptQuery), typeof(ReadOrganizationReceiptQuery), typeof(ReadDefaultOrganizationReceiptQuery),
    ];

    [Fact]
    public void Protected_Idempotent_Replay_Reauthorizes_Original_Target()
    {
        var catalog = new OperationPolicyCatalog([
            new TenancyOperationPolicySource(), new CustomizationOperationPolicySource(), new EducationOperationPolicySource(),
            new IdentityOperationPolicySource(), new BackgroundJobOperationPolicySource()]);
        foreach (var receipt in Receipts)
        {
            catalog.TryGet(receipt, out var policy).Should().BeTrue("Fix: classify every receipt read explicitly");
            policy.Kind.Should().Be(receipt == typeof(ReadOwnMembershipProfileReceiptQuery)
                ? OperationPolicyKind.OwnMembershipRead : OperationPolicyKind.Protected);
            policy.RequiresOrglessWrite.Should().BeFalse("disclosure is not the original mutation's eligibility");
            policy.RequiresOrganization.Should().BeFalse();
            policy.ReadScope.Should().Be(OperationReadScope.Scoped);
            if (policy.Kind == OperationPolicyKind.Protected)
                policy.Alternatives.Should().NotBeEmpty().And.OnlyContain(clause => clause.ReadScope == OperationReadScope.Scoped);
            else policy.Alternatives.Should().BeEmpty("own receipt disclosure uses coverage, not an invented permission clause");
        }
        WithProduction(modules =>
        {
            MarkerOffenders(modules).Should().BeEmpty("Fix: keep the receipt marker on exactly the six sealed original-target reads");
            DispatcherOffenders(modules).Should().BeEmpty("Fix: replay must construct the six fresh receipt queries, never the original mutation");
        });
    }

    [Fact]
    public void Receipt_census_detects_missing_indirect_extra_and_mutation_targets()
    {
        WithProduction(modules =>
        {
            var names = ReceiptNames();
            names.Add(Dispatcher);
            names.Add(typeof(CreateManagedOrganizationCommand).FullName!);
            var types = Types(modules).Where(type => names.Contains(type.FullName))
                .ToDictionary(type => type.FullName, StringComparer.Ordinal);
            var receipt = types[typeof(ReadOwnMembershipProfileReceiptQuery).FullName!];
            var marker = receipt.Interfaces.Single(item => item.InterfaceType.FullName == typeof(IIdempotencyReceiptRead).FullName);
            receipt.Interfaces.Remove(marker);
            try { MarkerOffenders(modules).Should().Contain(receipt.FullName); }
            finally { receipt.Interfaces.Add(marker); }
            var sealedState = receipt.IsSealed;
            receipt.IsSealed = false;
            try { MarkerOffenders(modules).Should().Contain(receipt.FullName); }
            finally { receipt.IsSealed = sealedState; }

            var module = receipt.Module;
            var alias = new TypeDefinition("ReceiptControl", "Alias", TypeAttributes.Interface | TypeAttributes.Abstract | TypeAttributes.NotPublic);
            alias.Interfaces.Add(new InterfaceImplementation(marker.InterfaceType));
            var extra = new TypeDefinition("ReceiptControl", "Extra", TypeAttributes.NotPublic | TypeAttributes.Sealed, module.TypeSystem.Object);
            extra.Interfaces.Add(new InterfaceImplementation(alias));
            module.Types.Add(alias);
            module.Types.Add(extra);
            try { MarkerOffenders(modules).Should().Contain(extra.FullName, "indirect marker aliases are not a new authority source"); }
            finally { module.Types.Remove(extra); module.Types.Remove(alias); }

            var dispatcher = types[Dispatcher].Methods.Single(method => method.Name == "ReadAsync");
            var construction = dispatcher.Body.Instructions.First(instruction => instruction.OpCode == OpCodes.Newobj
                && instruction.Operand is MethodReference target && ReceiptNames().Contains(target.DeclaringType.FullName));
            var index = dispatcher.Body.Instructions.IndexOf(construction);
            dispatcher.Body.Instructions.Remove(construction);
            try { DispatcherOffenders(modules).Should().NotBeEmpty("removing one actual receipt target must be detected"); }
            finally { dispatcher.Body.Instructions.Insert(index, construction); }
            var mutation = types[typeof(CreateManagedOrganizationCommand).FullName!].Methods
                .Single(method => method.IsConstructor && method.IsPublic);
            var extraMutation = Instruction.Create(OpCodes.Newobj, dispatcher.Module.ImportReference(mutation));
            dispatcher.Body.Instructions.Add(extraMutation);
            try { DispatcherOffenders(modules).Should().Contain(mutation.DeclaringType.FullName, "an original mutation is not a replay guard"); }
            finally { dispatcher.Body.Instructions.Remove(extraMutation); }
            MarkerOffenders(modules).Should().BeEmpty();
            DispatcherOffenders(modules).Should().BeEmpty();
        });
    }

    private static string[] MarkerOffenders(ModuleDefinition[] modules)
    {
        var all = Types(modules).ToArray();
        var types = all.ToDictionary(type => Symbol(type));
        var expected = ReceiptNames();
        var actual = all.Where(type => !type.IsInterface && HasMarker(type, types, [])).ToArray();
        var actualNames = actual.Select(type => type.FullName).ToHashSet(StringComparer.Ordinal);
        return expected.Except(actualNames, StringComparer.Ordinal)
            .Concat(actualNames.Except(expected, StringComparer.Ordinal))
            .Concat(actual.GroupBy(type => type.FullName, StringComparer.Ordinal).Where(group => group.Count() != 1).Select(group => group.Key))
            .Concat(actual.Where(type => !type.IsSealed || type.IsAbstract
                || type.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "LearnStack.SharedKernel.Tenancy.PublicSurfaceAttribute")
                || type.Properties.Count(property => property.GetMethod?.IsPublic == true) != 1)
                .Select(type => type.FullName))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool HasMarker(TypeDefinition type, Dictionary<(string Assembly, string Name), TypeDefinition> types,
        HashSet<TypeDefinition> visited)
    {
        if (!visited.Add(type)) return false;
        foreach (var contract in type.Interfaces)
        {
            if (contract.InterfaceType.FullName == typeof(IIdempotencyReceiptRead).FullName
                && Symbol(contract.InterfaceType).Assembly == typeof(IIdempotencyReceiptRead).Assembly.GetName().Name) return true;
            if (types.TryGetValue(Symbol(contract.InterfaceType), out var parent) && HasMarker(parent, types, visited)) return true;
        }
        return type.BaseType is { } baseType && types.TryGetValue(Symbol(baseType), out var ancestor)
            && HasMarker(ancestor, types, visited);
    }

    private static (string Assembly, string Name) Symbol(TypeReference reference)
    {
        var type = reference.GetElementType();
        return (type.Scope is AssemblyNameReference assembly ? assembly.Name : type.Module.Assembly.Name.Name, type.FullName);
    }

    private static string[] DispatcherOffenders(ModuleDefinition[] modules)
    {
        var owner = Types(modules).Single(type => type.FullName == Dispatcher);
        var method = owner.Methods.Single(candidate => candidate.Name == "ReadAsync");
        var constructions = method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Newobj)
            .Select(instruction => (MethodReference)instruction.Operand)
            .Where(target => target.DeclaringType.Namespace.Contains(".Application.Contracts", StringComparison.Ordinal))
            .Select(target => target.DeclaringType.FullName).ToArray();
        var expected = ReceiptNames();
        return expected.Except(constructions, StringComparer.Ordinal)
            .Concat(constructions.Except(expected, StringComparer.Ordinal))
            .Concat(constructions.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() != 1).Select(group => group.Key))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static HashSet<string> ReceiptNames() => Receipts.Select(type => type.FullName!).ToHashSet(StringComparer.Ordinal);
    private static IEnumerable<TypeDefinition> Types(IEnumerable<ModuleDefinition> modules) => modules.SelectMany(module => module.Types).SelectMany(Types);
    private static IEnumerable<TypeDefinition> Types(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Types));
    private static void WithProduction(Action<ModuleDefinition[]> assertion)
    {
        var modules = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        try { assertion(modules); }
        finally { foreach (var module in modules) module.Dispose(); }
    }
}
