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
    private const string JobTerminalClaimType = "LearnStack.Infrastructure.Jobs.BackgroundJobTerminalClaim";

    /// <summary>Step 5 implements the real bypass path. Step 7 adds the separate app failure-finalization path.</summary>
    [Fact]
    public void BackgroundJob_Terminal_Audit_Is_Row_Bound()
    {
        using var census = ProductionCensus.Read();
        JobTerminalOffenders(census).Should().BeEmpty(
            "Fix: preserve the actual-row witness, its one current bypass mint/caller and the sixth live-pair audit seam; do not fabricate the future app-finalization caller");
    }

    [Fact]
    public void Job_terminal_census_detects_missing_subjects_extra_subtypes_factories_and_all_caller_escape_shapes()
    {
        using var census = ProductionCensus.Read();
        var types = census.Types;
        var witness = types[(KernelAssembly, JobTerminalWitness)];
        var source = types[(KernelAssembly, JobTerminalMinting)];
        var factory = source.Methods.Single(method => method.Name == JobTerminalMint);
        var constructor = witness.Methods.Single(method => method.IsConstructor && !method.IsStatic);
        var terminal = types[(InfrastructureAssembly, JobTerminalStore)];
        var port = types[(KernelAssembly, AuditPort)];
        var write = port.Methods.Single(method => method.Name == JobTerminalWrite);
        var concreteWrite = types[(AuditAssembly, AuditStore)].Methods.Single(method => method.Name == JobTerminalWrite);
        var app = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);

        foreach (var target in new[] { factory, write, concreteWrite })
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertJobTerminalMutation(census, "an extra helper/delegate capability caller using " + opcode.Name,
                    () => AddCallProbe(app, "ExtraJobTerminalCaller", target, opcode));
        AssertJobTerminalMutation(census, "an attribute-linked async mint helper", () => AddAsyncCallProbe(app, "AsyncJobMint", factory));
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
        AssertJobTerminalMutation(census, "a public mint factory", () => ChangeAttributes(() => factory.Attributes,
            value => factory.Attributes = value, (factory.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public));
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
        foreach (var key in new[] { (KernelAssembly, JobTerminalWitness), (KernelAssembly, JobTerminalMinting), (InfrastructureAssembly, JobTerminalStore) })
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
        foreach (var name in new[] { JobTerminalMint, JobTerminalWrite })
        {
            AssertJobTerminalMutation(census, "an empty real " + name + " caller census", () =>
            {
                var body = census.Types.Values.SelectMany(type => type.Methods).First(method => method.HasBody
                    && method.Body.Instructions.Any(instruction => instruction.Operand is MethodReference target && target.Name == name));
                var instruction = body.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target && target.Name == name);
                var index = body.Body.Instructions.IndexOf(instruction);
                body.Body.Instructions.RemoveAt(index);
                return () => body.Body.Instructions.Insert(index, instruction);
            });
        }
    }

    private static SortedSet<string> JobTerminalOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        CheckSet(offenders, "job terminal production assemblies", census.Modules.Select(module => module.Assembly.Name.Name), ProductionAssemblies.Names());
        foreach (var key in new[] { (KernelAssembly, JobTerminalWitness), (KernelAssembly, JobTerminalMinting),
                     (InfrastructureAssembly, JobTerminalStore), (InfrastructureAssembly, JobTerminalClaimType),
                     (KernelAssembly, AuditPort), (AuditAssembly, AuditStore) })
            if (!types.ContainsKey(key)) offenders.Add("Missing live job terminal subject: " + key.Item2);
        if (offenders.Count != 0) return offenders;

        var witness = types[(KernelAssembly, JobTerminalWitness)];
        var source = types[(KernelAssembly, JobTerminalMinting)];
        var terminal = types[(InfrastructureAssembly, JobTerminalStore)];
        var port = types[(KernelAssembly, AuditPort)];
        var writer = types[(AuditAssembly, AuditStore)];
        var constructors = witness.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
        if (!witness.IsSealed || witness.BaseType?.FullName != "System.Object" || constructors.Length != 1
            || !constructors[0].IsPrivate || !ParametersMatch(constructors[0], MintParameters)
            || witness.Fields.All(field => field.IsStatic) || witness.Fields.Any(field => !field.IsStatic && !field.IsInitOnly)
            || witness.Properties.Count == 0 || witness.Properties.Any(property => property.SetMethod is not null))
            offenders.Add("Job terminal witness must have one private reader/pair/trace constructor and immutable nonempty facts");
        if (!source.IsNestedPublic || !source.IsAbstract || source.IsSealed
            || source.Methods.Where(method => method.IsConstructor && !method.IsStatic).Any(method => !method.IsFamily))
            offenders.Add("Job terminal minting source requires its closed protected construction shape");
        var factories = source.Methods.Where(method => !method.IsConstructor).ToArray();
        if (factories.Length != 1 || factories[0].Name != JobTerminalMint || !factories[0].IsFamily || !factories[0].IsStatic
            || factories[0].ReturnType.FullName != JobTerminalWitness || !ParametersMatch(factories[0], MintParameters))
            offenders.Add("Job terminal minting requires one protected static actual-reader/pair/trace factory");
        if (witness.Module.Assembly.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute"))
            offenders.Add("SharedKernel friend assemblies reopen the job witness authority");
        CheckSet(offenders, "current job terminal minting subtypes", types.Values.Where(type => Derives(type, source, types)).Select(TypeId), [TypeId(terminal)]);
        CheckSet(offenders, "job witness producing methods", types.Values.SelectMany(type => type.Methods)
            .Where(method => method.ReturnType.FullName == JobTerminalWitness).Select(MethodId), factories.Select(MethodId));
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
        var sites = CallSites(census).ToArray();
        CheckSet(offenders, "job witness constructor callers", sites.Where(site => site.Target.Name == ".ctor" && SameType(site.Target.DeclaringType, witness))
            .Select(site => site.Owner), factories.Select(MethodId));
        CheckSet(offenders, "job witness actual-row mint callers", sites.Where(site => site.Target.Name == JobTerminalMint
                && (SameType(site.Target.DeclaringType, source) || Derives(site.Target.DeclaringType, source, types)))
            .Select(site => site.Owner), terminalizers.Select(MethodId));
        CheckSet(offenders, "sixth audit callers", sites.Where(site => IsJobTerminalWriteName(site.Target)
                && (SameType(site.Target.DeclaringType, port) || Derives(site.Target.DeclaringType, port, types)))
            .Select(site => site.Owner), terminalizers.Select(MethodId));

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
