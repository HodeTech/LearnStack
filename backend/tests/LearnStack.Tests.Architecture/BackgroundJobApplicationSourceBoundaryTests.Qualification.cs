using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private static readonly string[] AppJobUnboundQualificationParameters =
        ["LearnStack.SharedKernel.Identifiers.IdentityAuthorityId", "System.String", BoundaryCancellation];
    private static readonly string[] AppJobBoundQualificationParameters =
        ["LearnStack.SharedKernel.Identifiers.IdentityAuthorityId", "System.String", AppJobDatabaseProof, BoundaryCancellation];

    private static void CheckJobQualificationEntries(ProductionCensus census,
        Dictionary<string, TypeDefinition> types, SortedSet<string> offenders)
    {
        foreach (var name in new[] { AppJobAuthorizationSource, AppJobEvidenceSource })
        {
            var source = types[name];
            var entries = source.Methods.Where(method => method.Name == "QualifyAsync").ToArray();
            if (entries.Length != 2) offenders.Add(name + ": qualification has exactly its two declared overloads");
            var expected = new List<MethodDefinition>();
            foreach (var parameters in new[] { AppJobUnboundQualificationParameters, AppJobBoundQualificationParameters })
            {
                var matches = entries.Where(method => ParametersMatch(method, parameters)).ToArray();
                if (matches.Length != 1 || !matches[0].IsPublic || matches[0].IsStatic || matches[0].HasGenericParameters
                    || matches[0].ReturnType.FullName != BoundaryTask)
                    offenders.Add(name + ": qualification requires its exact public instance signature (" + string.Join(",", parameters) + ")");
                else expected.Add(matches[0]);
            }
            var cores = source.Methods.Where(method => method.Name == "QualifyCoreEntryAsync").ToArray();
            if (cores.Length != 1 || !cores[0].IsPrivate || cores[0].IsStatic || cores[0].HasGenericParameters
                || cores[0].ReturnType.FullName != BoundaryTask
                || !ParametersMatch(cores[0], AppJobBoundQualificationParameters))
            { offenders.Add(name + ": qualification requires its exact shared private entry signature"); continue; }
            var core = cores[0];
            CheckSet(offenders, name + " shared qualification entry callers", CallSites(census)
                .Where(site => TargetsDeclaredType(site.Target, source) && site.Target.Name == "QualifyCoreEntryAsync")
                .Select(site => site.Owner), expected.Select(MethodId));
            foreach (var entry in expected)
            {
                var forwards = entry.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
                    && TargetsDeclaredType(target, source) && target.Name == "QualifyCoreEntryAsync").ToArray();
                if (forwards.Length != 1 || forwards[0].OpCode != OpCodes.Call
                    || forwards[0].Operand is not MethodReference actualForward
                    || !ParametersMatch(actualForward, AppJobBoundQualificationParameters))
                    offenders.Add(MethodId(entry) + ": qualification must directly invoke its actual shared guarded entry");
            }
            CheckQualificationJobGuard(census, core, offenders);
        }
        var proof = types[AppJobDatabaseProof];
        var opens = proof.Methods.Where(method => method.Name == "OpenAsync").ToArray();
        if (opens.Length != 1 || !opens[0].IsPublic || !opens[0].IsStatic || opens[0].HasGenericParameters
            || opens[0].ReturnType.FullName != "System.Threading.Tasks.Task`1<" + AppJobDatabaseProof + ">"
            || !ParametersMatch(opens[0], ["Npgsql.NpgsqlDataSource", BoundaryCancellation]))
            offenders.Add("Business qualification proof requires its exact guarded static startup entry");
        else CheckQualificationJobGuard(census, opens[0], offenders);
    }

    private static void CheckQualificationJobGuard(ProductionCensus census, MethodDefinition method, SortedSet<string> offenders)
    {
        var guards = OwnedBodies(census).Where(item => MethodId(item.Owner) == MethodId(method))
            .SelectMany(item => item.Body.Body.Instructions).Where(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == AppJobOwner && target.Name == "RefuseUnrelatedRead").ToArray();
        if (guards.Length != 1 || guards[0].OpCode != OpCodes.Call)
            offenders.Add(MethodId(method) + ": actual startup entry retains one direct job-source refusal guard");
    }

    private static void AssertJobQualificationMutations(ProductionCensus census)
    {
        foreach (var name in new[] { AppJobAuthorizationSource, AppJobEvidenceSource })
        {
            var source = census.Types.Values.Single(type => type.FullName == name);
            var core = source.Methods.Single(method => method.Name == "QualifyCoreEntryAsync");
            foreach (var entry in source.Methods.Where(method => method.Name == "QualifyAsync").ToArray())
            {
                var forward = entry.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
                    && target.DeclaringType.FullName == name && target.Name == "QualifyCoreEntryAsync");
                AssertMutation(census, "a qualification overload bypassing the shared job-source guard", () =>
                    ReplaceInstruction(forward, OpCodes.Nop, null!), JobApplicationSourceOffenders);
                AssertMutation(census, "a qualification overload exporting its shared entry as a delegate", () =>
                    ReplaceInstruction(forward, OpCodes.Ldftn, forward.Operand), JobApplicationSourceOffenders);
            }
            AssertMutation(census, "an additional qualification overload", () =>
            {
                var extra = new MethodDefinition("QualifyAsync", MethodAttributes.Public, core.ReturnType);
                extra.Parameters.Add(new ParameterDefinition(source.Module.TypeSystem.String));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Call, core));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                source.Methods.Add(extra);
                return () => source.Methods.Remove(extra);
            }, JobApplicationSourceOffenders);
            AssertMutation(census, "a different shared qualification entry signature", () =>
            {
                var parameter = new ParameterDefinition(source.Module.TypeSystem.String);
                core.Parameters.Add(parameter);
                return () => core.Parameters.Remove(parameter);
            }, JobApplicationSourceOffenders);
        }
        foreach (var name in new[] { AppJobAuthorizationSource, AppJobEvidenceSource, AppJobDatabaseProof })
        {
            var owner = census.Types.Values.Single(type => type.FullName == name).Methods.Single(method => method.Name ==
                (name == AppJobDatabaseProof ? "OpenAsync" : "QualifyCoreEntryAsync"));
            var guard = OwnedBodies(census).Where(item => MethodId(item.Owner) == MethodId(owner))
                .SelectMany(item => item.Body.Body.Instructions).Single(instruction => instruction.Operand is MethodReference target
                    && target.DeclaringType.FullName == AppJobOwner && target.Name == "RefuseUnrelatedRead");
            AssertMutation(census, "an actual qualification entry losing its job-source refusal", () =>
                ReplaceInstruction(guard, OpCodes.Nop, null!), JobApplicationSourceOffenders);
            AssertMutation(census, "an actual qualification entry delegating its job-source refusal", () =>
                ReplaceInstruction(guard, OpCodes.Ldftn, guard.Operand), JobApplicationSourceOffenders);
        }
    }
}
