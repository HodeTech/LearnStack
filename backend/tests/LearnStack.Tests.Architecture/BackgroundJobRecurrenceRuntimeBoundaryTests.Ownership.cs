using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string RcPending = RcControl + "/PendingRecipient";
    private const string RcSignal = "System.Threading.Tasks.TaskCompletionSource";
    private static readonly (string Type, string[] Names)[] RecurrenceFieldNames =
    [
        (RcControl, ["ApplicationOwnerLimit", "_sync", "_store", "_definitions", "_registry", "_projection", "_executor", "_runtime", "_bounds", "_time", "_recipients", "_nativeAfter", "_deploymentMismatch", "_nativeFailures", "_faults", "_stop", "_stopRequested", "_loopPublished", "_completion", "_stopRelay", "_completionObservation", "_client", "_native", "_sqlFlight", "_sqlOperation", "_started", "_stopping", "_cleanupFault", "_quarantined", "_progress", "_occurrences", "_skippedSlots", "_pages", "_routesExamined", "_recipientsInserted", "_fanoutsCompleted", "_turn", "_keyCursor", "_historyCursor", "_tick"]),
        (RcRecipient, ["_executor", "_projection", "_bounds", "_time", "_cancellation", "_published", "_returned", "_issued", "_stop", "_completion", "_wait", "_cancel", "_observation", "_scheduled", "_retained", "_cleanupFailed", "_result", "<Claim>k__BackingField", "<Gate>k__BackingField"]),
        (RcNative, ["_client", "_runtime", "_published", "_result", "_completion", "_thread", "_observation", "_time", "_stopped", "_scheduled", "<Manifest>k__BackingField", "<StartedAt>k__BackingField"]),
        (RcClient, ["_manager"]),
        (RcPending, ["<Operation>k__BackingField", "<After>k__BackingField"]),
    ];

    private static SortedSet<string> RecurrenceOwnershipOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        if (RecurrenceFieldNames.Any(item => !types.ContainsKey((InfrastructureAssembly, item.Type))))
        { offenders.Add("Missing actual recurrence field owner"); return offenders; }
        var bodies = OwnedBodies(census).ToArray();
        var sites = bodies.SelectMany(item => item.Body.Body.Instructions.Where(instruction => instruction.Operand is FieldReference)
            .Select(instruction => (item.Owner, Instruction: instruction, Field: (FieldReference)instruction.Operand))).ToArray();
        foreach (var (type, names) in RecurrenceFieldNames)
        {
            var subject = types[(InfrastructureAssembly, type)];
            CheckSet(offenders, "closed recurrence field inventory " + type, subject.Fields.Select(field => field.Name), names);
            if (subject.Fields.Any(field => field.Name != "ApplicationOwnerLimit" && (!field.IsPrivate || field.IsStatic)))
                offenders.Add("Recurrence state remains private instance state: " + type);
            if (subject.Fields.Any(field => !ExactResourceScopes(field.FieldType)))
                offenders.Add("Recurrence fields retain their exact declared resource assemblies: " + type);
        }
        var limit = types[(InfrastructureAssembly, RcControl)].Fields.SingleOrDefault(field => field.Name == "ApplicationOwnerLimit");
        if (limit is not { IsLiteral: true, IsStatic: true, Constant: 4 })
            offenders.Add("The recurrence application owner ceiling remains exactly four");

        Field(RcControl, "_store", "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", true,
            ".ctor:stfld ClaimAsync:ldfld RegisterAsync:ldfld ResolveOneAsync:ldfld RunAsync:ldfld RunAsync:ldfld");
        Field(RcControl, "_definitions", "LearnStack.Infrastructure.Jobs.BackgroundJobDefinitionRegistry", true, ".ctor:stfld RegisterAsync:ldfld");
        Field(RcControl, "_registry", RcRegistry, true,
            ".ctor:stfld get_Snapshot:ldfld Start:ldfld Start:ldfld SignalTick:ldfld ClaimAsync:ldfld RegisterAsync:ldfld RegisterAsync:ldfld RegisterAsync:ldfld RunAsync:ldfld RunAsync:ldfld");
        Field(RcControl, "_projection", RecipientProjection, true, ".ctor:stfld ClaimAsync:ldfld ResolveOneAsync:ldfld RunAsync:ldfld");
        Field(RcControl, "_executor", JobExecutorType, true, ".ctor:stfld ClaimAsync:ldfld");
        Field(RcControl, "_runtime", "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeGate", true,
            ".ctor:stfld SignalTick:ldfld MayStart:ldfld ClaimAsync:ldfld ClaimAsync:ldfld RegisterAsync:ldfld RegisterAsync:ldfld");
        Field(RcControl, "_bounds", "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeOptions", true,
            ".ctor:stfld get_Snapshot:ldfld get_Snapshot:ldfld ClaimAsync:ldfld RegisterAsync:ldfld ResolveOneAsync:ldfld RunAsync:ldfld");
        Field(RcControl, "_time", "System.TimeProvider", true,
            ".ctor:stfld get_Snapshot:ldfld get_Snapshot:ldfld get_Snapshot:ldfld get_Snapshot:ldfld Start:ldfld Later:ldfld Later:ldfld <ResolveOneAsync>b__52_0:ldfld ClaimAsync:ldfld ObserveSqlAsync:ldfld RegisterAsync:ldfld RegisterAsync:ldfld RunAsync:ldfld");
        foreach (var counter in new[] { "_occurrences", "_skippedSlots", "_pages", "_routesExamined", "_recipientsInserted", "_fanoutsCompleted" })
            Field(RcControl, counter, "System.Int64", false, "get_Snapshot:ldfld ObserveProgress:ldfld ObserveProgress:stfld");

        Field(RcControl, "_recipients", "System.Collections.Generic.Dictionary`2<System.Guid," + RcPending + ">", true,
            ".ctor:stfld get_Snapshot:ldfld get_Snapshot:ldfld get_Snapshot:ldfld RequestStop:ldfld ClaimAsync:ldfld ClaimAsync:ldfld ClaimAsync:ldfld ClaimAsync:ldfld ObserveCompletionAsync:ldfld ResolveOneAsync:ldfld ResolveOneAsync:ldfld ResolveOneAsync:ldfld ResolveOneAsync:ldfld");
        Field(RcControl, "_native", RcNative, false,
            "get_Snapshot:ldfld get_Snapshot:ldfld RequestStop:ldfld ObserveNative:ldfld ObserveNative:stfld ObserveCompletionAsync:ldfld RegisterAsync:ldfld RegisterAsync:stfld");
        Field(RcControl, "_client", RcClient, false, "BindNativeClient:ldfld BindNativeClient:stfld Start:ldfld RegisterAsync:ldfld RegisterAsync:ldfld");
        Field(RcControl, "_sqlFlight", BoundaryTask, false, "ObserveSqlAsync:stfld ObserveSqlAsync:stfld");
        Field(RcControl, "_sqlOperation", "System.Object", false, "ObserveSqlAsync:stfld ObserveSqlAsync:stfld");
        Field(RcControl, "_loopPublished", RcSignal + "`1<" + BoundaryTask + ">", true,
            ".ctor:stfld Start:ldfld RequestStop:ldfld ObserveCompletionAsync:ldfld");
        Field(RcControl, "_stopRequested", RcSignal, true, ".ctor:stfld RequestStop:ldfld RelayStopAsync:ldfld");
        Field(RcControl, "_stop", "System.Threading.CancellationTokenSource", true,
            ".ctor:stfld ClaimAsync:ldfld ObserveCompletionAsync:ldfld RegisterAsync:ldfld RelayStopAsync:ldfld ResolveOneAsync:ldfld RunAsync:ldfld RunAsync:ldfld RunAsync:ldfld RunAsync:ldfld");
        Field(RcControl, "_stopRelay", BoundaryTask, true, ".ctor:stfld ObserveCompletionAsync:ldfld");
        Field(RcControl, "_completionObservation", BoundaryTask, true, ".ctor:stfld");
        Field(RcPending, "<Operation>k__BackingField", RcRecipient, true, ".ctor:stfld get_Operation:ldfld");
        Field(RcRecipient, "_published", RcSignal + "`1<System.Threading.Tasks.Task`1<" + RecipientSubmission + ">>", true,
            ".ctor:stfld Start:ldfld ObserveAsync:ldfld");
        Field(RcRecipient, "_issued", RcSignal, true, ".ctor:stfld Start:ldfld WaitAsync:ldfld");
        Field(RcRecipient, "_returned", RcSignal, true, ".ctor:stfld ObserveAsync:ldfld WaitAsync:ldfld WaitAsync:ldfld WaitAsync:ldfld");
        Field(RcRecipient, "_stop", RcSignal, true, ".ctor:stfld RequestStop:ldfld RelayCancellationAsync:ldfld WaitAsync:ldfld");
        Field(RcRecipient, "_cancellation", "System.Threading.CancellationTokenSource", true,
            ".ctor:stfld ExecuteApplicationAsync:ldfld ObserveAsync:ldfld RelayCancellationAsync:ldfld");
        Field(RcRecipient, "_executor", JobExecutorType, true, ".ctor:stfld ExecuteApplicationAsync:ldfld");
        Field(RcRecipient, "_projection", RecipientProjection, true, ".ctor:stfld ExecuteApplicationAsync:ldfld");
        Field(RcRecipient, "_bounds", "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeOptions", true, ".ctor:stfld WaitAsync:ldfld");
        Field(RcRecipient, "_time", "System.TimeProvider", true, ".ctor:stfld WaitAsync:ldfld");
        foreach (var field in new[] { "_wait", "_cancel" })
            Field(RcRecipient, field, BoundaryTask, true, ".ctor:stfld ObserveAsync:ldfld");
        Field(RcRecipient, "_observation", BoundaryTask, true, ".ctor:stfld");
        Field(RcRecipient, "<Claim>k__BackingField", RecipientClaim, true, ".ctor:stfld get_Claim:ldfld");
        Field(RcRecipient, "<Gate>k__BackingField", JobCommitGate, true, ".ctor:stfld get_Gate:ldfld");
        Field(RcNative, "_published", RcSignal + "`1<" + BoundaryTask + ">", true, ".ctor:stfld Start:ldfld ObserveAsync:ldfld");
        Field(RcNative, "_result", RcSignal + "`1<System.Nullable`1<System.Boolean>>", true,
            ".ctor:stfld get_Result:ldfld get_Result:ldfld StartThread:ldfld StartThread:ldfld ExecuteNative:ldfld ObserveAsync:ldfld");
        Field(RcNative, "_thread", "System.Threading.Thread", true,
            ".ctor:stfld StartThread:ldfld StartThread:ldfld StartThread:ldfld StartThread:ldfld ObserveAsync:ldfld");
        Field(RcNative, "_client", RcClient, true, ".ctor:stfld ExecuteNative:ldfld");
        Field(RcNative, "_runtime", "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeGate", true,
            ".ctor:stfld StartThread:ldfld ExecuteNative:ldfld");
        Field(RcNative, "_time", "System.TimeProvider", true, ".ctor:stfld ObserveAsync:ldfld");
        Field(RcNative, "_observation", BoundaryTask, true, ".ctor:stfld");
        Field(RcClient, "_manager", "Hangfire.RecurringJobManager", true, ".ctor:stfld Register:ldfld");
        foreach (var type in new[] { RcControl, RcRecipient, RcNative })
        {
            var observer = type == RcControl ? "ObserveCompletionAsync" : "ObserveAsync";
            Field(type, "_completion", RcSignal, true, ".ctor:stfld get_Completion:ldfld " + observer + ":ldfld");
            var create = types[(InfrastructureAssembly, type)].Methods.Single(method => method.IsConstructor);
            var writes = create.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Stfld
                && instruction.Operand is FieldReference field && field.Name == "_completion").ToArray();
            if (writes.Length != 1 || writes[0].Previous?.OpCode != OpCodes.Newobj
                || writes[0].Previous?.Operand is not MethodReference factory
                || factory.DeclaringType.FullName != RcSignal || Key(factory.DeclaringType).Assembly != "System.Runtime"
                || !ParametersMatch(factory, ["System.Object", "System.Threading.Tasks.TaskCreationOptions"])
                || writes[0].Previous?.Previous?.Previous?.OpCode != OpCodes.Ldarg_0)
                offenders.Add("Actual recurrence completion retains its concrete owner: " + type);
        }
        CheckHolders();
        return offenders;

        void Field(string type, string name, string expectedType, bool readOnly, string access)
        {
            var subject = types[(InfrastructureAssembly, type)];
            var fields = subject.Fields.Where(field => field.Name == name).ToArray();
            if (fields.Length != 1 || fields[0].FieldType.FullName != expectedType || fields[0].IsInitOnly != readOnly)
            { offenders.Add("Retain exact recurrence resource type and mutability: " + type + ":" + name); return; }
            var actual = sites.Where(site => site.Field.Name == name && site.Field.DeclaringType.FullName == type).ToArray();
            if (actual.Any(site => !TargetsDeclaredType(site.Field, subject)))
                offenders.Add("A recurrence resource targets its actual declared assembly: " + type + ":" + name);
            CheckSet(offenders, "actual recurrence resource access " + type + ":" + name,
                actual.Select(site => MethodId(site.Owner) + ":" + site.Instruction.OpCode.Name),
                access.Split(' ').Select(entry =>
                {
                    var split = entry.LastIndexOf(':');
                    return JobOwner(offenders, types, InfrastructureAssembly, type, entry[..split]) + entry[split..];
                }));
        }

        void CheckHolders()
        {
            var protectedTypes = new[] { RcControl, RcRecipient, RcNative, RcClient, RcPending };
            bool Holds(TypeReference type) => protectedTypes.Contains(type.FullName, StringComparer.Ordinal)
                || type is GenericInstanceType generic && generic.GenericArguments.Any(Holds)
                || type is TypeSpecification specification && Holds(specification.ElementType);
            var stateOwners = types.Values.SelectMany(type => type.Methods)
                .SelectMany(method => method.CustomAttributes.Where(attribute => attribute.AttributeType.FullName
                    == "System.Runtime.CompilerServices.AsyncStateMachineAttribute")
                    .Where(attribute => attribute.ConstructorArguments.FirstOrDefault().Value is TypeReference)
                    .Select(attribute => (State: Key((TypeReference)attribute.ConstructorArguments[0].Value), Owner: method)))
                .ToDictionary(item => item.State, item => item.Owner);
            var actual = types.Values.SelectMany(type => type.Fields).Where(field => Holds(field.FieldType)).Select(field =>
            {
                if (!ExactResourceScopes(field.FieldType))
                    offenders.Add("A recurrence holder names a foreign capability: " + field.FullName);
                var owner = stateOwners.TryGetValue(Key(field.DeclaringType), out var method)
                    ? MethodId(method) : TypeId(field.DeclaringType);
                return owner + "::" + field.Name + ":" + field.FieldType.FullName;
            });
            var expected = new List<string>
            {
                TypeId(types[(InfrastructureAssembly, OrchestrationControl)]) + "::_recurrence:" + RcControl,
                TypeId(types[(InfrastructureAssembly, RcControl)]) + "::_recipients:System.Collections.Generic.Dictionary`2<System.Guid," + RcPending + ">",
                TypeId(types[(InfrastructureAssembly, RcControl)]) + "::_client:" + RcClient,
                TypeId(types[(InfrastructureAssembly, RcControl)]) + "::_native:" + RcNative,
                TypeId(types[(InfrastructureAssembly, RcPending)]) + "::<Operation>k__BackingField:" + RcRecipient,
                TypeId(types[(InfrastructureAssembly, RcNative)]) + "::_client:" + RcClient,
            };
            foreach (var (type, methods) in new[]
            {
                (RcControl, new[] { "RunAsync", "ClaimAsync", "RegisterAsync", "ResolveOneAsync", "ObserveSqlAsync", "RelayStopAsync", "ObserveCompletionAsync" }),
                (RcRecipient, new[] { "WaitAsync", "RelayCancellationAsync", "ObserveAsync" }),
                (RcNative, new[] { "ObserveAsync" }),
            })
                foreach (var method in methods)
                    expected.Add(JobOwner(offenders, types, InfrastructureAssembly, type, method) + "::<>4__this:" + type);
            expected.Add(JobOwner(offenders, types, InfrastructureAssembly, RcControl, "ResolveOneAsync") + "::<pending>5__2:" + RcPending);
            // These four private compiler cache slots bind the named source callbacks; they are not general holders.
            var cache = RcControl + "/<>c";
            expected.Add(InfrastructureAssembly + ":" + cache + "::<>9__44_0:System.Func`2<" + RcPending + ",System.Boolean>");
            expected.Add(InfrastructureAssembly + ":" + cache + "::<>9__44_1:System.Func`2<" + RcPending + ",System.Boolean>");
            expected.Add(InfrastructureAssembly + ":" + cache + "::<>9__48_0:System.Func`2<" + RcPending + "," + RcRecipient + ">");
            expected.Add(InfrastructureAssembly + ":" + cache + "::<>9__59_0:System.Func`2<" + RcPending + "," + BoundaryTask + ">");
            CheckSet(offenders, "whole-production concrete recurrence holders", actual, expected);
        }

        static bool ExactResourceScopes(TypeReference reference)
        {
            if (reference is GenericParameter) return true;
            if (reference is GenericInstanceType generic && !generic.GenericArguments.All(ExactResourceScopes)) return false;
            if (reference is TypeSpecification specification) return ExactResourceScopes(specification.ElementType);
            var expected = reference.FullName switch
            {
                "System.Threading.Thread" => "System.Threading.Thread",
                _ when reference.Namespace == "System.Collections.Generic" && reference.Name is "Dictionary`2" or "HashSet`1" => "System.Collections",
                _ when reference.FullName.StartsWith("System.", StringComparison.Ordinal) => "System.Runtime",
                _ when reference.FullName.StartsWith("LearnStack.Infrastructure.", StringComparison.Ordinal) => InfrastructureAssembly,
                _ when reference.FullName.StartsWith("LearnStack.SharedKernel.", StringComparison.Ordinal) => "LearnStack.SharedKernel",
                _ when reference.FullName.StartsWith("LearnStack.Application.", StringComparison.Ordinal) => DeliveryApplication,
                "Hangfire.RecurringJobManager" => "Hangfire.Core",
                _ => "",
            };
            if (Key(reference).Assembly != expected) return false;
            if (expected.StartsWith("System.", StringComparison.Ordinal))
                return reference.Scope is AssemblyNameReference framework && framework.Version == new Version(10, 0, 0, 0)
                    && Convert.ToHexString(framework.PublicKeyToken) == "B03F5F7F11D50A3A";
            if (expected == "Hangfire.Core")
                return reference.Scope is AssemblyNameReference vendor && vendor.Version == new Version(1, 8, 25, 0)
                    && vendor.PublicKeyToken.Length == 0;
            return true;
        }
    }

    private static Action MutateRecurrenceOwnership(ProductionCensus census, string mutation)
    {
        var owner = census.Types[(InfrastructureAssembly, RcRecipient)];
        if (mutation == "ownership_extra_holder")
        {
            var field = new FieldDefinition("UnlistedRecipientHolder", FieldAttributes.Private,
                new ArrayType(owner));
            census.Types[(InfrastructureAssembly, JobRuntime)].Fields.Add(field);
            return () => census.Types[(InfrastructureAssembly, JobRuntime)].Fields.Remove(field);
        }
        var fieldName = mutation == "ownership_published_type" ? "_published" : "_completion";
        var subject = owner.Fields.Single(field => field.Name == fieldName);
        if (mutation == "ownership_public_field")
            return ChangeAttributes(() => subject.Attributes, value => subject.Attributes = value,
                (subject.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public);
        if (mutation == "ownership_published_type")
        {
            var original = subject.FieldType;
            subject.FieldType = owner.Module.TypeSystem.Object;
            return () => subject.FieldType = original;
        }
        if (mutation is "ownership_foreign_resource" or "ownership_foreign_generic")
        {
            var resource = owner.Fields.Single(field => field.Name == (mutation == "ownership_foreign_resource" ? "_cancellation" : "_published"));
            if (mutation == "ownership_foreign_generic")
            {
                var inner = (GenericInstanceType)((GenericInstanceType)resource.FieldType).GenericArguments[0];
                var original = inner.GenericArguments[0];
                inner.GenericArguments[0] = new TypeReference(original.Namespace, original.Name, owner.Module,
                    census.Types[(DeliveryApplication, RecipientClaim)].Module);
                return () => inner.GenericArguments[0] = original;
            }
            var prior = resource.FieldType;
            resource.FieldType = new TypeReference(prior.Namespace, prior.Name, owner.Module,
                census.Types[(DeliveryApplication, RecipientClaim)].Module);
            return () => resource.FieldType = prior;
        }
        if (mutation is "ownership_forwarded_stop" or "ownership_forwarded_manager")
            return AddBoundaryFieldGetter(census.Types[(InfrastructureAssembly, mutation == "ownership_forwarded_stop" ? RcControl : RcClient)],
                "ForwardRecurrenceResource", mutation == "ownership_forwarded_stop" ? "_stop" : "_manager");
        if (mutation == "ownership_forwarded_field")
            return AddBoundaryFieldGetter(owner, "ForwardRecurrenceCompletion", subject.Name);
        if (mutation == "ownership_completion_state")
        {
            var write = owner.Methods.Single(method => method.IsConstructor).Body.Instructions.Single(instruction =>
                instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field && field.Name == "_completion");
            return ReplaceInstruction(write.Previous.Previous.Previous, OpCodes.Ldnull, null!);
        }
        throw new ArgumentOutOfRangeException(nameof(mutation));
    }
}
