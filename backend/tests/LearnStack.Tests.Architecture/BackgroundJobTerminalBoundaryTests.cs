using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string JobTerminalWitness = "LearnStack.SharedKernel.Audit.JobTerminalTransition";
    private const string JobTerminalMinting = JobTerminalWitness + "/MintingSource";
    private const string JobTerminalStore = "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobTerminalStore";
    private const string JobTerminalWrite = "WriteJobTerminalAsync";
    private const string JobTerminalMint = "FromBypassUpdate";
    private const string JobAppTerminalMint = "FromAppFinalizationUpdate";
    private const string JobAppTerminalBridge = "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobCompletion/FinalizationTerminalMint";
    private const string JobTerminalClaimType = "LearnStack.Infrastructure.Jobs.BackgroundJobTerminalClaim";

    /// <summary>Separate actual-row bypass and ordinary app finalization witnesses share the sixth live-pair audit seam.</summary>
    [Fact]
    public void BackgroundJob_Terminal_Audit_Is_Row_Bound()
    {
        using var census = ProductionCensus.Read();
        JobTerminalOffenders(census).Should().BeEmpty(
            "Fix: preserve both exact actual-row mint paths and their separate closed owners on the sixth live-pair audit seam");
    }

    [Fact]
    public void Job_terminal_census_detects_missing_subjects_extra_subtypes_factories_and_all_caller_escape_shapes()
    {
        using var census = ProductionCensus.Read();
        var types = census.Types;
        var witness = types[(KernelAssembly, JobTerminalWitness)];
        var source = types[(KernelAssembly, JobTerminalMinting)];
        var factory = source.Methods.Single(method => method.Name == JobTerminalMint);
        var appFactory = source.Methods.Single(method => method.Name == JobAppTerminalMint);
        var appBridge = types[(InfrastructureAssembly, JobAppTerminalBridge)];
        var appMint = appBridge.Methods.Single(method => method.Name == "Mint");
        var constructor = witness.Methods.Single(method => method.IsConstructor && !method.IsStatic);
        var terminal = types[(InfrastructureAssembly, JobTerminalStore)];
        var port = types[(KernelAssembly, AuditPort)];
        var write = port.Methods.Single(method => method.Name == JobTerminalWrite);
        var concreteWrite = types[(AuditAssembly, AuditStore)].Methods.Single(method => method.Name == JobTerminalWrite);
        var app = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);

        foreach (var target in new[] { factory, appFactory, appMint, write, concreteWrite })
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertJobTerminalMutation(census, "an extra helper/delegate capability caller using " + opcode.Name,
                    () => AddCallProbe(app, "ExtraJobTerminalCaller", target, opcode));
        foreach (var mint in new[] { factory, appFactory, appMint })
        {
            var declaring = mint.DeclaringType;
            AssertJobTerminalMutation(census, "an attribute-linked async mint helper", () => AddAsyncCallProbe(app, "AsyncJobMint", mint));
            AssertJobTerminalMutation(census, "an added actual-row mint overload", () =>
            {
                var overload = AddCall(declaring, mint.Name, mint, OpCodes.Call);
                return () => declaring.Methods.Remove(overload);
            });
            AssertJobTerminalMutation(census, "a missing actual-row mint declaration", () =>
            {
                var index = declaring.Methods.IndexOf(mint);
                declaring.Methods.Remove(mint);
                return () => declaring.Methods.Insert(index, mint);
            });
        }
        AssertJobTerminalMutation(census, "an attribute-linked async writer helper", () => AddAsyncCallProbe(app, "AsyncJobWrite", write));
        AssertJobTerminalMutation(census, "a direct private witness constructor", () => AddCallProbe(app, "DirectJobWitness", constructor, OpCodes.Newobj));
        AssertJobTerminalMutation(census, "a mint helper on the sanctioned terminal store", () =>
        {
            var helper = AddCall(terminal, "UnexpectedMintHelper", factory, OpCodes.Call);
            return () => terminal.Methods.Remove(helper);
        });
        AssertJobTerminalMutation(census, "an extra minting subtype", () =>
        {
            var extra = AddProbe(app, "ExtraJobMintingSource");
            extra.BaseType = app.ImportReference(source);
            return () => app.Types.Remove(extra);
        });
        AssertJobTerminalMutation(census, "an inherited minting subtype", () =>
        {
            var extra = AddProbe(app, "ExtraJobMintingParent");
            extra.Attributes |= TypeAttributes.Abstract;
            extra.BaseType = app.ImportReference(source);
            var child = AddProbe(app, "ExtraJobMintingChild");
            child.BaseType = extra;
            return () => { app.Types.Remove(child); app.Types.Remove(extra); };
        });
        AssertJobTerminalMutation(census, "a public witness constructor", () => ChangeAttributes(() => constructor.Attributes,
            value => constructor.Attributes = value, (constructor.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public));
        foreach (var mint in new[] { factory, appFactory })
            AssertJobTerminalMutation(census, "a public mint factory", () => ChangeAttributes(() => mint.Attributes,
                value => mint.Attributes = value, (mint.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public));
        AssertJobTerminalMutation(census, "an audit write overload", () =>
        {
            var overload = new MethodDefinition(JobTerminalWrite, MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual,
                port.Module.ImportReference(typeof(Task)));
            port.Methods.Add(overload);
            return () => port.Methods.Remove(overload);
        });
        AssertJobTerminalMutation(census, "a caller-supplied identity mint argument", () =>
        {
            var parameter = factory.Parameters[2];
            var original = parameter.ParameterType;
            parameter.ParameterType = factory.Module.TypeSystem.String;
            return () => parameter.ParameterType = original;
        });
        foreach (var key in new[] { (KernelAssembly, JobTerminalWitness), (KernelAssembly, JobTerminalMinting),
                     (InfrastructureAssembly, JobTerminalStore), (InfrastructureAssembly, JobAppTerminalBridge) })
        {
            AssertJobTerminalMutation(census, "missing live subject " + key.Item2, () =>
            {
                var type = census.Types[key];
                if (type.IsNested)
                {
                    var parent = type.DeclaringType;
                    parent.NestedTypes.Remove(type);
                    return () => parent.NestedTypes.Add(type);
                }
                var module = type.Module;
                module.Types.Remove(type);
                return () => module.Types.Add(type);
            });
        }
        var actualMintAndAuditSites = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions)
            .Where(instruction => instruction.Operand is MethodReference target
                && ((SameType(target.DeclaringType, source) || Derives(target.DeclaringType, source, types)) && target.Name is JobTerminalMint or JobAppTerminalMint
                    || target.DeclaringType.FullName == JobAppTerminalBridge && target.Name == "Mint"
                    || target.DeclaringType.FullName == AuditPort && target.Name == JobTerminalWrite)).ToArray();
        actualMintAndAuditSites.Should().HaveCount(5,
            "bypass mint/write and app bridge/mint/write are five distinct live sites");
        foreach (var instruction in actualMintAndAuditSites)
        {
            AssertJobTerminalMutation(census, "a missing actual terminal mint or audit call", () =>
                ReplaceInstruction(instruction, OpCodes.Nop, null!));
            AssertJobTerminalMutation(census, "an actual terminal caller escaping as a delegate", () =>
                ReplaceInstruction(instruction, OpCodes.Ldftn, instruction.Operand));
        }
    }

    private static SortedSet<string> JobTerminalOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        CheckSet(offenders, "job terminal production assemblies", census.Modules.Select(module => module.Assembly.Name.Name), ProductionAssemblies.Names());
        foreach (var key in new[] { (KernelAssembly, JobTerminalWitness), (KernelAssembly, JobTerminalMinting),
                     (InfrastructureAssembly, JobTerminalStore), (InfrastructureAssembly, JobTerminalClaimType),
                     (InfrastructureAssembly, JobAppTerminalBridge), (InfrastructureAssembly, JobPostgresParticipant),
                     (KernelAssembly, AuditPort), (AuditAssembly, AuditStore) })
            if (!types.ContainsKey(key)) offenders.Add("Missing live job terminal subject: " + key.Item2);
        if (offenders.Count != 0) return offenders;

        var sites = CallSites(census).ToArray();
        RecordDetachedTargets(offenders, sites);
        if (offenders.Count != 0) return offenders;

        var witness = types[(KernelAssembly, JobTerminalWitness)];
        var source = types[(KernelAssembly, JobTerminalMinting)];
        var terminal = types[(InfrastructureAssembly, JobTerminalStore)];
        var port = types[(KernelAssembly, AuditPort)];
        var writer = types[(AuditAssembly, AuditStore)];
        var constructors = witness.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
        if (!witness.IsSealed || witness.BaseType?.FullName != "System.Object" || constructors.Length != 1
            || !constructors[0].IsPrivate || !ParametersMatch(constructors[0], [.. MintParameters, LifecycleUnit])
            || witness.Fields.All(field => field.IsStatic) || witness.Fields.Any(field => !field.IsStatic && !field.IsInitOnly)
            || witness.Properties.Count == 0 || witness.Properties.Any(property => property.SetMethod is not null))
            offenders.Add("Job terminal witness must have one private reader/pair/trace constructor and immutable nonempty facts");
        if (!source.IsNestedPublic || !source.IsAbstract || source.IsSealed
            || source.Methods.Where(method => method.IsConstructor && !method.IsStatic).Any(method => !method.IsFamily))
            offenders.Add("Job terminal minting source requires its closed protected construction shape");
        var factories = source.Methods.Where(method => !method.IsConstructor).ToArray();
        var bypassFactory = factories.FirstOrDefault(method => method.Name == JobTerminalMint);
        var appFactory = factories.FirstOrDefault(method => method.Name == JobAppTerminalMint);
        if (factories.Length != 2 || bypassFactory is null || appFactory is null
            || factories.Any(method => !method.IsFamily || !method.IsStatic || method.HasGenericParameters
                || method.ReturnType.FullName != JobTerminalWitness)
            || bypassFactory is not null && !ParametersMatch(bypassFactory, MintParameters)
            || appFactory is not null && !ParametersMatch(appFactory, [LifecycleUnit, "System.Data.Common.DbDataReader", "System.String"]))
            offenders.Add("Job terminal minting requires its exact protected bypass-pair and app-unit actual-row factories");
        var appBridge = types[(InfrastructureAssembly, JobAppTerminalBridge)];
        var appMints = appBridge.Methods.Where(method => !method.IsConstructor).ToArray();
        if (!appBridge.IsNestedPrivate || !appBridge.IsSealed || appMints.Length != 1
            || appMints[0].Name != "Mint" || !appMints[0].IsAssembly || !appMints[0].IsStatic || appMints[0].HasGenericParameters
            || appMints[0].ReturnType.FullName != JobTerminalWitness
            || !ParametersMatch(appMints[0], [JobPostgresParticipant, "System.Data.Common.DbDataReader"]))
            offenders.Add("Application terminal minting remains one private sealed actual-participant bridge");
        if (witness.Module.Assembly.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute"))
            offenders.Add("SharedKernel friend assemblies reopen the job witness authority");
        CheckSet(offenders, "current job terminal minting subtypes", types.Values.Where(type => Derives(type, source, types)).Select(TypeId), [TypeId(terminal), TypeId(appBridge)]);
        CheckSet(offenders, "job witness producing methods", types.Values.SelectMany(type => type.Methods)
            .Where(method => method.ReturnType.FullName == JobTerminalWitness).Select(MethodId), factories.Concat(appMints).Select(MethodId));
        var terminalizers = terminal.Methods.Where(method => method.Name == Terminalize).ToArray();
        if (terminalizers.Length != 1 || !terminal.IsSealed || terminal.IsPublic) offenders.Add("The job bypass terminalizer must remain one internal sealed actual owner");

        var declarations = types.Values.Where(type => SameType(type, port) || Derives(type, port, types))
            .SelectMany(type => type.Methods).Where(method => IsJobTerminalWriteName(method)).ToArray();
        CheckSet(offenders, "sixth audit declarations", declarations.Select(MethodId),
            port.Methods.Concat(writer.Methods).Where(method => method.Name == JobTerminalWrite).Select(MethodId));
        if (declarations.Length != 2 || declarations.Any(method => method.ReturnType.FullName != "System.Threading.Tasks.Task"
                || !ParametersMatch(method, ["LearnStack.SharedKernel.Audit.AuditEntryDraft", JobTerminalWitness,
                    "System.Data.Common.DbConnection", "System.Data.Common.DbTransaction", "System.Threading.CancellationToken"])))
            offenders.Add("The sixth audit write requires its one exact live-pair witness signature on port and real writer");
        CheckSet(offenders, "real audit writers", types.Values.Where(type => !type.IsInterface && !type.IsAbstract && Derives(type, port, types)).Select(TypeId), [TypeId(writer)]);
        var finalizers = types[(InfrastructureAssembly, JobPostgresParticipant)].Methods.Where(method => method.Name == "FenceFinalizeAsync").ToArray();
        if (finalizers.Length != 1) offenders.Add("The ordinary app terminal writer requires its one actual Finalize fence");
        var instructions = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions).ToArray();
        foreach (var instruction in instructions.Where(instruction => instruction.Operand is MethodReference target
                     && ((SameType(target.DeclaringType, source) || Derives(target.DeclaringType, source, types)) && target.Name is JobTerminalMint or JobAppTerminalMint
                         || SameType(target.DeclaringType, appBridge) && target.Name == "Mint"
                         || IsJobTerminalWriteName(target) && (SameType(target.DeclaringType, port) || Derives(target.DeclaringType, port, types)))))
            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                offenders.Add("A terminal mint/write cannot escape as a delegate");
        CheckSet(offenders, "job witness constructor callers", sites.Where(site => site.Target.Name == ".ctor" && SameType(site.Target.DeclaringType, witness))
            .Select(site => site.Owner), factories.Select(MethodId));
        CheckSet(offenders, "job witness actual-row mint callers", sites.Where(site => site.Target.Name == JobTerminalMint
                && (SameType(site.Target.DeclaringType, source) || Derives(site.Target.DeclaringType, source, types)))
            .Select(site => site.Owner), terminalizers.Select(MethodId));
        CheckSet(offenders, "app witness actual-row mint callers", sites.Where(site => site.Target.Name == JobAppTerminalMint
                && (SameType(site.Target.DeclaringType, source) || Derives(site.Target.DeclaringType, source, types)))
            .Select(site => site.Owner), appMints.Select(MethodId));
        CheckSet(offenders, "private app terminal bridge callers", sites.Where(site => site.Target.Name == "Mint" && SameType(site.Target.DeclaringType, appBridge))
            .Select(site => site.Owner), finalizers.Select(MethodId));
        CheckSet(offenders, "sixth audit callers", sites.Where(site => IsJobTerminalWriteName(site.Target)
                && (SameType(site.Target.DeclaringType, port) || Derives(site.Target.DeclaringType, port, types)))
            .Select(site => site.Owner), terminalizers.Concat(finalizers).Select(MethodId));

        var claim = types[(InfrastructureAssembly, JobTerminalClaimType)];
        var claimConstructors = claim.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
        var claimFactories = claim.Methods.Where(method => method.Name == "FromReturnedRow").ToArray();
        if (!claim.IsSealed || claimConstructors.Length != 1 || !claimConstructors[0].IsPrivate
            || claimFactories.Length != 1 || !claimFactories[0].IsAssembly
            || !ParametersMatch(claimFactories[0], ["Npgsql.NpgsqlDataReader"])
            || claim.Properties.Any(property => property.SetMethod is not null))
            offenders.Add("Terminal claims require private immutable actual-returned-row construction");
        CheckSet(offenders, "terminal claim publication callers", sites.Where(site => site.Target.Name == "FromReturnedRow" && SameType(site.Target.DeclaringType, claim))
            .Select(site => site.Owner), terminal.Methods.Where(method => method.Name == "TryClaimTerminalAsync").Select(MethodId));
        return offenders;
    }

    private static bool IsJobTerminalWriteName(MethodReference method) => method.Name == JobTerminalWrite
        || method.Name.EndsWith("." + JobTerminalWrite, StringComparison.Ordinal);
    private static void AssertJobTerminalMutation(ProductionCensus census, string description, Func<Action> mutate) =>
        AssertMutation(census, description, mutate, JobTerminalOffenders);
}
