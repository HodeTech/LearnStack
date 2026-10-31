using System.Diagnostics;
using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private static void CheckJobTracing(ProductionCensus census, SortedSet<string> offenders,
        ILookup<string, (string Owner, MethodReference Target)>? sitesByName = null)
    {
        var types = census.Types;
        if (!types.TryGetValue((InfrastructureAssembly, JobObservation), out var observer))
        {
            offenders.Add("Missing concrete job trace source owner");
            return;
        }
        var constructor = RequireBoundaryMethod(offenders, observer, ".ctor", "System.Void", MethodAttributes.Public,
            "LearnStack.Infrastructure.Jobs.BackgroundJobDefinitionRegistry", "System.TimeProvider");
        var factory = RequireBoundaryMethod(offenders, observer, "CreateActivity", "System.Diagnostics.Activity", MethodAttributes.Assembly,
            "LearnStack.Infrastructure.Jobs.JobTraceOperation", "System.Diagnostics.ActivityContext");
        var dispose = RequireBoundaryMethod(offenders, observer, "Dispose", "System.Void", MethodAttributes.Public);
        var field = observer.Fields.SingleOrDefault(candidate => candidate.Name == "_activities");
        if (field is not { IsPrivate: true, IsInitOnly: true, IsStatic: false } || field.FieldType.FullName != "System.Diagnostics.ActivitySource")
            offenders.Add("The job trace source must have its concrete private readonly singleton owner");
        CheckBridgeCapturedField(offenders, census, observer, "_activities", "System.Diagnostics.ActivitySource",
            [constructor + ":stfld", factory + ":ldfld", dispose + ":ldfld"]);
        var ownedBodies = OwnedBodies(census).ToArray();
        var jobOwners = ownedBodies.Where(item => item.Owner.DeclaringType.Namespace == "LearnStack.Infrastructure.Jobs")
            .Select(item => MethodId(item.Owner)).ToHashSet(StringComparer.Ordinal);
        sitesByName ??= RuntimeCallSitesByName(CallSites(census));
        var expected = JobStageExecutorNames.Select(name => JobOwner(offenders, types, InfrastructureAssembly, JobExecutorType, name))
            .Append(JobOwner(offenders, types, InfrastructureAssembly, OrchestrationControl, "RunTerminalAsync")).ToArray();
        var factoryCalls = census.MethodInstructions("CreateActivity").Where(site => HasAttachedDeclaringType(site.Target)
            && TargetsDeclaredType(site.Target, observer)).ToArray();
        CheckSet(offenders, "exact job trace factory callers and opcodes", factoryCalls.Select(site => site.OwnerId + ":" + site.Instruction.OpCode.Name),
            expected.Select(owner => owner + ":callvirt"));
        CheckSet(offenders, "actual job trace source creation", sitesByName[".ctor"].Where(site => HasAttachedDeclaringType(site.Target)
                && site.Target.DeclaringType.FullName == "System.Diagnostics.ActivitySource"
                && site.Target.Name == ".ctor" && jobOwners.Contains(site.Owner))
            .Select(site => site.Owner), [constructor]);
        CheckSet(offenders, "actual job trace fallback creation", sitesByName[".ctor"].Where(site => HasAttachedDeclaringType(site.Target)
                && site.Target.DeclaringType.FullName == "System.Diagnostics.Activity"
                && site.Target.Name == ".ctor" && jobOwners.Contains(site.Owner))
            .Select(site => site.Owner), [factory]);
        var declaration = observer.Methods.SingleOrDefault(method => MethodId(method) == factory);
        var sourceOwner = observer.Methods.SingleOrDefault(method => MethodId(method) == constructor);
        var sourceConstructions = sourceOwner?.Body.Instructions.Where(instruction => instruction.Operand is MethodReference call
            && HasAttachedDeclaringType(call) && call.DeclaringType.FullName == "System.Diagnostics.ActivitySource" && call.Name == ".ctor").ToArray() ?? [];
        if (sourceConstructions is not [var sourceConstruction] || sourceConstruction.OpCode != OpCodes.Newobj
            || ((MethodReference)sourceConstruction.Operand).Parameters.Count != 1
            || sourceConstruction.Previous?.OpCode != OpCodes.Ldstr || sourceConstruction.Previous.Operand is not "learnstack.jobs"
            || sourceConstruction.Previous.Previous?.OpCode != OpCodes.Ldarg_0
            || sourceConstruction.Next?.OpCode != OpCodes.Stfld || sourceConstruction.Next.Operand is not FieldReference sourceDestination
            || sourceDestination.FullName != field?.FullName)
            offenders.Add("The actual job trace source must consume learnstack.jobs and assign its concrete source field");
        CheckSet(offenders, "actual job source disposal", sitesByName["Dispose"].Where(site => HasAttachedDeclaringType(site.Target)
                && site.Target.DeclaringType.FullName == "System.Diagnostics.ActivitySource"
                && site.Target.Name == "Dispose" && jobOwners.Contains(site.Owner)).Select(site => site.Owner), [dispose]);
        var disposalOwner = observer.Methods.SingleOrDefault(method => MethodId(method) == dispose);
        var sourceDisposals = disposalOwner?.Body.Instructions.Where(instruction => instruction.Operand is MethodReference call
            && HasAttachedDeclaringType(call) && call.DeclaringType.FullName == "System.Diagnostics.ActivitySource" && call.Name == "Dispose").ToArray() ?? [];
        if (sourceDisposals is not [var sourceDisposal] || sourceDisposal.OpCode != OpCodes.Callvirt
            || ((MethodReference)sourceDisposal.Operand).Parameters.Count != 0
            || ((MethodReference)sourceDisposal.Operand).ReturnType.FullName != "System.Void"
            || sourceDisposal.Previous?.OpCode != OpCodes.Ldfld
            || sourceDisposal.Previous.Operand is not FieldReference disposedSource || disposedSource.FullName != field?.FullName
            || sourceDisposal.Previous.Previous?.OpCode != OpCodes.Ldarg_0)
            offenders.Add("Job trace disposal must consume this observation's actual source");
        var creations = declaration?.Body.Instructions.Where(instruction => instruction.Operand is MethodReference call
            && HasAttachedDeclaringType(call) && call.DeclaringType.FullName == "System.Diagnostics.ActivitySource" && call.Name == "CreateActivity").ToArray() ?? [];
        if (creations is not [var creation]) offenders.Add("The actual job trace factory requires its one named source call");
        else
        {
            var index = declaration!.Body.Instructions.IndexOf(creation);
            var args = declaration.Body.Instructions.Skip(Math.Max(0, index - 8)).Take(8).ToArray();
            if (((MethodReference)creation.Operand).Parameters.Count != 6 || creation.OpCode != OpCodes.Callvirt
                || args.Length != 8 || args[0].OpCode != OpCodes.Ldarg_0 || args[1].OpCode != OpCodes.Ldfld
                || args[1].Operand is not FieldReference source || source.FullName != field?.FullName
                || args[2].OpCode != OpCodes.Ldloc_0 || args[3].OpCode != OpCodes.Ldc_I4_0
                || args[4].OpCode != OpCodes.Ldarg_2 || args[5].OpCode != OpCodes.Ldnull
                || args[6].OpCode != OpCodes.Ldnull || args[7].OpCode != OpCodes.Ldc_I4_2)
                offenders.Add("The actual trace factory must consume its owned source, selected name, declared parent and W3C format");
        }
        if (declaration?.HasBody != true || declaration.Body.Instructions.Any(instruction => instruction.Operand is MethodReference call
                && HasAttachedDeclaringType(call) && call.DeclaringType.FullName is "System.Diagnostics.Activity" or "System.Diagnostics.ActivitySource"
                && call.Name is "Start" or "StartActivity"))
            offenders.Add("The actual job trace factory must return only unstarted owned Activities");
        foreach (var call in factoryCalls)
        {
            var body = ownedBodies.SingleOrDefault(candidate => candidate.Body.Body.Instructions.Contains(call.Instruction)).Body;
            var store = call.Instruction.Next;
            var terminal = call.OwnerId == expected[^1];
            if (body is null || store?.OpCode != OpCodes.Stfld || store.Operand is not FieldReference destination
                || destination.FieldType.FullName != "System.Diagnostics.Activity"
                || (terminal ? destination.Name != "_terminalActivity" || destination.DeclaringType.FullName != OrchestrationControl
                    : !destination.Name.StartsWith("<activity>5__", StringComparison.Ordinal) || destination.DeclaringType.FullName != body.DeclaringType.FullName)
                || !LoadsTraceOwner(store.Next, body, terminal) || store.Next!.Next?.OpCode != OpCodes.Ldfld
                || store.Next.Next.Operand is not FieldReference receiver || receiver.FullName != destination.FullName
                || store.Next.Next.Next?.OpCode != OpCodes.Callvirt
                || store.Next.Next.Next.Operand is not MethodReference { Name: "Start", DeclaringType.FullName: "System.Diagnostics.Activity" })
                offenders.Add(call.OwnerId + ": job trace must assign its actual cleanup-owned Activity before Start");
            else
            {
                var disposal = body.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
                    && HasAttachedDeclaringType(target) && target.DeclaringType.FullName == "System.Diagnostics.Activity" && target.Name == "Dispose").ToArray();
                var disposed = disposal.Length == 1 ? disposal[0] : null;
                var index = disposed is null ? -1 : body.Body.Instructions.IndexOf(disposed);
                var load = terminal ? disposed?.Previous : index >= 6 ? body.Body.Instructions[index - 5] : null;
                if (disposed is null || disposed.OpCode != (terminal ? OpCodes.Callvirt : OpCodes.Call)
                    || ((MethodReference)disposed.Operand).Parameters.Count != 0
                    || ((MethodReference)disposed.Operand).ReturnType.FullName != "System.Void"
                    || load?.OpCode != OpCodes.Ldfld || load.Operand is not FieldReference cleaned || cleaned.FullName != destination.FullName
                    || !LoadsTraceOwner(load.Previous, body, terminal)
                    || !terminal && (body.Body.Instructions[index - 4].OpCode != OpCodes.Dup
                        || body.Body.Instructions[index - 3].OpCode.Code is not (Code.Brtrue or Code.Brtrue_S)
                        || !ReferenceEquals(body.Body.Instructions[index - 3].Operand, disposed)
                        || body.Body.Instructions[index - 2].OpCode != OpCodes.Pop
                        || body.Body.Instructions[index - 1].OpCode.Code is not (Code.Br or Code.Br_S)))
                    offenders.Add(call.OwnerId + ": job trace Start and actual Dispose must consume the same retained slot");
            }
        }
    }

    private static bool LoadsTraceOwner(Instruction? instruction, MethodDefinition body, bool terminal)
    {
        if (instruction is null) return false;
        if (!terminal) return instruction.OpCode == OpCodes.Ldarg_0;
        var index = instruction.OpCode.Code switch
        {
            Code.Ldloc_0 => 0,
            Code.Ldloc_1 => 1,
            Code.Ldloc_2 => 2,
            Code.Ldloc_3 => 3,
            Code.Ldloc or Code.Ldloc_S when instruction.Operand is VariableDefinition variable => variable.Index,
            _ => -1,
        };
        return index >= 0 && index < body.Body.Variables.Count && body.Body.Variables[index].VariableType.FullName == OrchestrationControl;
    }

    [Fact]
    public void Job_trace_source_and_actual_Activity_ownership_refuse_chained_Start_and_hidden_source_creation()
    {
        using var census = ProductionCensus.Read();
        SortedSet<string> Detect()
        {
            var result = new SortedSet<string>(StringComparer.Ordinal);
            CheckJobTracing(census, result);
            return result;
        }
        Detect().Should().BeEmpty();
        var observer = census.Types[(InfrastructureAssembly, JobObservation)];
        var sourceConstructor = observer.Methods.Single(method => method.IsConstructor && !method.IsStatic);
        var consumedName = sourceConstructor.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldstr
            && instruction.Next?.Operand is MethodReference { Name: ".ctor", DeclaringType.FullName: "System.Diagnostics.ActivitySource" });
        var originalName = consumedName.Operand;
        var bait = Instruction.Create(OpCodes.Ldstr, "learnstack.jobs");
        var discard = Instruction.Create(OpCodes.Pop);
        sourceConstructor.Body.Instructions.Insert(0, discard);
        sourceConstructor.Body.Instructions.Insert(0, bait);
        consumedName.Operand = "unexported.jobs";
        try { Detect().Should().Contain(value => value.Contains("must consume learnstack.jobs", StringComparison.Ordinal)); }
        finally
        {
            consumedName.Operand = originalName;
            sourceConstructor.Body.Instructions.Remove(bait);
            sourceConstructor.Body.Instructions.Remove(discard);
        }
        Detect().Should().BeEmpty();
        var site = census.MethodInstructions("CreateActivity").Single(candidate => TargetsDeclaredType(candidate.Target, observer)
            && candidate.OwnerId == JobOwner(new(StringComparer.Ordinal), census.Types, InfrastructureAssembly, JobExecutorType, "ExecuteCapacityCleanupAsync"));
        var body = OwnedBodies(census).Single(candidate => candidate.Body.Body.Instructions.Contains(site.Instruction)).Body.Body;
        var earlyStart = Instruction.Create(OpCodes.Callvirt, body.Method.Module.ImportReference(typeof(Activity).GetMethod(nameof(Activity.Start), Type.EmptyTypes)!));
        body.Instructions.Insert(body.Instructions.IndexOf(site.Instruction) + 1, earlyStart);
        try { Detect().Should().Contain(value => value.Contains("assign its actual cleanup-owned Activity before Start", StringComparison.Ordinal)); }
        finally { body.Instructions.Remove(earlyStart); }
        Detect().Should().BeEmpty();

        var store = site.Instruction.Next;
        var destination = (FieldReference)store.Operand;
        var load = store.Next.Next;
        var spare = new FieldDefinition("unownedTrace", FieldAttributes.Public, body.Method.Module.ImportReference(typeof(Activity)));
        body.Method.DeclaringType.Fields.Add(spare);
        store.Operand = spare;
        load.Operand = spare;
        try { Detect().Should().Contain(value => value.Contains("assign its actual cleanup-owned Activity", StringComparison.Ordinal)); }
        finally
        {
            store.Operand = destination;
            load.Operand = destination;
            body.Method.DeclaringType.Fields.Remove(spare);
        }
        Detect().Should().BeEmpty();

        var disposalOwner = observer.Methods.Single(method => method.Name == "Dispose");
        var sourceDispose = disposalOwner.Body.Instructions.Single(instruction => instruction.Operand is MethodReference
        { Name: "Dispose", DeclaringType.FullName: "System.Diagnostics.ActivitySource" });
        var activityDispose = body.Instructions.Single(instruction => instruction.Operand is MethodReference
        { Name: "Dispose", DeclaringType.FullName: "System.Diagnostics.Activity" });
        foreach (var (owner, instruction, diagnostic) in new[]
                 {
                     (disposalOwner, sourceDispose, "disposal must consume this observation's actual source"),
                     (body.Method, activityDispose, "Start and actual Dispose must consume the same retained slot"),
                 })
        {
            var originalOpcode = instruction.OpCode;
            instruction.OpCode = OpCodes.Ldftn;
            var capture = Instruction.Create(OpCodes.Newobj, owner.Module.ImportReference(typeof(Action).GetConstructor([typeof(object), typeof(IntPtr)])!));
            var pop = Instruction.Create(OpCodes.Pop);
            var index = owner.Body.Instructions.IndexOf(instruction);
            owner.Body.Instructions.Insert(index + 1, capture);
            owner.Body.Instructions.Insert(index + 2, pop);
            try { Detect().Should().Contain(value => value.Contains(diagnostic, StringComparison.Ordinal)); }
            finally
            {
                instruction.OpCode = originalOpcode;
                owner.Body.Instructions.Remove(capture);
                owner.Body.Instructions.Remove(pop);
            }
            Detect().Should().BeEmpty();
        }

        var initializer = observer.Methods.Single(method => method.IsConstructor && method.IsStatic);
        var create = Instruction.Create(OpCodes.Newobj, observer.Module.ImportReference(typeof(ActivitySource).GetConstructor([typeof(string), typeof(string)])!));
        var injected = new[] { Instruction.Create(OpCodes.Ldstr, "hidden.job.source"), Instruction.Create(OpCodes.Ldnull), create, Instruction.Create(OpCodes.Pop) };
        for (var index = 0; index < injected.Length; index++) initializer.Body.Instructions.Insert(index, injected[index]);
        try { Detect().Should().Contain(value => value.Contains("actual job trace source creation", StringComparison.Ordinal)); }
        finally { foreach (var instruction in injected) initializer.Body.Instructions.Remove(instruction); }
        Detect().Should().BeEmpty();

        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        var helper = AddProbe(application, "TraceMemoryHelper");
        var sql = AddCall(helper, "ReadSql", application.ImportReference(typeof(Npgsql.NpgsqlCommand).GetConstructor(Type.EmptyTypes)!), OpCodes.Newobj);
        sql.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Pop));
        var factory = observer.Methods.Single(method => method.Name == "CreateActivity");
        var call = Instruction.Create(OpCodes.Call, observer.Module.ImportReference(sql));
        factory.Body.Instructions.Insert(0, call);
        try
        {
            EvaluateDetector(census, RuntimeLifetimeOffenders).Should().Contain(value =>
                value.Contains("job stage metrics observation reaches persistence/provider activation", StringComparison.Ordinal));
        }
        finally { factory.Body.Instructions.Remove(call); application.Types.Remove(helper); }
        EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();

        helper = AddProbe(application, "TraceStartHelper");
        var start = new MethodDefinition("StartIt", MethodAttributes.Public | MethodAttributes.Static, application.ImportReference(typeof(Activity)));
        start.Parameters.Add(new(application.ImportReference(typeof(Activity))));
        start.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
        start.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt,
            application.ImportReference(typeof(Activity).GetMethod(nameof(Activity.Start), Type.EmptyTypes)!)));
        start.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        helper.Methods.Add(start);
        var hiddenStart = Instruction.Create(OpCodes.Call, observer.Module.ImportReference(start));
        var returned = factory.Body.Instructions.Last(instruction => instruction.OpCode == OpCodes.Ret);
        factory.Body.Instructions.Insert(factory.Body.Instructions.IndexOf(returned), hiddenStart);
        try
        {
            EvaluateDetector(census, RuntimeLifetimeOffenders).Should().Contain(value =>
                value.Contains("observation starts an Activity before its actual owner", StringComparison.Ordinal));
        }
        finally { factory.Body.Instructions.Remove(hiddenStart); application.Types.Remove(helper); }
        EvaluateDetector(census, RuntimeLifetimeOffenders).Should().BeEmpty();
    }
}
