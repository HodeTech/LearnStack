using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>ADR-0103: the operator recovery witness is minted only from the two stores' fenced resets and written once each.</summary>
public sealed partial class OutboxDispatchConventionTests
{
    private const string RecoveryWitness = "LearnStack.SharedKernel.Audit.OperatorRecoveryTransition";
    private const string RecoverySource = RecoveryWitness + "/MintingSource";
    private const string RecoveryReplayMint = "FromReplayUpdate";
    private const string RecoveryResetMint = "FromResetUpdate";
    private const string RecoveryWrite = "WriteOperatorRecoveryAsync";
    private const string RecoveryMintSubtype = "/RecoveryMint";
    private static readonly string[] RecoveryMintParameters =
    [
        "System.Data.Common.DbConnection", "System.Data.Common.DbTransaction",
        "System.Data.Common.DbDataReader", "LearnStack.SharedKernel.Platform.PlatformAdmission",
    ];
    private static readonly string[] RecoveryWriteParameters =
    [
        "LearnStack.SharedKernel.Audit.AuditEntryDraft", RecoveryWitness,
        "System.Data.Common.DbConnection", "System.Data.Common.DbTransaction",
        "System.Threading.CancellationToken",
    ];

    /// <summary>
    /// ADR-0103 § 5: the second writer of dispatcher status mints its witness from the actual returned row of its
    /// fenced UPDATE, and the audit port's recovery write has exactly the two dispatcher callers. PostgreSQL tests
    /// separately prove that the live result and the draft agree.
    /// </summary>
    [Fact]
    public void Operator_Recovery_Audit_Identity_Is_Row_Bound()
    {
        using var census = ProductionCensus.Read();
        RecoveryOffenders(census).Should().BeEmpty(
            "Fix: keep the immutable private recovery witness, its two nested store minting subtypes and the "
            + "recovery writer and its call sites closed; keep row and transaction identity checks in the witness");
    }

