using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string JobLeaseLossCause = "LearnStack.Infrastructure.Jobs.BackgroundJobLeaseLossCause";

    private static MethodDefinition[] LeaseObservationRoots(ProductionCensus census, SortedSet<string> offenders)
    {
        var types = census.Types;
        if (!types.TryGetValue((InfrastructureAssembly, OrchestrationExecution), out var execution)
            || !types.TryGetValue((InfrastructureAssembly, OrchestrationControl), out var control)
            || !types.TryGetValue((InfrastructureAssembly, JobLeaseLossCause), out var cause))
        { offenders.Add("Missing concrete first lease-loss observation owners"); return []; }
        var privateId = RequireBoundaryMethod(offenders, execution, "ObserveLeaseLoss", "System.Void", MethodAttributes.Private, JobLeaseLossCause);
        var parentId = RequireBoundaryMethod(offenders, control, "ObserveLeaseLoss", "System.Void", MethodAttributes.Assembly, JobLeaseLossCause);
        var roots = execution.Methods.Concat(control.Methods).Where(method => MethodId(method) == privateId || MethodId(method) == parentId).ToArray();
        if (roots.Length != 2 || roots.Any(method => method.IsStatic || method.IsVirtual || method.HasGenericParameters || !method.HasBody
            || method.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute")))
        { offenders.Add("Lease-loss recording requires two concrete synchronous instance helpers"); return []; }
        if (!cause.IsEnum || cause.Fields.SingleOrDefault(field => field.Name == "value__")?.FieldType.FullName != "System.Int32")
            offenders.Add("Lease-loss causes retain their closed Int32 enum");
        CheckSet(offenders, "exact first-loss causes", cause.Fields.Where(field => field.IsLiteral)
            .Select(field => field.Name + ":" + field.Constant), ["None:0", "RenewalRefused:1", "Elapsed:2"]);
        var parent = roots.Single(method => method.DeclaringType == control);
        CheckBridgeCapturedField(offenders, census, execution, "_firstLeaseLoss", "System.Int32", [privateId + ":ldflda"]);
        CheckBridgeCapturedField(offenders, census, execution, "_leaseObservationEligible", "System.Boolean",
            [privateId + ":ldfld",
             JobOwner(offenders, types, InfrastructureAssembly, OrchestrationExecution, "ObservePublicationAsync") + ":stfld",
             JobOwner(offenders, types, InfrastructureAssembly, OrchestrationExecution, "ExecuteExternalRecoveryAsync") + ":stfld"]);
        const string increment = "System.Threading:System.Int64 System.Threading.Interlocked::Increment(System.Int64&)";
        const string read = "System.Threading:System.Int64 System.Threading.Interlocked::Read(System.Int64&)";
        const string compare = "System.Threading:System.Int32 System.Threading.Interlocked::CompareExchange(System.Int32&,System.Int32,System.Int32)";
        CheckSet(offenders, "lease parent is Interlocked-only even under an outer execution lock", parent.Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => MethodId((MethodReference)instruction.Operand) + ":" + instruction.OpCode.Name),
            [increment + ":call", increment + ":call"]);

        var expected = new List<string>();
        foreach (var (method, value, count) in new[]
                 {
                     ("RefuseRenewal", 1, 1), ("RecordRenewal", 2, 2), ("LeaseElapsed", 2, 1),
                     ("ObservePublicationAsync", 2, 1), ("ExecuteExternalRecoveryAsync", 2, 1), ("StartRecoveryLease", 2, 2),
                 })
            expected.AddRange(Enumerable.Repeat(JobOwner(offenders, types, InfrastructureAssembly, OrchestrationExecution, method)
                + ":call:" + value, count));
        CheckSet(offenders, "exact consumed first-loss causes at eight private helper calls", census.MethodInstructions("ObserveLeaseLoss")
            .Where(site => TargetsDeclaredType(site.Target, execution))
            .Select(site => site.OwnerId + ":" + site.Instruction.OpCode.Name + ":" + Literal(site.Instruction.Previous)), expected);
        CheckSet(offenders, "one persistent first-loss recorder caller", census.MethodInstructions("ObserveLeaseLoss")
            .Where(site => TargetsDeclaredType(site.Target, control))
            .Select(site => site.OwnerId + ":" + site.Instruction.OpCode.Name + ":" + site.Instruction.Previous?.OpCode.Name),
            [privateId + ":callvirt:ldarg.1"]);

        var first = census.FieldInstructions("_firstLeaseLoss").Where(site => TargetsDeclaredType(site.Target, execution)).ToArray();
        if (first.Length != 1 || first[0].OwnerId != privateId || first[0].Instruction.OpCode != OpCodes.Ldflda
            || first[0].Instruction.Next?.OpCode != OpCodes.Ldarg_1 || Literal(first[0].Instruction.Next?.Next) != 0
            || first[0].Instruction.Next?.Next?.Next is not { OpCode.Code: Code.Call, Operand: MethodReference atomic }
            || MethodId(atomic) != compare)
            offenders.Add("The first-loss address is consumed only by its exact cause/zero CompareExchange");
        var snapshot = JobOwner(offenders, types, InfrastructureAssembly, OrchestrationControl, "get_Snapshot");
        foreach (var name in new[] { "_leaseLossRenewalRefused", "_leaseLossElapsed" })
        {
            CheckBridgeCapturedField(offenders, census, control, name, "System.Int64", [parentId + ":ldflda", snapshot + ":ldflda"]);
            var sites = census.FieldInstructions(name).Where(site => TargetsDeclaredType(site.Target, control)).ToArray();
            CheckSet(offenders, "exact consumed first-loss scalar atomics " + name, sites.Select(site =>
                site.OwnerId + ":" + site.Instruction.OpCode.Name + ":" +
                (site.Instruction.Next is { OpCode.Code: Code.Call, Operand: MethodReference method } ? MethodId(method) : "not-an-atomic-call")),
                [parentId + ":ldflda:" + increment, snapshot + ":ldflda:" + read]);
        }
        return roots;

        static int? Literal(Instruction? instruction) => instruction?.OpCode.Code switch
        {
            Code.Ldc_I4_0 => 0,
            Code.Ldc_I4_1 => 1,
            Code.Ldc_I4_2 => 2,
            _ => null,
        };
    }
}
