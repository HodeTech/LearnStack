using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private static SortedSet<string> RecurrencePublicationOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        const string storeType = "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore";
        if (!types.TryGetValue((InfrastructureAssembly, storeType), out var store))
        { offenders.Add("Missing actual recipient acquisition store"); return offenders; }
        var claims = store.Methods.Where(method => method.Name == "TryClaimRecipientAsync").ToArray();
        if (claims.Length != 1 || !ParametersMatch(claims[0], [RcRegistry, "System.Collections.Generic.IReadOnlyCollection`1<System.Guid>", "System.Threading.CancellationToken"])
            || claims[0].ReturnType.FullName != "System.Threading.Tasks.Task`1<LearnStack.Infrastructure.Jobs.OwnedBackgroundJobControl`1<" + RecipientClaim + ">>"
            || !claims[0].IsAssembly || claims[0].IsStatic || claims[0].IsVirtual || claims[0].HasGenericParameters)
        { offenders.Add("Retained recipient acquisition preserves its exact bounded signature"); return offenders; }
        var owned = OwnedBodies(census).ToArray();
        var sites = owned.SelectMany(item => item.Body.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => (item.Owner, Instruction: instruction, Target: (MethodReference)instruction.Operand))).ToArray();
        var callers = sites.Where(site => site.Target.DeclaringType.FullName == storeType && site.Target.Name == claims[0].Name).ToArray();
        CheckSet(offenders, "sole retained recipient selector caller", callers.Select(site => MethodId(site.Owner) + ":" + site.Instruction.OpCode.Name),
            [JobOwner(offenders, types, InfrastructureAssembly, RcControl, "ClaimAsync") + ":callvirt"]);
        if (callers.Any(site => !TargetsDeclaredType(site.Target, store) || MethodId(site.Target) != MethodId(claims[0])))
            offenders.Add("Recipient selection targets its actual declared store method");
        var claimBody = Body(storeType, "TryClaimRecipientAsync");
        var sql = claimBody.Where(instruction => instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string text
            && text.StartsWith("SELECT m.recurrence_key", StringComparison.Ordinal)).ToArray();
        if (sql.Length != 1 || !((string)sql[0].Operand).Contains("AND r.id<>ALL(@retained)", StringComparison.Ordinal))
            offenders.Add("Actual recipient selection excludes the retained identities in parameterized SQL");
        var typed = claimBody.Where(instruction => instruction.Operand is MethodReference method
            && method.DeclaringType.FullName == "Npgsql.NpgsqlParameterCollection" && method.Name == "AddWithValue"
            && ParametersMatch(method, ["System.String", "NpgsqlTypes.NpgsqlDbType", "System.Object"])).ToArray();
        if (typed.Length != 1 || typed[0].Previous?.Operand is not FieldReference retained || retained.Name != "<retained>5__2"
            || retained.FieldType.FullName != "System.Guid[]" || typed[0].Previous?.Previous?.OpCode != OpCodes.Ldarg_0
            || OrchestrationConstant(typed[0].Previous?.Previous?.Previous!) != unchecked((int)0x8000001b)
            || typed[0].Previous?.Previous?.Previous?.Previous?.Operand as string != "retained")
            offenders.Add("Recipient exclusions bind their actual snapshot as a typed UUID array");
        if (typed.Length != 1 || typed[0].OpCode != OpCodes.Callvirt || MethodId((MethodReference)typed[0].Operand)
            != "Npgsql:Npgsql.NpgsqlParameter Npgsql.NpgsqlParameterCollection::AddWithValue(System.String,NpgsqlTypes.NpgsqlDbType,System.Object)"
            || ((MethodReference)typed[0].Operand).DeclaringType.Scope is not AssemblyNameReference provider
            || provider.Version != new Version(10, 0, 0, 0) || Convert.ToHexString(provider.PublicKeyToken) != "5D8B90D52F46FDA7")
            offenders.Add("The UUID array is passed to the actual Npgsql invocation rather than a callback");
        var clones = Calls(claimBody, "System.Linq.Enumerable", "ToArray").ToArray();
        if (clones.Length != 1 || MethodId((MethodReference)clones[0].Operand)
            != "System.Linq:!!0[] System.Linq.Enumerable::ToArray<System.Guid>(System.Collections.Generic.IEnumerable`1<!!0>)")
            offenders.Add("Recipient selector clones the caller's bounded ID collection before I/O");
        if (claimBody.Count(instruction => instruction.OpCode == OpCodes.Ldc_I4_4 && instruction.Previous?.OpCode == OpCodes.Conv_I4
                && instruction.Next?.OpCode == OpCodes.Bgt_S) != 1
            || Calls(claimBody, "System.Linq.Enumerable", "Distinct").Count() != 1
            || Calls(claimBody, "System.Linq.Enumerable", "Count").Count() != 1
            || Calls(claimBody, "System.Linq.Enumerable", "Any").Count() != 1)
            offenders.Add("Recipient exclusions retain the four-ID, distinct and nonempty validation shape");

        var application = Body(RcControl, "ClaimAsync");
        if (Calls(application, "System.Collections.Generic.Dictionary`2<System.Guid," + RcPending + ">", "get_Keys").Count() != 1
            || Calls(application, "System.Linq.Enumerable", "ToArray").Count() != 1)
            offenders.Add("The selector snapshots the actual retained recipient dictionary keys");
        Before(application, instruction => instruction.Operand is MethodReference method && method.Name == "Add"
                && method.DeclaringType.FullName == "System.Collections.Generic.Dictionary`2<System.Guid," + RcPending + ">",
            instruction => IsCall(instruction, RcRecipient, "Start"), "recipient publication precedes application scheduling");
        Before(Body(RcControl, "RegisterAsync"), instruction => instruction.OpCode == OpCodes.Stfld
                && instruction.Operand is FieldReference field && field.Name == "_native",
            instruction => IsCall(instruction, RcNative, "Start"), "native publication precedes thread scheduling");
        var observed = Body(RcRecipient, "ObserveAsync");
        Before(observed, instruction => IsCall(instruction, "System.Threading.Tasks.Task", "WhenAll"),
            instruction => IsCall(instruction, "System.Threading.CancellationTokenSource", "Dispose"), "actual cancellation joins precede private source disposal");
        Before(observed, instruction => IsCall(instruction, "System.Runtime.CompilerServices.ConfiguredTaskAwaitable/ConfiguredTaskAwaiter", "GetResult"),
            instruction => IsCall(instruction, "System.Threading.CancellationTokenSource", "Dispose"), "the actual cancellation join ACK precedes private source disposal");
        Before(observed, instruction => IsCall(instruction, "System.Threading.CancellationTokenSource", "Dispose"),
            instruction => IsCall(instruction, RcSignal, "TrySetResult") && instruction.Previous?.Operand is FieldReference field
                && field.Name == "_completion", "actual source disposal precedes positive recipient completion");
        Before(Body(RcNative, "ObserveAsync"), instruction => IsCall(instruction, "System.Threading.Thread", "get_IsAlive"),
            instruction => IsCall(instruction, RcSignal, "TrySetResult"), "positive thread-exit observation precedes native completion");
        return offenders;

        Instruction[] Body(string type, string method)
        {
            var id = JobOwner(offenders, types, InfrastructureAssembly, type, method);
            return owned.Where(item => MethodId(item.Owner) == id && item.Body.Name == "MoveNext")
                .SelectMany(item => item.Body.Body.Instructions).ToArray();
        }
        static IEnumerable<Instruction> Calls(IEnumerable<Instruction> body, string type, string method) => body.Where(instruction => IsCall(instruction, type, method));
        static bool IsCall(Instruction instruction, string type, string name) => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == type && target.Name == name && instruction.OpCode.FlowControl == FlowControl.Call;
        void Before(Instruction[] body, Func<Instruction, bool> earlier, Func<Instruction, bool> later, string claim)
        {
            var first = body.Where(earlier).ToArray();
            var second = body.Where(later).ToArray();
            if (first.Length != 1 || second.Length != 1 || Array.IndexOf(body, first[0]) >= Array.IndexOf(body, second[0]))
                offenders.Add("Preserve recurrence ownership order: " + claim);
        }
    }

    private static Action MutateRecurrencePublication(ProductionCensus census, string mutation)
    {
        if (mutation == "publication_dispose_before_join_ack")
        {
            var observed = OwnedBodies(census).Single(item => item.Owner.DeclaringType.FullName == RcRecipient
                && item.Owner.Name == "ObserveAsync" && item.Body.Name == "MoveNext").Body.Body;
            var ack = observed.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == "System.Runtime.CompilerServices.ConfiguredTaskAwaitable/ConfiguredTaskAwaiter"
                && target.Name == "GetResult");
            var dispose = observed.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == "System.Threading.CancellationTokenSource" && target.Name == "Dispose");
            var prior = observed.Instructions.IndexOf(ack);
            observed.Instructions.Remove(ack);
            observed.Instructions.Insert(observed.Instructions.IndexOf(dispose) + 1, ack);
            return () => { observed.Instructions.Remove(ack); observed.Instructions.Insert(prior, ack); };
        }
        var type = mutation.StartsWith("exclusion_", StringComparison.Ordinal)
            ? "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore" : RcControl;
        var method = type == RcControl ? "ClaimAsync" : "TryClaimRecipientAsync";
        var body = OwnedBodies(census).Single(item => item.Owner.DeclaringType.FullName == type
            && item.Owner.Name == method && item.Body.Name == "MoveNext").Body.Body;
        if (mutation == "exclusion_sql")
        {
            var sql = body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldstr
                && instruction.Operand is string text && text.StartsWith("SELECT m.recurrence_key", StringComparison.Ordinal));
            var original = sql.Operand;
            sql.Operand = ((string)original).Replace("AND r.id<>ALL(@retained)", "", StringComparison.Ordinal);
            return () => sql.Operand = original;
        }
        if (mutation is "exclusion_typed_array" or "exclusion_delegated_binding" or "exclusion_foreign_binding")
        {
            var binding = body.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && target.Name == "AddWithValue" && ParametersMatch(target, ["System.String", "NpgsqlTypes.NpgsqlDbType", "System.Object"]));
            if (mutation == "exclusion_foreign_binding")
            {
                var original = (MethodReference)binding.Operand;
                var wrong = new TypeReference(original.DeclaringType.Namespace, original.DeclaringType.Name, original.Module,
                    new AssemblyNameReference("Npgsql", new Version(9, 0, 0, 0)));
                var methodReference = new MethodReference(original.Name, original.ReturnType, wrong) { HasThis = true };
                foreach (var parameter in original.Parameters) methodReference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
                return ReplaceInstruction(binding, binding.OpCode, methodReference);
            }
            return mutation == "exclusion_delegated_binding" ? ReplaceInstruction(binding, OpCodes.Ldftn, binding.Operand)
                : ReplaceInstruction(binding.Previous.Previous.Previous, OpCodes.Ldc_I4, 27);
        }
        if (mutation == "exclusion_clone")
        {
            var clone = body.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && target.Name == "ToArray" && target.DeclaringType.FullName == "System.Linq.Enumerable");
            return ReplaceInstruction(clone, OpCodes.Nop, null!);
        }
        if (mutation == "exclusion_limit")
        {
            var bound = body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldc_I4_4
                && instruction.Previous?.OpCode == OpCodes.Conv_I4);
            return ReplaceInstruction(bound, OpCodes.Ldc_I4_5, null!);
        }
        var start = body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == RcRecipient && target.Name == "Start");
        var add = body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == "System.Collections.Generic.Dictionary`2<System.Guid," + RcPending + ">" && target.Name == "Add");
        var index = body.Instructions.IndexOf(start);
        body.Instructions.Remove(start);
        body.Instructions.Insert(body.Instructions.IndexOf(add), start);
        return () => { body.Instructions.Remove(start); body.Instructions.Insert(index, start); };
    }
}
