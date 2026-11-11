using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string InvitationComposition = "LearnStack.Api.Composition.InvitationEmailComposition";
    private const string InvitationProviderSource = "LearnStack.Infrastructure.Email.InvitationSmtpProviderDataSource";

    private static MethodDefinition? CheckInvitationCredentialRegistration(SortedSet<string> offenders, ProductionCensus census)
    {
        if (!census.Types.TryGetValue((BoundaryApi, InvitationComposition), out var composition))
        { offenders.Add("Missing exact invitation custody composition"); return null; }
        var roots = composition.Methods.Where(method => method.Name == "AddInvitationEmailDelivery").ToArray();
        if (roots.Length != 1) { offenders.Add("Invitation custody requires one composition method"); return null; }
        var root = roots[0];
        if (!root.IsAssembly || !root.IsStatic || root.HasGenericParameters
            || root.ReturnType.FullName != "Microsoft.Extensions.DependencyInjection.IServiceCollection"
            || !ParametersMatch(root, ["Microsoft.Extensions.DependencyInjection.IServiceCollection", "Microsoft.Extensions.Configuration.IConfiguration"]))
            offenders.Add("Invitation custody composition requires its exact internal static signature");
        var reads = root.Body.Instructions.Where(instruction => instruction.Operand is MethodReference call && IsConnectionStringRead(call)).ToArray();
        if (reads.Length != 1 || reads[0].OpCode != OpCodes.Call || reads[0].Previous?.Operand as string != "Default"
            || !InvitationConfigurationArgument(root, reads[0].Previous?.Previous, census))
        { offenders.Add("Invitation custody reads exactly the literal Default app credential"); return root; }
        var read = reads[0];
        if (read.Next is not { OpCode.Code: Code.Stfld, Operand: FieldReference capture } || capture.FieldType.FullName != "System.String")
        { offenders.Add("Invitation credential must be captured by its exact private source factory"); return root; }
        var constructions = census.MethodInstructions(".ctor").Where(site => site.Target.DeclaringType.FullName == InvitationProviderSource).ToArray();
        var delegates = DelegateTargets(root).Select(MethodId).ToArray();
        var uses = census.FieldInstructionsByFullName(capture.FullName).ToArray();
        var loads = uses.Where(use => use.Instruction.OpCode == OpCodes.Ldfld).ToArray();
        if (constructions.Length != 1 || constructions[0].Instruction.OpCode != OpCodes.Newobj
            || !delegates.Contains(constructions[0].OwnerId, StringComparer.Ordinal)
            || constructions[0].Target.Parameters.Count != 1 || constructions[0].Target.Parameters[0].ParameterType.FullName != "System.String"
            || uses.Length != 2 || loads.Length != 1 || uses.Count(use => use.Body == root && use.Instruction == read.Next) != 1
            || MethodId(loads[0].Body) != constructions[0].OwnerId)
            offenders.Add("Invitation app credential has exactly one capture and one read by the registered private SMTP source factory");
        return root;
    }

    // The options factory also captures IConfiguration, so Release lowers the
    // root's original argument read through that same generated closure field.
    // Admit only its single unchanged argument-1 capture, never a substituted
    // source, an arbitrary helper or another credential resolver.
    private static bool InvitationConfigurationArgument(MethodDefinition root, Instruction? argument, ProductionCensus census,
        string composition = InvitationComposition)
    {
        if (argument?.OpCode == OpCodes.Ldarg_1) return true;
        if (argument is not { OpCode.Code: Code.Ldfld, Operand: FieldReference field }
            || field.FieldType.FullName != "Microsoft.Extensions.Configuration.IConfiguration"
            || field.DeclaringType.DeclaringType?.FullName != composition
            || !field.DeclaringType.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal)) return false;
        var writes = census.FieldInstructionsByFullName(field.FullName).Where(site => site.Instruction.OpCode != OpCodes.Ldfld).ToArray();
        return writes.Length == 1 && writes[0].Body == root && writes[0].Instruction.OpCode == OpCodes.Stfld
            && writes[0].Instruction.Previous?.OpCode == OpCodes.Ldarg_1;
    }

    private static void AssertInvitationCredentialMutations(ProductionCensus census)
    {
        var root = census.Types[(BoundaryApi, InvitationComposition)].Methods.Single(method => method.Name == "AddInvitationEmailDelivery");
        var read = root.Body.Instructions.Single(instruction => instruction.Operand is MethodReference call && IsConnectionStringRead(call));
        foreach (var key in new[] { "PlatformAdmin", "OutboxDispatcher", "IdentityAuthorization" })
            AssertMutation(census, "a different SMTP custody credential", () => ReplaceInstruction(read.Previous!, OpCodes.Ldstr, key), CredentialOffenders);
        AssertMutation(census, "missing SMTP custody app credential read", () => ReplaceInstruction(read, OpCodes.Nop, null!), CredentialOffenders);
        var capture = (FieldReference)read.Next!.Operand;
        AssertMutation(census, "forwarded SMTP custody credential", () =>
            AddBoundaryFieldGetter(census.Types[(BoundaryApi, capture.DeclaringType.FullName)], "ForwardInvitationCredential", capture.Name), CredentialOffenders);
        if (read.Previous?.Previous is { Operand: FieldReference configuration })
        {
            var write = census.FieldInstructionsByFullName(configuration.FullName).Single(site => site.Instruction.OpCode == OpCodes.Stfld);
            AssertMutation(census, "substituted SMTP composition configuration argument", () =>
                ReplaceInstruction(write.Instruction.Previous!, OpCodes.Ldnull, null!), CredentialOffenders);
        }
    }
}
