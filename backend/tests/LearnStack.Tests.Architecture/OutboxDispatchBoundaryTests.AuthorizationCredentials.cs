using LearnStack.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string AuthorizationConfigurationRoot = "AddLearnStackAuthorizationSourceConfiguration";
    private const string AuthorizationConfiguration = "LearnStack.Infrastructure.Persistence.AuthorizationSourceConfiguration";
    private const string AuthorizationLiveRoot = "LearnStack.Api.Authentication.LiveAuthorizationRegistration";

    private static MethodDefinition? CheckAuthorizationCredentialRegistration(SortedSet<string> offenders,
        ProductionCensus census, IEnumerable<ServiceDescriptor> services)
    {
        var declarations = census.Types[(BoundaryApi, BoundaryPersistence)].Methods
            .Where(method => method.Name == AuthorizationConfigurationRoot).ToArray();
        if (declarations.Length != 1) { offenders.Add("Authorization configuration requires its sole canonical registration method"); return null; }
        var root = declarations[0];
        if (!root.IsStatic || !root.IsAssembly || root.HasGenericParameters
            || root.ReturnType.FullName != "Microsoft.Extensions.DependencyInjection.IServiceCollection"
            || !ParametersMatch(root, ["Microsoft.Extensions.DependencyInjection.IServiceCollection",
                "Microsoft.Extensions.Configuration.IConfiguration", "System.Boolean"]))
            offenders.Add(MethodId(root) + ": authorization configuration requires its exact internal static registration signature");
        var caller = census.Types[(BoundaryApi, AuthorizationLiveRoot)].Methods.Single(method => method.Name == "AddLearnStackLiveAuthorization");
        CheckSet(offenders, "authorization configuration callers", CallSites(census)
            .Where(site => site.Target.DeclaringType.FullName == BoundaryPersistence && site.Target.Name == AuthorizationConfigurationRoot)
            .Select(site => site.Owner), [MethodId(caller)]);
        var descriptors = services.Where(descriptor => !descriptor.IsKeyedService
            && descriptor.ServiceType == typeof(AuthorizationSourceConfiguration)).ToArray();
        if (descriptors.Length != 1 || descriptors[0].Lifetime != ServiceLifetime.Singleton
            || descriptors[0].ImplementationFactory is not { } implementation)
        { offenders.Add("Authorization configuration requires its actual sole singleton factory"); return root; }
        var factory = FindRuntimeMethod(census, implementation.Method);
        if (factory is null) { offenders.Add("Authorization configuration factory is missing"); return root; }
        CheckSet(offenders, "authorization configuration delegates", DelegateTargets(root).Select(MethodId), [MethodId(factory)]);
        var reads = root.Body.Instructions.Where(instruction => instruction.Operand is MethodReference call && IsConnectionStringRead(call)).ToArray();
        if (!reads.Select(instruction => instruction.Previous?.Operand as string)
            .SequenceEqual(["Default", "IdentityAuthorization", "AuthorizationEvidence"])
            || reads.Any(instruction => instruction.Previous?.Previous?.OpCode != OpCodes.Ldarg_1))
            offenders.Add(MethodId(root) + ": authorization reads require exactly their three ordered literal keys");
        var captures = reads.Select(instruction => instruction.Next is { OpCode: var code, Operand: FieldReference field }
            && code == OpCodes.Stfld && field.FieldType.FullName == "System.String" ? field : null).ToArray();
        var resolve = factory.Body.Instructions.Where(instruction => instruction.Operand is MethodReference call
            && call.DeclaringType.FullName == AuthorizationConfiguration && call.Name == "Resolve").ToArray();
        var loads = factory.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldfld)
            .Select(instruction => instruction.Operand).OfType<FieldReference>().ToArray();
        if (captures.Length != 3 || captures.Any(field => field is null) || resolve.Length != 1
            || loads.Length != 4 || !loads.Take(3).Select(field => field.FullName)
                .SequenceEqual(captures.Select(field => field?.FullName))
            || loads[^1].FieldType.FullName != "System.Boolean" || MethodOperands(factory).Count() != 1)
        { offenders.Add(MethodId(factory) + ": configuration must resolve from its unchanged ordered credential and mode captures"); return root; }
        var argument = resolve[0].Previous;
        foreach (var field in loads.Reverse())
        {
            if (argument?.OpCode != OpCodes.Ldfld || argument.Operand is not FieldReference supplied
                || supplied.FullName != field.FullName || argument.Previous?.OpCode != OpCodes.Ldarg_0)
            { offenders.Add(MethodId(factory) + ": Resolve must consume only the four actual captured arguments"); break; }
            argument = argument.Previous.Previous;
        }
        foreach (var field in loads)
        {
            var uses = census.Types.Values.SelectMany(type => type.Methods).Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions.Where(instruction => instruction.Operand is FieldReference referenced
                    && referenced.FullName == field.FullName).Select(instruction => (Method: method, Instruction: instruction))).ToArray();
            var writes = uses.Where(use => use.Instruction.OpCode != OpCodes.Ldfld).ToArray();
            if (writes.Length != 1 || writes[0].Method != root || writes[0].Instruction.OpCode != OpCodes.Stfld
                || uses.Count(use => use.Method == factory && use.Instruction.OpCode == OpCodes.Ldfld) != 1
                || uses.Length != 2)
                offenders.Add(field.FullName + ": authorization capture must have only its original write and actual factory read");
            else if (field.FieldType.FullName == "System.Boolean" && writes[0].Instruction.Previous?.OpCode != OpCodes.Ldarg_2)
                offenders.Add(field.FullName + ": derivation permission must come unchanged from the composition argument");
        }
        return root;
    }

    private static void AssertAuthorizationCredentialMutations(ProductionCensus census)
    {
        var root = census.Types[(BoundaryApi, BoundaryPersistence)].Methods.Single(method => method.Name == AuthorizationConfigurationRoot);
        var application = census.Modules.Single(module => module.Assembly.Name.Name == "LearnStack.Application");
        AssertMutation(census, "an extra authorization configuration locator", () =>
            AddCallProbe(application, "ExtraAuthorizationConfiguration", root, OpCodes.Call), CredentialOffenders);
        var reads = root.Body.Instructions.Where(instruction => instruction.Operand is MethodReference call && IsConnectionStringRead(call)).ToArray();
        foreach (var read in reads)
            foreach (var key in new[] { "PlatformAdmin", "OutboxDispatcher", "IdentityAuthorization" })
            {
                if (read.Previous!.Operand as string == key) continue;
                AssertMutation(census, "a different authorization source credential key", () =>
                    ReplaceInstruction(read.Previous!, OpCodes.Ldstr, key), CredentialOffenders);
            }
        var factory = FindRuntimeMethod(census, BoundaryRegistrations().Single(descriptor => !descriptor.IsKeyedService
            && descriptor.ServiceType == typeof(AuthorizationSourceConfiguration)).ImplementationFactory!.Method)!;
        foreach (var field in factory.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldfld)
            .Select(instruction => instruction.Operand).OfType<FieldReference>())
            AssertMutation(census, "an extra forwarding helper for a captured authorization setting", () =>
                AddBoundaryFieldGetter(factory.DeclaringType, "ForwardAuthorizationSetting", field.Name), CredentialOffenders);
        var mode = root.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Stfld
            && instruction.Operand is FieldReference field && field.FieldType.FullName == "System.Boolean");
        AssertMutation(census, "a helper that enables derivation independently of its caller", () =>
            ReplaceInstruction(mode.Previous!, OpCodes.Ldc_I4_1, null!), CredentialOffenders);
        var resolve = factory.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == AuthorizationConfiguration && target.Name == "Resolve");
        AssertMutation(census, "a factory substituting its actual Resolve argument", () =>
            ReplaceInstruction(resolve.Previous!, OpCodes.Ldc_I4_1, null!), CredentialOffenders);
        var caller = census.Types[(BoundaryApi, AuthorizationLiveRoot)].Methods.Single(method => method.Name == "AddLearnStackLiveAuthorization");
        var call = caller.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == BoundaryPersistence && target.Name == AuthorizationConfigurationRoot);
        AssertMutation(census, "missing canonical authorization configuration registration", () =>
            ReplaceInstruction(call, OpCodes.Nop, null!), CredentialOffenders);
    }
}
