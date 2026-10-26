using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private static IEnumerable<string> CleanupOwnerConsumerIds(ProductionCensus census, string store) =>
        census.Types.TryGetValue((InfrastructureAssembly, store + "/OwnedConnection"), out var owner)
            ? owner.Methods.Where(method => method.Name is ".ctor" or "DisposeCoreAsync").Select(MethodId)
            : [];

    private static void CheckCleanupConnectionOwner(SortedSet<string> offenders, ProductionCensus census,
        TypeDefinition store, string reader, string[] callers)
    {
        var name = store.FullName + "/OwnedConnection";
        if (!census.Types.TryGetValue((InfrastructureAssembly, name), out var owner))
        { offenders.Add("Missing concrete cleanup owner: " + name); return; }
        if (!owner.IsNestedPrivate || !owner.IsSealed || owner.Interfaces.Count != 1
            || owner.Interfaces[0].InterfaceType.FullName != "System.IAsyncDisposable")
            offenders.Add(name + ": cleanup ownership must remain private sealed and async-disposable");
        var bridge = store.FullName == JobBridgeStore;
        var constructor = RequireBoundaryMethod(offenders, owner, ".ctor", "System.Void", MethodAttributes.Public,
            bridge ? [store.FullName] : [store.FullName, "System.Int32"]);
        var attach = RequireBoundaryMethod(offenders, owner, "Attach", "System.Void", MethodAttributes.Assembly, "Npgsql.NpgsqlConnection");
        var connection = RequireBoundaryMethod(offenders, owner, "get_Connection", "Npgsql.NpgsqlConnection", MethodAttributes.Assembly);
        var dispose = RequireBoundaryMethod(offenders, owner, "DisposeAsync", "System.Threading.Tasks.ValueTask", MethodAttributes.Public);
        var cleanup = RequireBoundaryMethod(offenders, owner, "DisposeCoreAsync", BoundaryTask, MethodAttributes.Private);
        var release = RequireBoundaryMethod(offenders, store, "ReleaseConnection", "System.Void", MethodAttributes.Private, name);
        var retain = RequireBoundaryMethod(offenders, store, "RetainConnection", "System.Void", MethodAttributes.Private, name);
        var observation = RequireBoundaryMethod(offenders, store, "get_HasUnconfirmedCleanup", "System.Boolean", MethodAttributes.Public);
        CheckBridgeCapturedField(offenders, census, owner, "<owner>P", store.FullName,
            [constructor + ":stfld", cleanup + ":ldfld", cleanup + ":ldfld"]);

        var sites = CallSites(census).ToArray();
        RecordDetachedTargets(offenders, sites);
        CheckSet(offenders, name + " gated acquisition callers", sites.Where(site => TargetsDeclaredType(site.Target, store)
            && site.Target.Name == "OpenConnectionAsync").Select(site => site.Owner), callers);
        CheckCallers(store, "ReleaseConnection", [reader, cleanup]);
        CheckCallers(store, "RetainConnection", [cleanup]);
        CheckCallers(owner, ".ctor", [reader]);
        CheckCallers(owner, "Attach", [reader]);
        CheckCallers(owner, "get_Connection", callers.Concat([cleanup]));
        CheckCallers(owner, "DisposeAsync", callers);
        CheckCallers(owner, "DisposeCoreAsync", [dispose]);
        if (!bridge)
        {
            RequireBoundaryMethod(offenders, owner, "get_Slot", "System.Int32", MethodAttributes.Assembly);
            RequireBoundaryMethod(offenders, owner, "RecordCommitFailure", "System.Void", MethodAttributes.Assembly, "System.Exception");
            RequireBoundaryMethod(offenders, owner, "ConfirmCommitOutcome", "System.Void", MethodAttributes.Assembly);
            var terminal = callers.Single(caller => caller.Contains("::TryTerminalizeAsync(", StringComparison.Ordinal));
            CheckCallers(owner, "get_Slot", [release, release, retain]);
            CheckCallers(owner, "RecordCommitFailure", [terminal, terminal]);
            CheckCallers(owner, "ConfirmCommitOutcome", [terminal]);
        }

        // The actual driver disposal moved into this one retained owner. A helper
        // on either sanctioned type must not recover a second disposal path.
        CheckSet(offenders, name + " concrete connection disposal", sites.Where(site =>
                (site.Target.DeclaringType?.FullName is "Npgsql.NpgsqlConnection" or "System.Data.Common.DbConnection")
                && site.Target.Name is "Dispose" or "DisposeAsync"
                && (IsDeclaredOwner(site.Owner, store) || IsDeclaredOwner(site.Owner, owner)))
            .Select(site => site.Owner + ":" + site.Target.Name), [cleanup + ":DisposeAsync"]);

        var property = store.Properties.SingleOrDefault(item => item.Name == "HasUnconfirmedCleanup");
        if (property is null || property.SetMethod is not null || property.GetMethod is null
            || !HasAttachedDeclaringType(property.GetMethod) || MethodId(property.GetMethod) != observation
            || property.PropertyType.FullName != "System.Boolean")
            offenders.Add(store.FullName + ": cleanup observation must remain getter-only Boolean memory state");
        var fields = store.Fields.Where(field => field.Name == "_cleanupUnconfirmed").ToArray();
        if (fields.Length != 1 || !fields[0].IsPrivate || fields[0].IsStatic || fields[0].FieldType.FullName != "System.Boolean")
            offenders.Add(store.FullName + ": cleanup seal must remain one private instance Boolean");
        var accesses = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions
            .Where(instruction => instruction.Operand is FieldReference field && TargetsDeclaredType(field, store)
                && field.Name == "_cleanupUnconfirmed")
            .Select(instruction => (Owner: MethodId(item.Owner), Instruction: instruction))).ToArray();
        CheckSet(offenders, store.FullName + " monotonic cleanup seal access",
            accesses.Select(item => item.Owner + ":" + item.Instruction.OpCode.Name),
            [observation + ":ldfld", reader + ":ldfld", retain + ":stfld"]);
        if (accesses.Any(item => item.Instruction.OpCode == OpCodes.Stfld
                && item.Instruction.Previous?.OpCode != OpCodes.Ldc_I4_1))
            offenders.Add(store.FullName + ": cleanup seal may only be set true");
        var getter = store.Methods.SingleOrDefault(method => MethodId(method) == observation);
        if (getter is not null)
        {
            CheckSet(offenders, store.FullName + " memory-only cleanup observation calls", MethodOperands(getter)
                .Select(target => target.DeclaringType?.FullName + ":" + target.Name),
                ["System.Threading.Monitor:Enter", "System.Threading.Monitor:Exit"]);
            CheckSet(offenders, store.FullName + " memory-only cleanup observation fields", getter.Body.Instructions
                .Where(instruction => instruction.Operand is FieldReference).Select(instruction =>
                    ((FieldReference)instruction.Operand).FullName),
                [$"System.Object {store.FullName}::{(bridge ? "_acquisitionGate" : "_connectionGate")}",
                 $"System.Boolean {store.FullName}::_cleanupUnconfirmed"]);
        }

        void CheckCallers(TypeDefinition type, string method, IEnumerable<string> expected) =>
            CheckSet(offenders, name + " " + type.Name + "." + method + " callers", sites
                .Where(site => TargetsDeclaredType(site.Target, type) && site.Target.Name == method)
                .Select(site => site.Owner), expected);
    }

    public sealed class CleanupOwnerMutationTests
    {
        [Fact]
        public void Cleanup_owner_census_rejects_extra_acquisition_disposal_capture_and_observation_paths()
        {
            using var census = ProductionCensus.Read();
            EvaluateDetector(census, CredentialOffenders).Should().BeEmpty("the three actual cleanup owners are nonempty clean subjects");
            foreach (var name in new[] { Store, DeliveryStore, JobBridgeStore })
            {
                var store = census.Types[(InfrastructureAssembly, name)];
                var owner = census.Types[(InfrastructureAssembly, name + "/OwnedConnection")];
                var reader = store.Methods.Single(method => method.Name == "OpenConnectionAsync");
                var getter = store.Methods.Single(method => method.Name == "get_HasUnconfirmedCleanup");
                var retain = store.Methods.Single(method => method.Name == "RetainConnection");
                var capture = owner.Fields.Single(field => field.Name == "<owner>P");
                var seal = store.Fields.Single(field => field.Name == "_cleanupUnconfirmed");
                AssertMutation(census, "a public cleanup holder", () =>
                    ChangeAttributes(() => owner.Attributes, value => owner.Attributes = value,
                        (owner.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedPublic), CredentialOffenders);
                AssertMutation(census, "an unsealed cleanup holder", () =>
                    ChangeAttributes(() => owner.Attributes, value => owner.Attributes = value, owner.Attributes & ~TypeAttributes.Sealed), CredentialOffenders);
                AssertMutation(census, "a missing actual cleanup holder", () =>
                {
                    store.NestedTypes.Remove(owner);
                    return () => store.NestedTypes.Add(owner);
                }, CredentialOffenders);
                AssertMutation(census, "an extra acquisition helper inside the cleanup holder", () =>
                {
                    var method = AddCall(owner, "AdditionalAcquisition", reader, OpCodes.Call);
                    return () => owner.Methods.Remove(method);
                }, CredentialOffenders);
                AssertMutation(census, "an extra concrete disposal helper on an approved owner", () =>
                {
                    var target = owner.Module.ImportReference(typeof(NpgsqlConnection).GetMethod(nameof(NpgsqlConnection.DisposeAsync), Type.EmptyTypes)!);
                    var method = AddCall(owner, "AdditionalDisposal", target, OpCodes.Callvirt);
                    return () => owner.Methods.Remove(method);
                }, CredentialOffenders);
                foreach (var opcode in new[] { OpCodes.Ldfld, OpCodes.Ldflda, OpCodes.Stfld })
                    AssertMutation(census, "an extra cleanup-owner capability capture access through " + opcode, () =>
                    {
                        var method = owner.Methods.Single(item => item.Name == ".ctor");
                        var instruction = Instruction.Create(opcode, capture);
                        method.Body.Instructions.Add(instruction);
                        return () => method.Body.Instructions.Remove(instruction);
                    }, CredentialOffenders);
                var write = retain.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Stfld
                    && instruction.Operand is FieldReference field && field.Name == seal.Name);
                AssertMutation(census, "a cleanup seal reset instead of monotonic failure", () =>
                    ReplaceInstruction(write.Previous!, OpCodes.Ldc_I4_0, null!), CredentialOffenders);
                AssertMutation(census, "a cleanup observer that acquires physical SQL", () =>
                {
                    var instruction = Instruction.Create(OpCodes.Call, reader);
                    getter.Body.Instructions.Add(instruction);
                    return () => getter.Body.Instructions.Remove(instruction);
                }, CredentialOffenders);
                AssertMutation(census, "a writable cleanup observer", () =>
                {
                    var property = store.Properties.Single(item => item.Name == "HasUnconfirmedCleanup");
                    var setter = new MethodDefinition("set_HasUnconfirmedCleanup", MethodAttributes.Public, store.Module.TypeSystem.Void);
                    property.SetMethod = setter;
                    return () => property.SetMethod = null;
                }, CredentialOffenders);
                var clean = AddCall(owner, "ReadDeclaredRuntimeType", owner.Module.ImportReference(typeof(object).GetMethod(nameof(GetType))!), OpCodes.Callvirt);
                try { EvaluateDetector(census, CredentialOffenders).Should().BeEmpty("a memory-only helper grants no acquisition, disposal or store capability"); }
                finally { owner.Methods.Remove(clean); }
            }
        }
    }
}