    [Fact]
    public void Operator_recovery_census_detects_extra_derivers_constructors_and_callers()
    {
        using var census = ProductionCensus.Read();
        var source = census.Types[(KernelAssembly, RecoverySource)];
        var factory = source.Methods.Single(method => method.Name == RecoveryReplayMint);
        var constructor = census.Types[(KernelAssembly, RecoveryWitness)].Methods.Single(method => method.IsConstructor && !method.IsStatic);
        var port = census.Types[(KernelAssembly, AuditPort)];
        var write = port.Methods.Single(method => method.Name == RecoveryWrite);
        var application = census.Modules.Single(module => module.Assembly.Name.Name == "LearnStack.Application");

        AssertMutation(census, "a second minting subtype in another production assembly", () =>
        {
            var type = AddProbe(application, "AdditionalRecoveryMintingSource");
            type.BaseType = application.ImportReference(source);
            return () => application.Types.Remove(type);
        }, RecoveryOffenders);
        foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn })
        {
            AssertMutation(census, "a cross-assembly mint helper using " + opcode.Name, () =>
                AddCallProbe(application, "AdditionalRecoveryMintCaller", factory, opcode), RecoveryOffenders);
        }

        AssertMutation(census, "a direct private witness constructor call", () =>
            AddCallProbe(application, "AdditionalRecoveryConstructor", constructor, OpCodes.Newobj), RecoveryOffenders);
        foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt })
        {
            AssertMutation(census, "an extra recovery writer caller using " + opcode.Name, () =>
                AddCallProbe(application, "AdditionalRecoveryWriter", write, opcode), RecoveryOffenders);
        }

        AssertMutation(census, "a helper on the sanctioned store type", () =>
        {
            var type = census.Types[(InfrastructureAssembly, Store)];
            var method = AddCall(type, "UnrelatedRecoveryHelper", factory, OpCodes.Call);
            return () => type.Methods.Remove(method);
        }, RecoveryOffenders);
    }

    private static SortedSet<string> RecoveryOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        foreach (var key in new[] { (KernelAssembly, RecoveryWitness), (KernelAssembly, RecoverySource),
                     (InfrastructureAssembly, Store), (InfrastructureAssembly, DeliveryStore),
                     (InfrastructureAssembly, Store + RecoveryMintSubtype), (InfrastructureAssembly, DeliveryStore + RecoveryMintSubtype),
                     (KernelAssembly, AuditPort), (AuditAssembly, AuditStore) })
        {
            if (!types.ContainsKey(key)) offenders.Add("Missing production subject: " + key.Item2);
        }

        if (offenders.Count != 0) return offenders;

        var witness = types[(KernelAssembly, RecoveryWitness)];
        var source = types[(KernelAssembly, RecoverySource)];
        var port = types[(KernelAssembly, AuditPort)];
        var auditStore = types[(AuditAssembly, AuditStore)];
        var subtypes = new[] { types[(InfrastructureAssembly, Store + RecoveryMintSubtype)], types[(InfrastructureAssembly, DeliveryStore + RecoveryMintSubtype)] };

        var constructors = witness.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
        if (!witness.IsSealed || witness.BaseType?.FullName != "System.Object") offenders.Add(RecoveryWitness + ": must be sealed with no custom base");
        if (constructors.Length != 1 || constructors.Any(method => !method.IsPrivate
                || !ParametersMatch(method, [.. RecoveryMintParameters, "System.Boolean"])))
            offenders.Add(RecoveryWitness + ": requires exactly one private live-reader constructor");
        if (witness.Fields.All(field => field.IsStatic) || witness.Fields.Any(field => !field.IsStatic && !field.IsInitOnly))
            offenders.Add(RecoveryWitness + ": instance fields must be nonempty and readonly");
        if (witness.Properties.Count == 0 || witness.Properties.Any(property => property.SetMethod is not null))
            offenders.Add(RecoveryWitness + ": properties must be nonempty and getter-only");
        if (!source.IsNestedPublic || !source.IsAbstract || source.IsSealed)
            offenders.Add(RecoverySource + ": requires the public abstract nested minting capability");
        if (source.Methods.Where(method => method.IsConstructor && !method.IsStatic).Any(method => !method.IsFamily))
            offenders.Add(RecoverySource + ": constructors must be protected");
        if (witness.Module.Assembly.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute"))
            offenders.Add(KernelAssembly + ": friend assemblies reopen restricted authority");

        var factories = source.Methods.Where(method => method.Name is RecoveryReplayMint or RecoveryResetMint).ToArray();
        if (factories.Length != 2 || factories.Select(method => method.Name).Distinct(StringComparer.Ordinal).Count() != 2
            || factories.Any(method => !method.IsFamily || !method.IsStatic || method.ReturnType.FullName != RecoveryWitness
                || !ParametersMatch(method, RecoveryMintParameters)))
            offenders.Add(RecoverySource + ": requires the two protected static live-reader mint signatures");
        if (subtypes.Any(type => !type.IsNestedPrivate || !type.IsSealed))
            offenders.Add("The recovery minting subtypes must be private sealed nested classes of their stores");

        CheckSet(offenders, "recovery minting subtypes", types.Values.Where(type => Derives(type, source, types)).Select(TypeId),
            subtypes.Select(TypeId));
        CheckSet(offenders, "recovery witness-producing methods", types.Values.SelectMany(type => type.Methods)
            .Where(method => method.ReturnType.FullName == RecoveryWitness).Select(MethodId),
            factories.Select(MethodId).Concat(subtypes.SelectMany(type => type.Methods.Where(method => method.Name is "Replay" or "Reset")
                .Select(MethodId))));

        var writers = types.Values.Where(type => !type.IsInterface && !type.IsAbstract && Derives(type, port, types)).ToArray();
        CheckSet(offenders, "audit store implementations", writers.Select(TypeId), [TypeId(auditStore)]);
        var declarations = types.Values.Where(type => SameType(type, port) || Derives(type, port, types))
            .SelectMany(type => type.Methods).Where(method => method.Name == RecoveryWrite).ToArray();
        CheckSet(offenders, "recovery audit declarations", declarations.Select(method => TypeId(method.DeclaringType) + "." + method.Name),
            [TypeId(port) + "." + RecoveryWrite, TypeId(auditStore) + "." + RecoveryWrite]);
        foreach (var method in declarations.Where(method => method.ReturnType.FullName != "System.Threading.Tasks.Task"
                     || !ParametersMatch(method, RecoveryWriteParameters)))
            offenders.Add(MethodId(method) + ": wrong recovery write signature");

        var sites = CallSites(census).ToArray();
        CheckSet(offenders, "recovery witness constructor call sites", sites.Where(site => site.Target.Name == ".ctor"
                && SameType(site.Target.DeclaringType, witness)).Select(site => site.Owner), factories.Select(MethodId));
        foreach (var (mint, wrapper, store) in new[] { (RecoveryReplayMint, "Replay", DeliveryStore), (RecoveryResetMint, "Reset", Store) })
        {
            var subtype = types[(InfrastructureAssembly, store + RecoveryMintSubtype)];
            CheckSet(offenders, mint + " call sites", sites.Where(site => site.Target.Name == mint
                    && (SameType(site.Target.DeclaringType, source) || Derives(site.Target.DeclaringType, source, types)))
                .Select(site => site.Owner), subtype.Methods.Where(method => method.Name == wrapper).Select(MethodId));
            var core = types[(InfrastructureAssembly, store)].Methods.Where(method => method.Name is "ReplayCoreAsync" or "ResetCoreAsync").ToArray();
            CheckSet(offenders, wrapper + " wrapper call sites", sites.Where(site => site.Target.Name == wrapper
                    && SameType(site.Target.DeclaringType, subtype)).Select(site => site.Owner), core.Select(MethodId));
        }

        var recoveryRows = types[(InfrastructureAssembly, Store)].Methods.Concat(types[(InfrastructureAssembly, DeliveryStore)].Methods)
            .Where(method => method.Name == "WriteRecoveryRowAsync").ToArray();
        CheckSet(offenders, "recovery audit call sites", sites.Where(site => site.Target.Name == RecoveryWrite
                && (SameType(site.Target.DeclaringType, port) || Derives(site.Target.DeclaringType, port, types)))
            .Select(site => site.Owner), recoveryRows.Select(MethodId));
        return offenders;
    }
}
