using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string RcControl = "LearnStack.Infrastructure.Jobs.BackgroundJobRecurrenceControl";
    private const string RcRecipient = "LearnStack.Infrastructure.Jobs.BackgroundJobRecipientOperation";
    private const string RcNative = "LearnStack.Infrastructure.Jobs.BackgroundJobNativeRecurrenceOperation";
    private const string RcClient = "LearnStack.Infrastructure.Jobs.HangfireRecurrenceClient";
    private const string RcRegistry = "LearnStack.Infrastructure.Jobs.BackgroundJobRecurrenceRegistry";
    private const string RcManifest = "LearnStack.Infrastructure.Jobs.BackgroundJobRecurrenceDescriptor";

    /// <summary>ADR-0047/0059: only the concrete retained owner may create, start or stop each recurrence capability.</summary>
    [Fact]
    public void Background_job_recurrence_runtime_keeps_exact_concrete_capability_owners()
    {
        using var census = ProductionCensus.Read();
        var offenders = RecurrenceRuntimeOffenders(census);
        offenders.Should().BeEmpty(
            "Fix: preserve exact recurrence construction, callback, stop and actual-cleanup ownership. "
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>ADR-0047: nonempty clean/dirty/restored subjects pin actual opcodes and positive joins.</summary>
    [Theory]
    [InlineData("constructor_visibility")]
    [InlineData("callback_signature")]
    [InlineData("extra_caller")]
    [InlineData("foreign_target")]
    [InlineData("foreign_cleanup")]
    [InlineData("foreign_await")]
    [InlineData("delegate_escape")]
    [InlineData("parent_join")]
    [InlineData("native_stop")]
    [InlineData("recipient_cancel_join")]
    [InlineData("thread_exit")]
    [InlineData("sdk_queue")]
    [InlineData("sdk_cron")]
    [InlineData("sdk_tick_type")]
    [InlineData("sdk_manager")]
    [InlineData("sdk_payload")]
    [InlineData("sdk_misfire")]
    [InlineData("sdk_arity")]
    [InlineData("sdk_key_slot")]
    [InlineData("sdk_extra_caller")]
    [InlineData("sdk_interface_caller")]
    [InlineData("sdk_swapped_literals")]
    [InlineData("ownership_extra_holder")]
    [InlineData("ownership_public_field")]
    [InlineData("ownership_published_type")]
    [InlineData("ownership_forwarded_field")]
    [InlineData("ownership_completion_state")]
    [InlineData("ownership_foreign_resource")]
    [InlineData("ownership_foreign_generic")]
    [InlineData("ownership_forwarded_stop")]
    [InlineData("ownership_forwarded_manager")]
    [InlineData("memory_wake")]
    [InlineData("memory_field")]
    [InlineData("memory_helper")]
    [InlineData("memory_indirect")]
    [InlineData("memory_scheduling")]
    [InlineData("memory_tick_type")]
    [InlineData("memory_vendor_identity")]
    [InlineData("memory_readiness_wait")]
    [InlineData("memory_extra_caller")]
    [InlineData("exclusion_sql")]
    [InlineData("exclusion_typed_array")]
    [InlineData("exclusion_clone")]
    [InlineData("exclusion_limit")]
    [InlineData("exclusion_delegated_binding")]
    [InlineData("exclusion_foreign_binding")]
    [InlineData("publication_before_start")]
    [InlineData("publication_dispose_before_join_ack")]
    public void Background_job_recurrence_runtime_controls_reject_changed_authority_and_cleanup(string mutation)
    {
        using var census = ProductionCensus.Read();
        var types = census.Types;
        AssertMutation(census, mutation, () =>
        {
            if (mutation.StartsWith("sdk_", StringComparison.Ordinal)) return MutateRecurrenceSdk(census, mutation);
            if (mutation.StartsWith("ownership_", StringComparison.Ordinal)) return MutateRecurrenceOwnership(census, mutation);
            if (mutation.StartsWith("memory_", StringComparison.Ordinal)) return MutateRecurrenceMemory(census, mutation);
            if (mutation.StartsWith("exclusion_", StringComparison.Ordinal) || mutation.StartsWith("publication_", StringComparison.Ordinal))
                return MutateRecurrencePublication(census, mutation);
            var recipient = types[(InfrastructureAssembly, RcRecipient)];
            if (mutation == "constructor_visibility")
            {
                var constructor = recipient.Methods.Single(method => method.IsConstructor);
                return ChangeAttributes(() => constructor.Attributes, value => constructor.Attributes = value,
                    (constructor.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public);
            }
            if (mutation == "callback_signature")
            {
                var callback = types[(InfrastructureAssembly, RcNative)].Methods.Single(method => method.Name == "StartThread");
                var original = callback.ReturnType;
                callback.ReturnType = callback.Module.TypeSystem.Int32;
                return () => callback.ReturnType = original;
            }
            if (mutation == "extra_caller")
            {
                var owner = types[(InfrastructureAssembly, RcControl)];
                var extra = new MethodDefinition("UnlistedRecurrenceStart", MethodAttributes.Private,
                    owner.Module.TypeSystem.Void);
                owner.Methods.Add(extra);
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Call,
                    types[(InfrastructureAssembly, RcNative)].Methods.Single(method => method.Name == "Start")));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                return () => owner.Methods.Remove(extra);
            }
            if (mutation == "foreign_await")
            {
                var module = types[(DeliveryApplication, RecipientClaim)].Module;
                var outer = new TypeDefinition("System.Runtime.CompilerServices", "ConfiguredTaskAwaitable",
                    TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Class, module.TypeSystem.Object);
                var nested = new TypeDefinition("", "ConfiguredTaskAwaiter",
                    TypeAttributes.NestedPublic | TypeAttributes.Sealed | TypeAttributes.Class, module.TypeSystem.Object);
                outer.NestedTypes.Add(nested);
                module.Types.Add(outer);
                var method = new MethodDefinition("GetResult", MethodAttributes.Public, module.TypeSystem.Void);
                nested.Methods.Add(method);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var calls = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == RcNative
                        && item.Owner.Name == "ObserveAsync").SelectMany(item => item.Body.Body.Instructions)
                    .Where(item => item.Operand is MethodReference target
                        && target.DeclaringType.FullName == "System.Runtime.CompilerServices.ConfiguredTaskAwaitable/ConfiguredTaskAwaiter"
                        && target.Name == "GetResult").ToArray();
                calls.Should().HaveCount(2, "both non-generic awaits are real subjects before substituting one");
                var original = calls[0].Operand;
                calls[0].Operand = method;
                return () => { calls[0].Operand = original; module.Types.Remove(outer); };
            }
            if (mutation is "foreign_target" or "foreign_cleanup")
            {
                var module = types[(DeliveryApplication, RecipientClaim)].Module;
                var cleanup = mutation == "foreign_cleanup";
                var foreign = new TypeDefinition(cleanup ? "System.Threading" : "LearnStack.Infrastructure.Jobs",
                    cleanup ? "Thread" : "BackgroundJobNativeRecurrenceOperation",
                    TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Class, module.TypeSystem.Object);
                module.Types.Add(foreign);
                var method = new MethodDefinition(cleanup ? "get_IsAlive" : "Start", MethodAttributes.Assembly,
                    cleanup ? module.TypeSystem.Boolean : module.TypeSystem.Void);
                foreign.Methods.Add(method);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var call = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == (cleanup ? RcNative : RcControl)
                        && item.Owner.Name == (cleanup ? "ObserveAsync" : "RegisterAsync")).SelectMany(item => item.Body.Body.Instructions)
                    .Single(item => item.Operand is MethodReference target
                        && target.DeclaringType.FullName == (cleanup ? "System.Threading.Thread" : RcNative)
                        && target.Name == (cleanup ? "get_IsAlive" : "Start"));
                var original = call.Operand;
                call.Operand = method;
                return () => { call.Operand = original; module.Types.Remove(foreign); };
            }
            var (ownerType, ownerMethod, targetType, targetMethod) = mutation switch
            {
                "delegate_escape" => (RcControl, "RegisterAsync", RcNative, "Start"),
                "parent_join" => (OrchestrationControl, "ObserveCompletionAsync", RcControl, "get_Completion"),
                "native_stop" => (RcControl, "RequestStop", RcNative, "RequestStop"),
                "recipient_cancel_join" => (RcRecipient, "ObserveAsync", "System.Threading.Tasks.Task", "WhenAll"),
                "thread_exit" => (RcNative, "ObserveAsync", "System.Threading.Thread", "get_IsAlive"),
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            };
            var instruction = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == ownerType
                    && item.Owner.Name == ownerMethod).SelectMany(item => item.Body.Body.Instructions)
                .Single(item => item.Operand is MethodReference target
                    && target.DeclaringType.FullName == targetType && target.Name == targetMethod);
            return mutation == "delegate_escape" ? ReplaceInstruction(instruction, OpCodes.Ldftn, instruction.Operand)
                : ReplaceInstruction(instruction, OpCodes.Nop, null!);
        }, RecurrenceRuntimeOffenders);
    }

    private static SortedSet<string> RecurrenceRuntimeOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        foreach (var name in new[] { RcControl, RcRecipient, RcNative, RcClient })
        {
            if (!types.TryGetValue((InfrastructureAssembly, name), out var subject))
                offenders.Add("Missing actual recurrence owner: " + name);
            else if (!subject.IsNotPublic || !subject.IsSealed)
                offenders.Add("Recurrence capability owner must remain internal sealed: " + name);
        }
        if (offenders.Count != 0) return offenders;
        var bodies = OwnedBodies(census).ToArray();
        var sites = bodies.SelectMany(item => item.Body.Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => (Owner: MethodId(item.Owner), Instruction: instruction,
                Target: (MethodReference)instruction.Operand))).ToArray();
        RecordDetachedTargets(offenders, CallSites(census));
        if (offenders.Count != 0) return offenders;

        Cap(RcControl, ".ctor", "System.Void",
            ["LearnStack.Infrastructure.Persistence.PrivilegedDataSource", "LearnStack.Infrastructure.Jobs.BackgroundJobDefinitionRegistry",
             RcRegistry, RecipientProjection, JobExecutorType, "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeGate",
             "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeOptions", "LearnStack.SharedKernel.Identifiers.IGuidFactory", "System.TimeProvider"],
            MethodAttributes.Assembly, [(OrchestrationControl, ".ctor", "newobj")]);
        Cap(RcRecipient, ".ctor", "System.Void",
            [RecipientClaim, JobExecutorType, RecipientProjection, "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeGate",
             "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeOptions", "System.TimeProvider"],
            MethodAttributes.Assembly, [(RcControl, "ClaimAsync", "newobj")]);
        Cap(RcNative, ".ctor", "System.Void",
            [RcManifest, RcClient, "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeGate", "System.TimeProvider"],
            MethodAttributes.Assembly, [(RcControl, "RegisterAsync", "newobj")]);
        Cap(RcClient, ".ctor", "System.Void", ["Hangfire.JobStorage", "Hangfire.Common.IJobFilterProvider"],
            MethodAttributes.Public, [(JobRuntime, "PreallocateOwners", "newobj")]);
        Cap(RcControl, "BindNativeClient", "System.Void", [RcClient], MethodAttributes.Assembly,
            [(OrchestrationControl, "BindRecurrenceClient", "callvirt")]);
        Cap(OrchestrationControl, "BindRecurrenceClient", "System.Void", [RcClient], MethodAttributes.Assembly,
            [(JobRuntime, "PreallocateOwners", "callvirt")]);
        Cap(OrchestrationControl, "SignalRecurrenceTick", "System.Void", ["System.String", "System.Int64"], MethodAttributes.Assembly,
            [(RcBound, "RunTickAsync", "callvirt")]);
        Cap(RcBound, "RunTickAsync", BoundaryTask, ["System.String", "System.Int64"], MethodAttributes.Assembly,
            [(RcTick, "RunAsync", "callvirt")]);
        Cap(RcControl, "Start", "System.Void", [], MethodAttributes.Assembly,
            [(OrchestrationControl, "ScheduleLoops", "callvirt")]);
        Cap(RcControl, "RequestStop", "System.Void", [], MethodAttributes.Assembly,
            [(OrchestrationControl, "RequestStop", "callvirt")]);
        Cap(RcControl, "get_Completion", BoundaryTask, [], MethodAttributes.Assembly,
            [(OrchestrationControl, "ObserveCompletionAsync", "callvirt"), (RcControl, "get_Snapshot", "call")]);
        Cap(RcControl, "SignalTick", "System.Void", ["System.String", "System.Int64"], MethodAttributes.Assembly,
            [(OrchestrationControl, "SignalRecurrenceTick", "callvirt")]);
        Cap(RcRecipient, "Start", "System.Void", [], MethodAttributes.Assembly, [(RcControl, "ClaimAsync", "callvirt")]);
        Cap(RcRecipient, "RequestStop", "System.Void", [], MethodAttributes.Assembly,
            [(RcControl, "RequestStop", "callvirt"), (RcRecipient, "Start", "call"),
             (RcRecipient, "WaitAsync", "call"), (RcRecipient, "ObserveAsync", "call")]);
        Cap(RcNative, "Start", "System.Void", [], MethodAttributes.Assembly, [(RcControl, "RegisterAsync", "callvirt")]);
        Cap(RcNative, "RequestStop", "System.Void", [], MethodAttributes.Assembly,
            [(RcControl, "RequestStop", "call"), (RcNative, "Start", "call")]);
        Cap(RcClient, "Register", "System.Void", [RcManifest], MethodAttributes.Assembly,
            [(RcNative, "ExecuteNative", "callvirt")]);
        Cap(RcControl, "RunAsync", BoundaryTask, [], MethodAttributes.Private,
            [(RcControl, "Start", "ldftn"), (RcControl, "Start", "ldftn")]);
        Cap(RcRecipient, "ExecuteApplicationAsync", "System.Threading.Tasks.Task`1<" + RecipientSubmission + ">", [], MethodAttributes.Private,
            [(RcRecipient, "Start", "ldftn"), (RcRecipient, "Start", "ldftn")]);
        Cap(RcNative, "StartThread", "System.Void", [], MethodAttributes.Private,
            [(RcNative, "Start", "ldftn"), (RcNative, "Start", "ldftn")]);
        Cap(RcNative, "ExecuteNative", "System.Void", [], MethodAttributes.Private, [(RcNative, ".ctor", "ldftn")]);
        foreach (var (owner, methods) in new[]
                 {
                     (RcControl, new[] { "RelayStopAsync", "ObserveCompletionAsync" }),
                     (RcRecipient, new[] { "WaitAsync", "RelayCancellationAsync", "ObserveAsync" }),
                     (RcNative, new[] { "ObserveAsync" }),
                 })
            foreach (var method in methods)
                Cap(owner, method, BoundaryTask, [], MethodAttributes.Private, [(owner, ".ctor", "call")]);

        RequireCall(RcRecipient, "ObserveAsync", "System.Threading.Tasks.Task", "WhenAll", "System.Runtime",
            "System.Threading.Tasks.Task", ["System.ReadOnlySpan`1<System.Threading.Tasks.Task>"]);
        RequireCall(RcNative, "ObserveAsync", "System.Threading.Thread", "get_IsAlive", "System.Threading.Thread",
            "System.Boolean", []);
        const string configured = "System.Runtime.CompilerServices.ConfiguredTaskAwaitable/ConfiguredTaskAwaiter";
        RequireAwaits(RcRecipient, "ObserveAsync", [GenericAwaiter("System.Threading.Tasks.Task`1<" + RecipientSubmission + ">"),
            GenericAwaiter(RecipientSubmission), configured]);
        RequireAwaits(RcNative, "ObserveAsync", [GenericAwaiter(BoundaryTask), configured,
            GenericAwaiter("System.Nullable`1<System.Boolean>"), configured]);
        RequireAwaits(RcControl, "ObserveCompletionAsync", [GenericAwaiter(BoundaryTask), configured, configured, configured]);
        offenders.UnionWith(RecurrenceSdkOffenders(census));
        offenders.UnionWith(RecurrenceOwnershipOffenders(census));
        offenders.UnionWith(RecurrenceMemoryOffenders(census));
        offenders.UnionWith(RecurrencePublicationOffenders(census));
        return offenders;

        void Cap(string type, string name, string result, string[] parameters, MethodAttributes visibility,
            (string Type, string Method, string Opcode)[] expected)
        {
            var subject = types[(InfrastructureAssembly, type)];
            var methods = subject.Methods.Where(method => method.Name == name).ToArray();
            if (methods.Length != 1 || methods[0].ReturnType.FullName != result || !ParametersMatch(methods[0], parameters)
                || methods[0].IsStatic || methods[0].IsVirtual || methods[0].HasGenericParameters
                || (methods[0].Attributes & MethodAttributes.MemberAccessMask) != visibility)
                offenders.Add("Recurrence capability requires its exact signature and visibility: " + type + "." + name);
            CheckSet(offenders, "closed recurrence capability " + type + "." + name,
                sites.Where(site => site.Target.DeclaringType.FullName == type && site.Target.Name == name)
                    .Select(site => site.Owner + ":" + site.Instruction.OpCode.Name),
                expected.Select(owner => JobOwner(offenders, types, InfrastructureAssembly, owner.Type, owner.Method)
                    + ":" + owner.Opcode));
            if (methods.Length == 1 && sites.Any(site => site.Target.DeclaringType.FullName == type
                    && site.Target.Name == name && (!TargetsDeclaredType(site.Target, subject)
                        || MethodId(site.Target) != MethodId(methods[0]))))
                offenders.Add("Recurrence capability targets its exact declared assembly and method: " + type + "." + name);
        }
        void RequireCall(string owner, string method, string targetType, string targetName, string assembly,
            string result, string[] parameters)
        {
            var id = JobOwner(offenders, types, InfrastructureAssembly, owner, method);
            var found = sites.Where(site => site.Owner == id && site.Target.DeclaringType.FullName == targetType
                && site.Target.Name == targetName).ToArray();
            if (found.Length != 1 || !TrustedBcl(found[0].Target, assembly) || found[0].Target.ReturnType.FullName != result
                || !ParametersMatch(found[0].Target, parameters) || found[0].Target.HasGenericParameters
                || found[0].Target.HasThis != (targetName == "get_IsAlive")
                || found[0].Instruction.OpCode != (targetName == "get_IsAlive" ? OpCodes.Callvirt : OpCodes.Call))
                offenders.Add("Actual recurrence cleanup lost exact " + owner + "." + method + " -> " + targetType + "." + targetName
                    + "; actual [" + string.Join(", ", found.Select(site => MethodId(site.Target) + ":" + site.Instruction.OpCode.Name)) + "]");
        }
        void RequireAwaits(string owner, string method, string[] awaiters)
        {
            var id = JobOwner(offenders, types, InfrastructureAssembly, owner, method);
            var found = sites.Where(site => site.Owner == id && site.Target.Name == "GetResult").ToArray();
            CheckSet(offenders, "actual recurrence awaited results " + owner + "." + method,
                found.Select(site => MethodId(site.Target) + ":" + site.Instruction.OpCode.Name), awaiters.Select(awaiter =>
                    "System.Runtime:" + (awaiter == configured ? "System.Void" : "!0") + " " + awaiter + "::GetResult():call"));
            if (found.Any(site => !TrustedBcl(site.Target, "System.Runtime") || !site.Target.HasThis
                    || site.Target.HasGenericParameters || site.Target.Parameters.Count != 0))
                offenders.Add("Actual recurrence awaits require trusted exact instance awaiters: " + owner + "." + method);
        }
        static string GenericAwaiter(string argument) =>
            "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1/ConfiguredTaskAwaiter<" + argument + ">";
        static bool TrustedBcl(MethodReference target, string assembly) => target.DeclaringType.Scope is AssemblyNameReference scope
            && scope.Name == assembly && scope.Version == new Version(10, 0, 0, 0)
            && Convert.ToHexString(scope.PublicKeyToken) == "B03F5F7F11D50A3A";
    }
}
