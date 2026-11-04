using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>Finite source construction and installation remain closed independently of UUID/context shape.</summary>
public sealed class TrustedOperationSourceConventionTests
{
    private const string Descriptor = "LearnStack.SharedKernel.Permissions.TrustedOperationDescriptor";
    private const string Minter = Descriptor + "/MintingSource";
    private const string Seed = "LearnStack.Tools.Seeder.SeedOperationSource";
    private const string Delivery = "LearnStack.Infrastructure.Messaging.DeliveryOperationSource";
    private const string Job = "LearnStack.Application.Jobs.BackgroundJobOperationSource";
    private const string Admission = "LearnStack.Application.Jobs.BackgroundJobExecutionAdmission";
    private const string JobOwner = "LearnStack.SharedKernel.Jobs.JobApplicationSourceOwner";

    [Fact]
    public void Finite_operation_descriptors_have_exact_minters_private_construction_and_revocable_evidence()
    {
        using var census = Census.Read();
        Offenders(census).Should().BeEmpty("Fix: preserve the exact owned seed/delivery/job source set and its closed lifetime (ADR-0076)");
        var descriptor = census.Type(Descriptor);
        Dirty(census, () =>
        {
            var constructor = descriptor.Methods.Single(method => method.IsConstructor);
            var attributes = constructor.Attributes;
            constructor.IsPublic = true;
            return () => constructor.Attributes = attributes;
        });
        Dirty(census, () =>
        {
            var field = descriptor.Fields.Single(field => field.Name == "_evidence");
            var attributes = field.Attributes;
            field.IsInitOnly = false;
            return () => field.Attributes = attributes;
        });
        Dirty(census, () =>
        {
            var module = census.Type(Seed).Module;
            var extra = new TypeDefinition("AuthorityControl", "AnotherSource", TypeAttributes.NotPublic,
                module.ImportReference(census.Type(Minter)));
            module.Types.Add(extra);
            return () => module.Types.Remove(extra);
        });
        foreach (var (type, method, target, name) in new[]
        {
            (Descriptor, "get_IsActive", Minter, "MatchesEvidence"),
            (Descriptor, "get_IsActive", Minter, "get_IsActive"),
            (Minter, "Dispose", "System.Threading.Interlocked", "Exchange"),
            (Delivery, "Capture", "LearnStack.SharedKernel.Permissions.OperationPolicy", "get_TrustedEventHandlerType"),
            (Delivery, "IsEvidenceCurrent", "LearnStack.SharedKernel.Persistence.IUnitOfWork", "GetTenantContextAnnouncement"),
            (Admission, "IsOperationEvidenceCurrent", JobOwner, "get_IsCurrentBinding"),
            (Admission, "IsOperationEvidenceCurrent", Admission, "IsLive"),
        }) Dirty(census, () => RemoveCall(census.Body(type, method), target, name));
        foreach (var (typeName, methodName) in new[]
                 { (Delivery, "Enter"), ("LearnStack.SharedKernel.Permissions.ITrustedOperationSource", "Capture") })
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn }) Dirty(census, () =>
            {
                var module = census.Type(Seed).Module;
                var method = new MethodDefinition("UnownedDeliveryInstallation", MethodAttributes.Assembly | MethodAttributes.Static,
                    module.TypeSystem.Void);
                method.Body.Instructions.Add(Instruction.Create(opcode, module.ImportReference(census.Method(typeName, methodName))));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                census.Type(Seed).Methods.Add(method);
                return () => census.Type(Seed).Methods.Remove(method);
            });
    }

    private static List<string> Offenders(Census census)
    {
        var errors = new List<string>();
        var descriptor = census.Type(Descriptor);
        var constructors = descriptor.Methods.Where(method => method.IsConstructor).ToArray();
        if (!descriptor.IsSealed || !descriptor.IsPublic || constructors.Length != 1 || !constructors[0].IsPrivate
            || descriptor.Fields.Count != 5 || descriptor.Fields.Any(field => !field.IsPrivate || !field.IsInitOnly || field.IsStatic)
            || descriptor.Properties.Any(property => property.SetMethod is not null)
            || !Same(descriptor.Fields.Select(field => field.Name), ["_source", "_context", "_evidence",
                "<SourceKind>k__BackingField", "<RequestType>k__BackingField"]))
            errors.Add("The immutable descriptor has only private closed construction and exact evidence fields");
        if (!Same(census.Types.Where(type => type.BaseType?.FullName == Minter).Select(type => type.FullName), [Seed, Delivery, Job]))
            errors.Add("Only the three enumerated owners derive the finite descriptor source");
        foreach (var instruction in census.Methods.SelectMany(method => method.Body.Instructions))
            if (instruction.Operand is MethodReference target && (target.DeclaringType.FullName == Descriptor && target.Name == ".ctor"
                    || target.DeclaringType.FullName == Minter && target.Name == "ForOperation"
                    || target.DeclaringType.FullName == Delivery && target.Name == "Enter"
                    || target.DeclaringType.FullName == Admission && target.Name == "CaptureOperationEvidence"
                    || target.DeclaringType.FullName == "LearnStack.SharedKernel.Permissions.ITrustedOperationSource" && target.Name == "Capture")
                && instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj))
                errors.Add("A finite authority constructor/entry cannot become an executable alias");
        Exact(Descriptor, ".ctor", [Minter + "::ForOperation"]);
        Exact(Seed, ".ctor", ["LearnStack.Tools.Seeder.SeedComposition::Build"]);
        Exact(Delivery, ".ctor", []);
        Exact(Job, ".ctor", []);
        Exact(Minter, "ForOperation", [Seed + "::Capture", Delivery + "::Capture", Job + "::Capture"]);
        Exact(Delivery, "Enter", ["LearnStack.Infrastructure.Messaging.DeliveryExecutor::ExecuteOwnedAsync"]);
        Exact(Admission, "CaptureOperationEvidence", [Job + "::Capture"]);
        Exact(Admission, "IsOperationEvidenceCurrent", [Admission + "::CaptureOperationEvidence", Job + "::IsEvidenceCurrent"]);
        Exact("LearnStack.SharedKernel.Permissions.ITrustedOperationSource", "Capture",
            ["LearnStack.Application.Authorization.OperationAuthorizationCoordinator::ExecuteAsync"]);
        Require(Descriptor, "get_IsActive", Minter, "MatchesEvidence");
        Require(Descriptor, "get_IsActive", Minter, "get_IsActive");
        Require(Minter, "Dispose", "System.Threading.Interlocked", "Exchange");
        Require(Delivery, "Capture", "LearnStack.SharedKernel.Permissions.OperationPolicy", "get_TrustedEventHandlerType");
        Require(Delivery, "IsEvidenceCurrent", "LearnStack.SharedKernel.Persistence.IUnitOfWork", "GetTenantContextAnnouncement");
        Require(Admission, "IsOperationEvidenceCurrent", JobOwner, "get_IsCurrentBinding");
        Require(Admission, "IsOperationEvidenceCurrent", Admission, "IsLive");
        if (Calls(census.Body(Admission, "CaptureOperationEvidence")).Any(target => target.DeclaringType.FullName == Admission
            && target.Name is "TryEnterRequest" or "PermitsRequest"))
            errors.Add("Capturing job evidence never repeats root or nested request admission");
        return errors;

        void Exact(string type, string name, string[] owners)
        {
            var actual = census.Methods.SelectMany(method => Calls(method).Where(target => target.DeclaringType.FullName == type
                    && target.Name == name).Select(_ => census.Owner(method)));
            if (!Same(actual, owners)) errors.Add("Closed finite authority callers changed: " + type + "::" + name);
        }

        void Require(string type, string method, string target, string name)
        {
            if (Calls(census.Body(type, method)).Count(call => call.DeclaringType.FullName == target && call.Name == name) != 1)
                errors.Add("Missing executing finite authority gate: " + type + "::" + method + "->" + target + "::" + name);
        }
    }

    private static void Dirty(Census census, Func<Action> mutate)
    {
        var undo = mutate();
        try { Offenders(census).Should().NotBeEmpty("an actual construction/caller/lifetime mutation must be detected"); }
        finally { undo(); }
        Offenders(census).Should().BeEmpty("the actual graph is restored after each dirty control");
    }

    private static Action RemoveCall(MethodDefinition method, string type, string name)
    {
        var instruction = method.Body.Instructions.Single(instruction => instruction.Operand is MethodReference call
            && call.DeclaringType.FullName == type && call.Name == name);
        var opcode = instruction.OpCode;
        var operand = instruction.Operand;
        instruction.OpCode = OpCodes.Nop;
        instruction.Operand = null;
        return () => { instruction.OpCode = opcode; instruction.Operand = operand; };
    }

    private static IEnumerable<MethodReference> Calls(MethodDefinition method) => method.Body.Instructions
        .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
        .Select(instruction => instruction.Operand).OfType<MethodReference>();
    private static bool Same(IEnumerable<string> actual, IEnumerable<string> expected) =>
        actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    internal sealed class Census : IDisposable
    {
        private readonly ModuleDefinition[] _modules;
        private readonly Dictionary<string, string> _asyncOwners;
        private Census(ModuleDefinition[] modules)
        {
            _modules = modules;
            _asyncOwners = Methods.SelectMany(method => method.CustomAttributes.Where(attribute =>
                    attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute")
                .Select(attribute => (State: ((TypeReference)attribute.ConstructorArguments[0].Value).GetElementType().FullName,
                    Owner: method.DeclaringType.FullName + "::" + method.Name)))
                .ToDictionary(item => item.State, item => item.Owner, StringComparer.Ordinal);
        }
        internal TypeDefinition[] Types => _modules.SelectMany(module => module.Types.SelectMany(Flatten)).ToArray();
        internal MethodDefinition[] Methods => Types.SelectMany(type => type.Methods).Where(method => method.HasBody).ToArray();
        internal static Census Read() => new(ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray());
        internal TypeDefinition Type(string name) => Types.Single(type => type.FullName == name);
        internal MethodDefinition Method(string type, string name) => Type(type).Methods.Single(method => method.Name == name);
        internal MethodDefinition Body(string type, string name)
        {
            var method = Method(type, name);
            var state = method.CustomAttributes.SingleOrDefault(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
            return state is null ? method : Type(((TypeReference)state.ConstructorArguments[0].Value).GetElementType().FullName)
                .Methods.Single(candidate => candidate.Name == "MoveNext");
        }
        internal string Owner(MethodDefinition method)
        {
            if (_asyncOwners.TryGetValue(method.DeclaringType.FullName, out var owner)) return owner;
            if (method.DeclaringType.FullName.StartsWith("LearnStack.Tools.Seeder.SeedComposition/", StringComparison.Ordinal)
                && method.Name.StartsWith("<Build>", StringComparison.Ordinal))
                return "LearnStack.Tools.Seeder.SeedComposition::Build";
            if (method.DeclaringType.FullName.StartsWith("LearnStack.Application.Authorization.OperationAuthorizationCoordinator/<>c__DisplayClass", StringComparison.Ordinal)
                && method.Name.StartsWith("<ExecuteAsync>b__", StringComparison.Ordinal))
                return "LearnStack.Application.Authorization.OperationAuthorizationCoordinator::ExecuteAsync";
            if (method.DeclaringType.FullName.StartsWith("LearnStack.Application.Audit.AuthorizationEvidenceCoordinator/<>c__DisplayClass", StringComparison.Ordinal)
                && method.Name.StartsWith("<Snapshot>b__", StringComparison.Ordinal))
                return "LearnStack.Application.Audit.AuthorizationEvidenceCoordinator::Snapshot";
            if ((method.DeclaringType.FullName == "LearnStack.Modules.Identity.Application.Authorization.RoleGrantAuthorization"
                    || method.DeclaringType.FullName.StartsWith("LearnStack.Modules.Identity.Application.Authorization.RoleGrantAuthorization/<>c__DisplayClass", StringComparison.Ordinal))
                && method.Name.StartsWith("<RecheckAsync>g__Refused|", StringComparison.Ordinal))
                return "LearnStack.Modules.Identity.Application.Authorization.RoleGrantAuthorization::RecheckAsync";
            if ((method.DeclaringType.FullName == "LearnStack.Modules.Identity.Application.Authorization.MembershipAdministrationAuthorization"
                    || method.DeclaringType.FullName.StartsWith("LearnStack.Modules.Identity.Application.Authorization.MembershipAdministrationAuthorization/<>c__DisplayClass", StringComparison.Ordinal))
                && method.Name.StartsWith("<RecheckAsync>g__Refused|", StringComparison.Ordinal))
                return "LearnStack.Modules.Identity.Application.Authorization.MembershipAdministrationAuthorization::RecheckAsync";
            return method.DeclaringType.FullName + "::" + method.Name;
        }
        public void Dispose() { foreach (var module in _modules) module.Dispose(); }
        private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
    }
}
