using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string ProviderComposition = "LearnStack.Api.Composition.ProviderEventComposition";
    private const string ProviderIntakeSource = "LearnStack.Infrastructure.Identity.ProviderEvents.ProviderEventIntakeSource";

    private static MethodDefinition? CheckProviderCredentialRegistration(SortedSet<string> offenders, ProductionCensus census)
    {
        if (!census.Types.TryGetValue((BoundaryApi, ProviderComposition), out var composition))
        { offenders.Add("Missing exact provider mirror composition"); return null; }
        var roots = composition.Methods.Where(method => method.Name == "AddLearnStackProviderEventMirror").ToArray();
        if (roots.Length != 1) { offenders.Add("Provider mirror requires one composition method"); return null; }
        var root = roots[0];
        if (!root.IsAssembly || !root.IsStatic || root.HasGenericParameters
            || root.ReturnType.FullName != "Microsoft.Extensions.DependencyInjection.IServiceCollection"
            || !ParametersMatch(root, ["Microsoft.Extensions.DependencyInjection.IServiceCollection", "Microsoft.Extensions.Configuration.IConfiguration"]))
            offenders.Add("Provider mirror composition requires its exact internal static signature");
        var reads = root.Body.Instructions.Where(instruction => instruction.Operand is MethodReference call && IsConnectionStringRead(call)).ToArray();
        if (reads.Length != 1 || reads[0].OpCode != OpCodes.Call || reads[0].Previous?.Operand as string != "IdentityIngress"
            || !InvitationConfigurationArgument(root, reads[0].Previous?.Previous, census, ProviderComposition))
        { offenders.Add("Provider mirror reads exactly its literal IdentityIngress machine credential"); return root; }
        var read = reads[0];
        if (read.Next is not { OpCode.Code: Code.Stfld, Operand: FieldReference capture } || capture.FieldType.FullName != "System.String")
        { offenders.Add("Provider credential must be captured by its exact private source factory"); return root; }
        var constructions = census.MethodInstructions(".ctor").Where(site => site.Target.DeclaringType.FullName == ProviderIntakeSource).ToArray();
        var delegates = DelegateTargets(root).Select(MethodId).ToArray();
        var uses = census.FieldInstructionsByFullName(capture.FullName).ToArray();
        var loads = uses.Where(use => use.Instruction.OpCode == OpCodes.Ldfld).ToArray();
        if (constructions.Length != 1 || constructions[0].Instruction.OpCode != OpCodes.Newobj
            || !delegates.Contains(constructions[0].OwnerId, StringComparer.Ordinal)
            || constructions[0].Target.Parameters.Count != 1 || constructions[0].Target.Parameters[0].ParameterType.FullName != "System.String"
            || uses.Length != 2 || loads.Length != 1 || uses.Count(use => use.Body == root && use.Instruction == read.Next) != 1
            || MethodId(loads[0].Body) != constructions[0].OwnerId)
            offenders.Add("Provider machine credential has exactly one capture and one read by its registered private source factory");
        return root;
    }
}
