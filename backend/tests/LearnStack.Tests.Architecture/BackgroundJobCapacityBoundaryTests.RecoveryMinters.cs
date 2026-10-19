using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string RecoveryHistoryCandidate = CapacityRecoverySource + "/CheckpointHistoryCandidate";
    private const string RecoveryExternalCandidate = CapacityRecoverySource + "/ExternalRecoveryCandidate";
    private const string RecoveryRenewalCandidate = CapacityRecoverySource + "/ExternalRecoveryRenewalCandidate";
    private const string RecoveryCheckpointConfirmation = "LearnStack.Application.Jobs.BackgroundJobCheckpointConfirmation";
    private const string RecoveryHistoryFacts = "LearnStack.Application.Jobs.BackgroundJobCheckpointHistoryFacts";
    private const string RecoveryExternalReceipt = "LearnStack.Application.Jobs.BackgroundJobExternalRecoveryReceipt";
    private const string RecoveryExternalRenewal = "LearnStack.Application.Jobs.BackgroundJobExternalRecoveryRenewal";
    private const string RecoveryArm = "LearnStack.Application.Jobs.RecordBackgroundJobArmCommand";
    private const string RecoveryOutcome = "LearnStack.Application.Jobs.RecordBackgroundJobOutcomeCommand";
    private const string RecoveryReader = "System.Data.Common.DbDataReader";
    private const string RecoveryRuntime = JobRunGate + "/RuntimeStop";
    private const string RecoveryResources = "LearnStack.Infrastructure.Jobs.BackgroundJobControlResources";
    private const string RecoveryRenewalRow = "LearnStack.Infrastructure.Jobs.BackgroundJobRenewal";

    // Repetition is intentional: three distinct signatures share CaptureCheckpointHistory.
    private static string[] RecoveryMinterSurface() => [".ctor", "CaptureCandidate", "ConfirmCandidate",
        "CaptureCheckpointHistory", "CaptureCheckpointHistory", "CaptureCheckpointHistory", "PublishCheckpointHistory",
        "CaptureSelectedNormalHistory", "CaptureExternalRecoveryCandidate", "PublishExternalRecoveryCandidate",
        "CaptureExternalRecoveryRenewalCandidate", "PublishExternalRecoveryRenewalCandidate"];

    private sealed record RecoveryMinterSeam(string Key, string Type, string Name, string Result,
        string[] Parameters, MethodAttributes Access, bool Static, string[] Owners);

    private static RecoveryMinterSeam[] RecoveryMinterSeams() =>
    [
        new("capture-receipt", CapacityRecoverySource, "CaptureCheckpointHistory", RecoveryHistoryCandidate,
            [JobStartReceipt, JobCommitGate, "System.Int64", RecoveryReader], MethodAttributes.Family, true, ["store:ProbeExternalCheckpointAsync"]),
        new("capture-arm", CapacityRecoverySource, "CaptureCheckpointHistory", RecoveryHistoryCandidate,
            [RecoveryArm, "System.Int64", RecoveryReader], MethodAttributes.Family, true, ["store:ProbeExternalCheckpointAsync"]),
        new("capture-outcome", CapacityRecoverySource, "CaptureCheckpointHistory", RecoveryHistoryCandidate,
            [RecoveryOutcome, "System.Int64", RecoveryReader], MethodAttributes.Family, true, ["store:ProbeExternalCheckpointAsync"]),
        new("history-candidate", RecoveryHistoryCandidate, ".ctor", "System.Void",
            [JobStartReceipt, JobCommitGate, RecoveryArm, RecoveryOutcome, "System.Int64", RecoveryReader], MethodAttributes.Assembly, false,
            ["capture-receipt", "capture-arm", "capture-outcome"]),
        new("publish-history", CapacityRecoverySource, "PublishCheckpointHistory", RecoveryCheckpointConfirmation,
            [RecoveryHistoryCandidate], MethodAttributes.Family, true, ["store:ProbeExternalCheckpointAsync"]),
        new("confirmation", RecoveryCheckpointConfirmation, ".ctor", "System.Void",
            [JobStartReceipt, JobCommitGate, RecoveryArm, RecoveryOutcome, RecoveryHistoryFacts, "System.Int64"], MethodAttributes.Assembly, false,
            ["publish-history"]),
        new("record-confirmation", JobCommitGate, "RecordConfirmation", "System.Void", [RecoveryCheckpointConfirmation],
            MethodAttributes.Assembly, false, ["publish-history"]),
        new("history-facts", RecoveryHistoryFacts, ".ctor", "System.Void", [RecoveryReader], MethodAttributes.Assembly, false,
            ["history-candidate", "capture-selected"]),
        new("capture-selected", CapacityRecoverySource, "CaptureSelectedNormalHistory", RecoveryHistoryFacts,
            [RecoveryReader], MethodAttributes.Family, true,
            ["store:ReadSelectedFinalizationAsync", "store:ReadNoPlanTerminalHistoryAsync", "store:ReadKnownRetryHistoryAsync",
                "store:ReadUnresolvedHistoryAsync", "store:ReadTerminalNoStartHistoryAsync",
                "store:ReadFinalizerControlHistoryAsync", "store:ReadFinalizerControlHistoryAsync"]),
        new("capture-recovery", CapacityRecoverySource, "CaptureExternalRecoveryCandidate", RecoveryExternalCandidate,
            [RecoveryReader, RecoveryRuntime, "System.Int64"], MethodAttributes.Family, true, ["store:ReadExternalRecoveryAsync"]),
        new("recovery-candidate", RecoveryExternalCandidate, ".ctor", "System.Void",
            [RecoveryReader, RecoveryRuntime, "System.Int64"], MethodAttributes.Assembly, false, ["capture-recovery"]),
        new("capture-recovery-core", RecoveryExternalReceipt, "Capture", RecoveryExternalReceipt,
            [RecoveryReader, RecoveryRuntime, "System.Int64"], MethodAttributes.Assembly, true, ["recovery-candidate"]),
        new("recovery-receipt", RecoveryExternalReceipt, ".ctor", "System.Void",
            [RecoveryReader, RecoveryRuntime, "System.Int64"], MethodAttributes.Private, false, ["capture-recovery-core"]),
        new("publish-recovery", CapacityRecoverySource, "PublishExternalRecoveryCandidate", RecoveryExternalReceipt,
            [RecoveryExternalCandidate], MethodAttributes.Family, true,
            ["store:AcquireExternalRecoveryAsync", "store:AcquireExternalTerminalNoStartAsync", "store:ProbeExternalRecoveryAsync"]),
        new("recovery-value", RecoveryExternalCandidate, "get_Receipt", RecoveryExternalReceipt, [], MethodAttributes.Assembly, false,
            ["publish-recovery"]),
        new("capture-renewal", CapacityRecoverySource, "CaptureExternalRecoveryRenewalCandidate", RecoveryRenewalCandidate,
            [RecoveryReader, RecoveryRuntime, "System.Int64"], MethodAttributes.Family, true, ["store:RenewBatchAsync"]),
        new("renewal-candidate", RecoveryRenewalCandidate, ".ctor", "System.Void",
            [RecoveryReader, RecoveryRuntime, "System.Int64"], MethodAttributes.Assembly, false, ["capture-renewal"]),
        new("capture-renewal-core", RecoveryExternalRenewal, "Capture", RecoveryExternalRenewal,
            [RecoveryReader, RecoveryRuntime, "System.Int64"], MethodAttributes.Assembly, true, ["renewal-candidate"]),
        new("renewal-evidence", RecoveryExternalRenewal, ".ctor", "System.Void",
            [RecoveryReader, RecoveryRuntime, "System.Int64"], MethodAttributes.Private, false, ["capture-renewal-core"]),
        new("publish-renewal", CapacityRecoverySource, "PublishExternalRecoveryRenewalCandidate", RecoveryExternalRenewal,
            [RecoveryRenewalCandidate], MethodAttributes.Family, true, ["renewal-callback"]),
        new("renewal-value", RecoveryRenewalCandidate, "get_Evidence", RecoveryExternalRenewal, [], MethodAttributes.Assembly, false,
            ["publish-renewal"]),
    ];

    private static void CheckRecoveryMinterSeams(ProductionCensus census, SortedSet<string> offenders,
        ILookup<string, OwnedCallSite> sitesByName, ILookup<string, Instruction> instructionsByName,
        (MethodDefinition Body, MethodDefinition Owner)[] bodies)
    {
        var seams = RecoveryMinterSeams();
        var subjects = seams.Select(seam => seam.Type).Distinct().ToArray();
        foreach (var name in subjects)
            if (!census.Types.ContainsKey((DeliveryApplication, name))) offenders.Add("Missing recovery minter subject: " + name);
        if (offenders.Count != 0) return;
        // Reuse the enclosing detector's fresh scan. Only guarded target names
        // enter these indexes; no seam rescans all production methods or calls.
        var names = seams.Select(seam => seam.Name).Distinct(StringComparer.Ordinal).ToArray();
        var guardedTypes = subjects.ToHashSet(StringComparer.Ordinal);
        var sitesByTarget = names.SelectMany(name => sitesByName[name])
            .Where(site => guardedTypes.Contains(site.Target.DeclaringType.FullName))
            .ToLookup(site => (site.Target.DeclaringType.FullName, site.Target.Name));
        var instructionsByTarget = names.SelectMany(name => instructionsByName[name])
            .Where(instruction => guardedTypes.Contains(((MethodReference)instruction.Operand).DeclaringType.FullName))
            .ToLookup(instruction => (((MethodReference)instruction.Operand).DeclaringType.FullName, ((MethodReference)instruction.Operand).Name));
        var bodiesByOwner = bodies.ToLookup(item => (item.Owner.DeclaringType.FullName, item.Owner.Name));
        var resolved = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
        foreach (var seam in seams)
        {
            var methods = census.Types[(DeliveryApplication, seam.Type)].Methods.Where(method =>
                method.Name == seam.Name && ParametersMatch(method, seam.Parameters)).ToArray();
            if (methods.Length != 1) { offenders.Add("Missing or overloaded recovery minter signature: " + seam.Key); continue; }
            var method = methods[0];
            resolved.Add(seam.Key, method);
            if (method.HasGenericParameters || method.IsStatic != seam.Static || method.ReturnType.FullName != seam.Result
                || (method.Attributes & MethodAttributes.MemberAccessMask) != seam.Access)
                offenders.Add("Changed recovery minter signature: " + seam.Key);
        }
        // Closed groups catch a borrowed name with different parameters, including constructors.
        foreach (var group in seams.GroupBy(seam => (seam.Type, seam.Name)))
            if (census.Types[(DeliveryApplication, group.Key.Type)].Methods.Count(method => method.Name == group.Key.Name) != group.Count())
                offenders.Add("Extra or missing recovery minter overload: " + group.Key);
        var store = census.Types[(InfrastructureAssembly, OrchestrationStore)];
        foreach (var name in seams.SelectMany(seam => seam.Owners).Where(name => name.StartsWith("store:", StringComparison.Ordinal))
                     .Select(name => name[6..]).Distinct())
        {
            var methods = store.Methods.Where(method => method.Name == name).ToArray();
            if (methods.Length != 1) offenders.Add("Missing or overloaded recovery minter owner: " + name);
            else resolved.Add("store:" + name, methods[0]);
        }
        if (resolved.TryGetValue("store:RenewBatchAsync", out var renewal))
        {
            var callbacks = bodiesByOwner[(OrchestrationStore, "RenewBatchAsync")].SelectMany(item => DelegateTargets(item.Body))
                .Select(reference => census.Types.TryGetValue(Key(reference.DeclaringType), out var declaring)
                    ? declaring.Methods.SingleOrDefault(method => method.FullName == reference.FullName) : null)
                .Where(method => method is not null && ParametersMatch(method, [RecoveryRenewalRow])
                    && method.ReturnType.FullName == RecoveryRenewalRow).Cast<MethodDefinition>().Distinct().ToArray();
            if (callbacks.Length != 1) offenders.Add("Renewal publication requires its single actual bound projection callback");
            else
            {
                var callback = callbacks[0];
                resolved.Add("renewal-callback", callback);
                if (callback.IsPublic || callback.IsStatic || callback.HasGenericParameters
                    || !callback.DeclaringType.IsNestedPrivate || callback.DeclaringType.DeclaringType != store)
                    offenders.Add("Renewal publication callback remains its private captured projection");
                CheckSet(offenders, "renewal publication callback issuance", sitesByName[callback.Name].Where(site =>
                    TargetsDeclaredType(site.Target, callback.DeclaringType) && ParametersMatch(site.Target, [RecoveryRenewalRow]))
                    .Select(site => MethodId(site.Owner)), [MethodId(renewal)]);
            }
        }
        var ids = resolved.ToDictionary(item => item.Key, item => MethodId(item.Value), StringComparer.Ordinal);
        foreach (var seam in seams)
        {
            if (!resolved.TryGetValue(seam.Key, out var method)) continue;
            CheckSet(offenders, "exact recovery mint callers " + seam.Key,
                sitesByTarget[(seam.Type, seam.Name)].Where(site => TargetsDeclaredType(site.Target, method.DeclaringType)
                    && ParametersMatch(site.Target, seam.Parameters)).Select(site => MethodId(site.Owner)),
                seam.Owners.Select(owner => ids.TryGetValue(owner, out var id) ? id : "missing:" + owner));
            foreach (var instruction in instructionsByTarget[(seam.Type, seam.Name)].Where(instruction =>
                         instruction.Operand is MethodReference target && TargetsDeclaredType(target, method.DeclaringType)
                         && ParametersMatch(target, seam.Parameters)))
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt
                    && !(seam.Name == ".ctor" && instruction.OpCode == OpCodes.Newobj))
                    offenders.Add("Recovery mint capability escapes as a delegate: " + seam.Key);
        }
        foreach (var name in new[] { RecoveryHistoryCandidate, RecoveryExternalCandidate, RecoveryRenewalCandidate,
                     RecoveryCheckpointConfirmation, RecoveryHistoryFacts, RecoveryExternalReceipt, RecoveryExternalRenewal })
        {
            var type = census.Types[(DeliveryApplication, name)];
            if (!type.IsSealed || type.Properties.Any(property => property.SetMethod is not null)
                || type.Fields.Any(field => !field.IsStatic && !field.IsInitOnly
                    && !(name == RecoveryExternalReceipt && field.Name == "_binding")))
                offenders.Add("Recovery candidate/evidence facts remain closed and immutable: " + name);
            if (name.StartsWith(CapacityRecoverySource + "/", StringComparison.Ordinal) ? !type.IsNestedFamily : !type.IsPublic)
                offenders.Add("Recovery candidate/evidence retains its exact visibility: " + name);
        }
        foreach (var (name, properties) in new (string, (string Name, string Type)[])[]
        {
            (RecoveryHistoryCandidate, [("Receipt", JobStartReceipt), ("Gate", JobCommitGate), ("Arm", RecoveryArm),
                ("Outcome", RecoveryOutcome), ("Facts", RecoveryHistoryFacts), ("ObservedAt", "System.Int64")]),
            (RecoveryExternalCandidate, [("Receipt", RecoveryExternalReceipt)]),
            (RecoveryRenewalCandidate, [("Evidence", RecoveryExternalRenewal)]),
        })
        {
            var candidate = census.Types[(DeliveryApplication, name)];
            CheckSet(offenders, "closed recovery candidate methods " + name, candidate.Methods.Select(method => method.Name),
                properties.Select(property => "get_" + property.Name).Prepend(".ctor"));
            CheckSet(offenders, "closed recovery candidate facts " + name,
                candidate.Properties.Select(property => property.Name + ":" + property.PropertyType.FullName),
                properties.Select(property => property.Name + ":" + property.Type));
            CheckSet(offenders, "closed recovery candidate fields " + name, candidate.Fields.Select(field => field.Name),
                properties.Select(property => "<" + property.Name + ">k__BackingField"));
        }
        CheckRecoveryPublicationOrder(census, offenders, resolved, bodiesByOwner);
    }

    private static void CheckRecoveryPublicationOrder(ProductionCensus census, SortedSet<string> offenders,
        Dictionary<string, MethodDefinition> resolved,
        ILookup<(string Type, string Name), (MethodDefinition Body, MethodDefinition Owner)> bodiesByOwner)
    {
        foreach (var (owner, publication, probe) in new[]
        {
            ("AcquireExternalRecoveryAsync", "publish-recovery", false),
            ("AcquireExternalTerminalNoStartAsync", "publish-recovery", false),
            ("ProbeExternalRecoveryAsync", "publish-recovery", true),
            ("ProbeExternalCheckpointAsync", "publish-history", true),
            ("RenewBatchAsync", "renewal-callback", false),
        })
        {
            var body = bodiesByOwner[(OrchestrationStore, owner)].Where(item => item.Body.Name == "MoveNext")
                .SelectMany(item => item.Body.Body.Instructions).ToArray();
            int Find(string type, string name) => Array.FindIndex(body, instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == type && target.Name == name);
            var commit = probe ? Array.FindIndex(body, instruction => instruction.Operand is MethodReference target
                && IntegrationLifecycleCapability(target, census.Types) == "database.RollbackAsync") : Find(OrchestrationStore, "CommitAsync");
            var cleanup = Find(RecoveryResources, "CleanupAsync");
            var ack = Find(RecoveryResources, probe ? "get_ProbeRollbackAcknowledged" : "get_CommitAcknowledged");
            var clean = Find(RecoveryResources, "get_CleanupConfirmed");
            var failure = Find(RecoveryResources, "get_Failure");
            var publish = resolved.TryGetValue(publication, out var targetMethod) ? Array.FindIndex(body,
                instruction => instruction.Operand is MethodReference target && target.Name == targetMethod.Name
                    && TargetsDeclaredType(target, targetMethod.DeclaringType)) : -1;
            bool Joined(int before, int after) => before >= 0 && after > before
                && body.Skip(before + 1).Take(after - before - 1).Any(IsCapacityAwaitResult);
            if (!Joined(commit, cleanup) || !Joined(cleanup, publish) || ack <= cleanup || clean <= ack || failure <= clean || publish <= failure)
                offenders.Add("Recovery publication follows actual transaction completion, cleanup and positive gates: " + owner);
        }
    }

    private static void AssertRecoveryMinterMutations(ProductionCensus census)
    {
        foreach (var name in new[] { RecoveryHistoryCandidate, RecoveryExternalCandidate, RecoveryRenewalCandidate,
                     RecoveryCheckpointConfirmation, RecoveryHistoryFacts, RecoveryExternalReceipt, RecoveryExternalRenewal })
        {
            var type = census.Types[(DeliveryApplication, name)];
            AssertMutation(census, "an inheritable recovery candidate/evidence", () => ChangeAttributes(() => type.Attributes,
                value => type.Attributes = value, type.Attributes & ~TypeAttributes.Sealed), JobCapacityOffenders);
            foreach (var field in type.Fields.Where(field => !field.IsStatic && field.IsInitOnly))
                AssertMutation(census, "a mutable recovery identity fact", () => ChangeAttributes(() => field.Attributes,
                    value => field.Attributes = value, field.Attributes & ~FieldAttributes.InitOnly), JobCapacityOffenders);
        }
        foreach (var name in new[] { RecoveryHistoryCandidate, RecoveryExternalCandidate, RecoveryRenewalCandidate })
        {
            var type = census.Types[(DeliveryApplication, name)];
            AssertMutation(census, "an alternate candidate authority getter", () =>
            {
                var extra = new MethodDefinition("ExposeCandidate", MethodAttributes.Public, type.Module.TypeSystem.Object);
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                type.Methods.Add(extra);
                return () => type.Methods.Remove(extra);
            }, JobCapacityOffenders);
        }
        foreach (var seam in RecoveryMinterSeams())
        {
            var method = census.Types[(DeliveryApplication, seam.Type)].Methods.Single(method =>
                method.Name == seam.Name && ParametersMatch(method, seam.Parameters));
            AssertMutation(census, "a public recovery minter or witness constructor", () => ChangeAttributes(() => method.Attributes,
                value => method.Attributes = value, (method.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public), JobCapacityOffenders);
            AssertMutation(census, "a changed recovery minter return type", () => ChangeAttributes(() => method.ReturnType,
                value => method.ReturnType = value, method.Module.TypeSystem.Object), JobCapacityOffenders);
        }
        foreach (var (owner, publication) in new[] { ("AcquireExternalRecoveryAsync", "PublishExternalRecoveryCandidate"),
                     ("AcquireExternalTerminalNoStartAsync", "PublishExternalRecoveryCandidate"),
                     ("ProbeExternalRecoveryAsync", "PublishExternalRecoveryCandidate"),
                     ("ProbeExternalCheckpointAsync", "PublishCheckpointHistory") })
            AssertEarlyCapacityCall(census, OrchestrationStore, owner, CapacityRecoverySource, publication,
                "recovery evidence published before its actual completion and cleanup");
        foreach (var (owner, probe) in new[] { ("AcquireExternalRecoveryAsync", false),
                     ("AcquireExternalTerminalNoStartAsync", false), ("ProbeExternalRecoveryAsync", true),
                     ("ProbeExternalCheckpointAsync", true), ("RenewBatchAsync", false) })
        {
            var body = OwnedBodies(census).Single(item => item.Owner.DeclaringType.FullName == OrchestrationStore
                && item.Owner.Name == owner && item.Body.Name == "MoveNext").Body.Body;
            foreach (var name in new[] { "CleanupAsync", probe ? "get_ProbeRollbackAcknowledged" : "get_CommitAcknowledged",
                         "get_CleanupConfirmed", "get_Failure" })
            {
                var guard = body.Instructions.Single(instruction => instruction.Operand is MethodReference target
                    && target.DeclaringType.FullName == RecoveryResources && target.Name == name);
                AssertMutation(census, "a removed recovery publication cleanup/ACK guard " + owner + "/" + name,
                    () => ReplaceInstruction(guard, OpCodes.Nop, null!), JobCapacityOffenders);
            }
        }
        // Existing facts may be read without granting another mint or issuer.
        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        var clean = AddProbe(application, "RecoveryReadOnlyConsumer");
        try
        {
            foreach (var name in new[] { RecoveryExternalReceipt, RecoveryHistoryFacts, RecoveryCheckpointConfirmation })
                foreach (var property in census.Types[(DeliveryApplication, name)].Properties.Where(property => property.GetMethod!.IsPublic))
                    AddCall(clean, "Read" + name.Split('.').Last() + property.Name, property.GetMethod!, OpCodes.Call);
            EvaluateDetector(census, JobCapacityOffenders).Should().BeEmpty("read-only consumers mint no recovery authority");
        }
        finally { application.Types.Remove(clean); }
    }
}
