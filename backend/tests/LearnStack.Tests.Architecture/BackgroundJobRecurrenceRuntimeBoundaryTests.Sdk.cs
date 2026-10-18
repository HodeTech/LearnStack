using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private static Action MutateRecurrenceSdk(ProductionCensus census, string mutation)
    {
        var type = census.Types[(InfrastructureAssembly, RcClient)];
        var register = type.Methods.Single(method => method.Name == "Register").Body.Instructions;
        if (mutation is "sdk_extra_caller" or "sdk_interface_caller")
        {
            var target = register.Single(instruction => instruction.Operand is MethodReference method && method.Name == "AddOrUpdate");
            var extra = new MethodDefinition("UnlistedNativeRecurrencePublish", MethodAttributes.Private, type.Module.TypeSystem.Void);
            type.Methods.Add(extra);
            var called = (MethodReference)target.Operand;
            if (mutation == "sdk_interface_caller")
            {
                var contract = new TypeReference("Hangfire", "IRecurringJobManagerV2", type.Module, called.DeclaringType.Scope);
                called = new MethodReference("TriggerJob", type.Module.TypeSystem.String, contract) { HasThis = true };
                called.Parameters.Add(new ParameterDefinition(type.Module.TypeSystem.String));
            }
            extra.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, called));
            extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            return () => type.Methods.Remove(extra);
        }
        if (mutation == "sdk_swapped_literals")
        {
            var queue = register.Single(instruction => instruction.OpCode == OpCodes.Ldstr && (string)instruction.Operand == "control");
            var cron = register.Single(instruction => instruction.OpCode == OpCodes.Ldstr && (string)instruction.Operand == "* * * * *");
            (queue.Operand, cron.Operand) = (cron.Operand, queue.Operand);
            return () => (queue.Operand, cron.Operand) = (cron.Operand, queue.Operand);
        }
        if (mutation == "sdk_arity")
            return ReplaceInstruction(register.Single(instruction => instruction.OpCode == OpCodes.Newarr).Previous,
                OpCodes.Ldc_I4_3, null!);
        if (mutation == "sdk_key_slot")
        {
            var keyStore = register.Single(instruction => instruction.OpCode == OpCodes.Stelem_Ref
                && instruction.Previous?.Operand is MethodReference method && method.Name == "get_Key");
            return ReplaceInstruction(keyStore.Previous.Previous.Previous.Previous, OpCodes.Ldc_I4_1, null!);
        }
        if (mutation is "sdk_queue" or "sdk_cron")
        {
            var literal = register.Single(instruction => instruction.OpCode == OpCodes.Ldstr
                && (string)instruction.Operand == (mutation == "sdk_queue" ? "control" : "* * * * *"));
            var original = literal.Operand;
            literal.Operand = mutation == "sdk_queue" ? "transactional" : "0 * * * *";
            return () => literal.Operand = original;
        }
        if (mutation == "sdk_tick_type")
        {
            var token = register.Single(instruction => instruction.OpCode == OpCodes.Ldtoken);
            return ReplaceInstruction(token, OpCodes.Ldtoken, census.Types[(InfrastructureAssembly, RcControl)]);
        }
        if (mutation == "sdk_manager")
        {
            var create = type.Methods.Single(method => method.IsConstructor).Body.Instructions
                .Single(instruction => instruction.OpCode == OpCodes.Newobj);
            return ReplaceInstruction(create, OpCodes.Nop, null!);
        }
        if (mutation == "sdk_payload")
        {
            var box = register.Single(instruction => instruction.OpCode == OpCodes.Box);
            return ReplaceInstruction(box, OpCodes.Box, type.Module.TypeSystem.Int32);
        }
        if (mutation == "sdk_misfire")
        {
            var setter = register.Single(instruction => instruction.Operand is MethodReference method
                && method.Name == "set_MisfireHandling");
            return ReplaceInstruction(setter.Previous, OpCodes.Ldc_I4_1, null!);
        }
        throw new ArgumentOutOfRangeException(nameof(mutation));
    }

    private static SortedSet<string> RecurrenceSdkOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        if (!types.TryGetValue((InfrastructureAssembly, RcClient), out var client))
        { offenders.Add("Missing concrete native recurrence client"); return offenders; }
        var constructor = client.Methods.Where(method => method.IsConstructor).ToArray();
        var register = client.Methods.Where(method => method.Name == "Register").ToArray();
        if (constructor.Length != 1 || register.Length != 1 || !constructor[0].HasBody || !register[0].HasBody)
        { offenders.Add("The recurrence SDK retains its one actual constructor and register body"); return offenders; }
        var sites = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => (Owner: MethodId(item.Owner), Instruction: instruction,
                Target: (MethodReference)instruction.Operand))).ToArray();
        var managerConstructor = "Hangfire.Core:System.Void Hangfire.RecurringJobManager::.ctor(Hangfire.JobStorage,Hangfire.Common.IJobFilterProvider)";
        var managerRegister = "Hangfire.Core:System.Void Hangfire.RecurringJobManager::AddOrUpdate(System.String,Hangfire.Common.Job,System.String,Hangfire.RecurringJobOptions)";
        var sdkSites = sites.Where(site => site.Target.DeclaringType.FullName.StartsWith("Hangfire.RecurringJob", StringComparison.Ordinal)
            || site.Target.DeclaringType.FullName.StartsWith("Hangfire.IRecurringJobManager", StringComparison.Ordinal)).ToArray();
        CheckSet(offenders, "sole native recurrence SDK entry sites", sdkSites.Select(site =>
                site.Owner + " -> " + MethodId(site.Target) + ":" + site.Instruction.OpCode.Name),
            [MethodId(constructor[0]) + " -> " + managerConstructor + ":newobj",
             MethodId(register[0]) + " -> " + managerRegister + ":callvirt",
             MethodId(register[0]) + " -> Hangfire.Core:System.Void Hangfire.RecurringJobOptions::.ctor():newobj",
             MethodId(register[0]) + " -> Hangfire.Core:System.Void Hangfire.RecurringJobOptions::set_TimeZone(System.TimeZoneInfo):callvirt",
             MethodId(register[0]) + " -> Hangfire.Core:System.Void Hangfire.RecurringJobOptions::set_MisfireHandling(Hangfire.MisfireHandlingMode):callvirt"]);
        foreach (var site in sdkSites)
            if (site.Target.DeclaringType.Scope is not AssemblyNameReference scope || scope.Name != "Hangfire.Core"
                || scope.Version != new Version(1, 8, 25, 0) || scope.PublicKeyToken.Length != 0)
                offenders.Add("Recurrence uses the pinned stock Hangfire assembly reference");

        var actualCalls = register[0].Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => MethodId((MethodReference)instruction.Operand) + ":" + instruction.OpCode.Name);
        const string infra = "LearnStack.Infrastructure:";
        var facts = infra + "LearnStack.Infrastructure.Jobs.BackgroundJobRecurrenceFacts " + RcManifest + "::get_Facts():callvirt";
        var key = infra + "System.String LearnStack.Infrastructure.Jobs.BackgroundJobRecurrenceFacts::get_Key():callvirt";
        CheckSet(offenders, "closed recurrence SDK registration calls", actualCalls,
        [
            "System.Runtime:System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle):call",
            infra + "System.Reflection.MethodInfo LearnStack.Infrastructure.Jobs.HangfireInvocationContract::get_TickMethod():call",
            facts, facts, facts, key, key,
            infra + "System.Int64 LearnStack.Infrastructure.Jobs.BackgroundJobRecurrenceFacts::get_Revision():callvirt",
            infra + "System.Void <>z__ReadOnlyArray`1<System.Object>::.ctor(T[]):newobj",
            "Hangfire.Core:System.Void Hangfire.Common.Job::.ctor(System.Type,System.Reflection.MethodInfo,System.Collections.Generic.IReadOnlyList`1<System.Object>,System.String):newobj",
            "Hangfire.Core:System.Void Hangfire.RecurringJobOptions::.ctor():newobj",
            "System.Runtime:System.TimeZoneInfo System.TimeZoneInfo::get_Utc():call",
            "Hangfire.Core:System.Void Hangfire.RecurringJobOptions::set_TimeZone(System.TimeZoneInfo):callvirt",
            "Hangfire.Core:System.Void Hangfire.RecurringJobOptions::set_MisfireHandling(Hangfire.MisfireHandlingMode):callvirt",
            managerRegister + ":callvirt",
        ]);
        var instructions = register[0].Body.Instructions.ToArray();
        CheckSet(offenders, "fixed recurrence queue and UTC cron", instructions.Where(instruction => instruction.OpCode == OpCodes.Ldstr)
            .Select(instruction => (string)instruction.Operand), ["control", "* * * * *"]);
        var tokens = instructions.Where(instruction => instruction.OpCode == OpCodes.Ldtoken).ToArray();
        var tickType = "LearnStack.Infrastructure.Jobs.HangfireRecurrenceTick";
        if (tokens.Length != 1 || tokens[0].Operand is not TypeReference tick
            || !types.TryGetValue((InfrastructureAssembly, tickType), out var actualTick) || !SameType(tick, actualTick))
            offenders.Add("Only the exact opaque recurrence tick type reaches the native manager");
        var boxing = instructions.Where(instruction => instruction.OpCode == OpCodes.Box).ToArray();
        if (boxing.Length != 1 || boxing[0].Operand is not TypeReference boxed || !SameType(boxed, client.Module.TypeSystem.Int64))
            offenders.Add("The native recurrence payload boxes only its fixed revision");
        var arrays = instructions.Where(instruction => instruction.OpCode == OpCodes.Newarr).ToArray();
        var stores = instructions.Where(instruction => instruction.OpCode == OpCodes.Stelem_Ref).ToArray();
        if (arrays.Length != 1 || arrays[0].Operand is not TypeReference element
            || !SameType(element, client.Module.TypeSystem.Object) || arrays[0].Previous?.OpCode != OpCodes.Ldc_I4_2
            || stores.Length != 2 || stores[0].Previous?.Operand is not MethodReference keyRead || keyRead.Name != "get_Key"
            || stores[0].Previous?.Previous?.Previous?.OpCode != OpCodes.Ldarg_1
            || stores[0].Previous?.Previous?.Previous?.Previous?.OpCode != OpCodes.Ldc_I4_0
            || stores[1].Previous?.OpCode != OpCodes.Box
            || stores[1].Previous?.Previous?.Operand is not MethodReference revisionRead || revisionRead.Name != "get_Revision"
            || stores[1].Previous?.Previous?.Previous?.Previous?.OpCode != OpCodes.Ldarg_1
            || stores[1].Previous?.Previous?.Previous?.Previous?.Previous?.OpCode != OpCodes.Ldc_I4_1)
            offenders.Add("Native recurrence arguments remain exactly manifest key at zero and revision at one");
        var misfire = instructions.Where(instruction => instruction.Operand is MethodReference method
            && method.DeclaringType.FullName == "Hangfire.RecurringJobOptions" && method.Name == "set_MisfireHandling").ToArray();
        if (misfire.Length != 1 || misfire[0].Previous?.OpCode != OpCodes.Ldc_I4_0)
            offenders.Add("The recurrence hint uses only the pinned Relaxed misfire policy");
        var jobCreate = instructions.SingleOrDefault(instruction => instruction.Operand is MethodReference method
            && method.DeclaringType.FullName == "Hangfire.Common.Job" && method.Name == ".ctor");
        var optionsCreate = instructions.SingleOrDefault(instruction => instruction.Operand is MethodReference method
            && method.DeclaringType.FullName == "Hangfire.RecurringJobOptions" && method.Name == ".ctor");
        if (jobCreate?.Previous?.OpCode != OpCodes.Ldstr || jobCreate.Previous.Operand as string != "control"
            || optionsCreate?.Previous?.OpCode != OpCodes.Ldstr || optionsCreate.Previous.Operand as string != "* * * * *")
            offenders.Add("The fixed queue and cron stay bound to their actual SDK argument positions");
        var manager = client.Fields.Where(field => field.Name == "_manager").ToArray();
        if (manager.Length != 1 || client.Fields.Count != 1 || !manager[0].IsPrivate || !manager[0].IsInitOnly
            || manager[0].IsStatic || manager[0].FieldType.FullName != "Hangfire.RecurringJobManager")
            offenders.Add("The recurrence SDK retains exactly one private readonly concrete manager");
        return offenders;
    }
}
