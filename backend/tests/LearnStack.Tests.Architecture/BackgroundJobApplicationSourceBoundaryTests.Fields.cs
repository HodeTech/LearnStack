using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private static readonly string[] JobApplicationOwnerFields =
        ["Ambient", "External", "_sync", "_auxiliary", "_authorization", "_evidence", "_mainUnit", "_mainConnection",
         "_mainTransaction", "_closed", "_cleanupFailed", "_used", "_externalRecovery"];
    private static readonly string[] JobApplicationResourceFields =
        ["_sync", "_owner", "_connection", "_transaction", "_commands", "_reader", "_actualTask",
         "_readerDisposalAttempted", "_cleanupAttempted", "_cleanupConfirmed", "_readerCleanupFailed", "_released"];

    private static void CheckJobApplicationRetainedFields(ProductionCensus census, TypeDefinition owner,
        TypeDefinition resources, SortedSet<string> offenders)
    {
        CheckSet(offenders, "closed job application-owner resource inventory", owner.Fields.Select(field => field.Name), JobApplicationOwnerFields);
        CheckSet(offenders, "closed job auxiliary resource inventory", resources.Fields.Select(field => field.Name), JobApplicationResourceFields);
        var bodies = OwnedBodies(census).ToArray();
        var fields = bodies.SelectMany(item => item.Body.Body.Instructions.Where(instruction => instruction.Operand is FieldReference)
            .Select(instruction => (item.Owner, Instruction: instruction, Field: (FieldReference)instruction.Operand))).ToArray();

        // The three slots remain mutually exclusive within one job; these exact reads include
        // both readiness gates, acquisition gates and the uncancelable actual-cleanup join.
        Lane("_auxiliary", [("get_CleanupConfirmed", 2), ("RequireBusinessReady", 2), ("JoinAuxiliaryCleanupAsync", 1),
            ("BeginAuxiliary", 2), ("BeginConfinedLane", 2), ("Release", 1), ("OwnsAuxiliary", 1)], "BeginAuxiliary");
        Lane("_authorization", [("get_CleanupConfirmed", 2), ("RequireBusinessReady", 2), ("JoinAuxiliaryCleanupAsync", 1),
            ("BeginAuxiliary", 2), ("BeginConfinedLane", 2), ("Release", 1), ("OwnsAuxiliary", 1)], "BeginConfinedLane");
        Lane("_evidence", [("get_CleanupConfirmed", 2), ("RequireBusinessReady", 2), ("JoinAuxiliaryCleanupAsync", 1),
            ("BeginAuxiliary", 2), ("BeginConfinedLane", 2), ("OwnsAuxiliary", 1)], "BeginConfinedLane");

        Field(owner, "_cleanupFailed", "System.Boolean", false, 3,
            ["get_CleanupConfirmed", "get_IsCurrentBinding", "RequireBusinessReady", "BeginAuxiliary", "BeginConfinedLane",
             "ReportMainTransactionCleanup", "ReportMainCleanup", "ReportCleanupFailure"]);
        Field(owner, "_closed", "System.Boolean", false, 1,
            ["Enter", "get_IsCurrentBinding", "RequireBusinessReady", "CloseIssuance", "BeginAuxiliary", "BeginConfinedLane"]);
        Field(resources, "_owner", AppJobOwner, true, 1,
            [".ctor", "MarkCleanupFailed", "DisposeReaderAsync", "CleanupAsync", "TryRelease"]);
        Field(resources, "_connection", "System.Data.Common.DbConnection", false, 1, ["HoldConnection", "CleanupAsync"]);
        Field(resources, "_transaction", "System.Data.Common.DbTransaction", false, 1, ["HoldTransaction", "CleanupAsync"]);
        Field(resources, "_commands", "System.Collections.Generic.List`1<System.Data.Common.DbCommand>", true, 1,
            [".ctor", "HoldCommand", "CleanupAsync"]);
        Field(resources, "_reader", "System.Data.Common.DbDataReader", false, 1,
            ["HoldReader", "DisposeReaderAsync", "CleanupAsync"]);
        Field(resources, "_actualTask", "System.Threading.Tasks.Task", false, 1,
            ["HoldActualTask", "TryRelease", "JoinActualTaskAsync"]);
        Field(resources, "_readerDisposalAttempted", "System.Boolean", false, 1, ["DisposeReaderAsync", "CleanupAsync"]);
        Field(resources, "_cleanupAttempted", "System.Boolean", false, 1,
            ["HoldConnection", "HoldTransaction", "HoldCommand", "HoldReader", "CleanupAsync"]);
        Field(resources, "_cleanupConfirmed", "System.Boolean", false, 1, ["CleanupAsync", "TryRelease"]);
        Field(resources, "_readerCleanupFailed", "System.Boolean", false, 2,
            ["MarkCleanupFailed", "DisposeReaderAsync", "CleanupAsync"]);
        Field(resources, "_released", "System.Boolean", false, 0, ["TryRelease", "get_IsReleased", "MarkReleased"]);
        CheckSet(offenders, "one-way auxiliary release addresses", fields.Where(site => SameType(site.Field.DeclaringType, resources)
                && site.Field.Name == "_released" && site.Instruction.OpCode == OpCodes.Ldflda).Select(site => site.Owner.Name),
            ["get_IsReleased", "MarkReleased"]);

        RequireConstant(resources, ".ctor", 3);
        RequireConstant(resources, "HoldCommand", 3);
        RequireConstant(owner, "BeginAuthorizationRead", 1);
        RequireConstant(owner, "BeginAuthorizationEvidence", 0);
        foreach (var method in new[] { "get_CleanupConfirmed", "RequireBusinessReady", "BeginAuxiliary", "BeginConfinedLane" })
        {
            var reconciliations = bodies.Where(item => item.Owner.DeclaringType == owner && item.Owner.Name == method)
                .SelectMany(item => item.Body.Body.Instructions).Where(instruction => instruction.Operand is MethodReference target
                    && SameType(target.DeclaringType, resources) && target.Name == "TryRelease").ToArray();
            if (reconciliations.Length != 3 || reconciliations.Any(site => site.OpCode != OpCodes.Call && site.OpCode != OpCodes.Callvirt))
                offenders.Add("Each job readiness/acquisition gate reconciles all three completed lanes: " + method);
        }
        foreach (var type in new[] { "System.Data.Common.DbCommand", "System.Data.Common.DbTransaction", "System.Data.Common.DbConnection" })
            RequireCall("CleanupAsync", type, "DisposeAsync");
        RequireCall("DisposeReaderAsync", "System.Data.Common.DbDataReader", "DisposeAsync");
        RequireCall("TryRelease", "System.Threading.Tasks.Task", "get_IsCompleted");

        void Lane(string name, (string Method, int Reads)[] expectedReads, string publisher)
        {
            Field(owner, name, AppJobResources, false, 2, [.. expectedReads.Select(item => item.Method), publisher, "Release"]);
            var access = fields.Where(site => SameType(site.Field.DeclaringType, owner) && site.Field.Name == name).ToArray();
            CheckSet(offenders, "exact job lane readiness/acquisition/join reads " + name,
                access.Where(site => site.Instruction.OpCode == OpCodes.Ldfld).Select(site => site.Owner.Name),
                expectedReads.SelectMany(item => Enumerable.Repeat(item.Method, item.Reads)));
            CheckSet(offenders, "exact job lane publication/release " + name,
                access.Where(site => site.Instruction.OpCode == OpCodes.Stfld).Select(site => site.Owner.Name), [publisher, "Release"]);
        }

        void Field(TypeDefinition subject, string name, string type, bool readOnly, int writes, string[] expectedOwners)
        {
            var matches = subject.Fields.Where(field => field.Name == name).ToArray();
            if (matches.Length != 1 || !matches[0].IsPrivate || matches[0].IsStatic
                || matches[0].IsInitOnly != readOnly || matches[0].FieldType.FullName != type)
            { offenders.Add("Missing or retyped job retained resource: " + subject.FullName + ":" + name); return; }
            var access = fields.Where(site => SameType(site.Field.DeclaringType, subject) && site.Field.Name == name).ToArray();
            var expected = expectedOwners.Distinct(StringComparer.Ordinal).Select(methodName =>
            {
                var methods = subject.Methods.Where(method => method.Name == methodName).ToArray();
                if (methods.Length == 1) return MethodId(methods[0]);
                offenders.Add("Missing or overloaded retained-resource owner: " + subject.FullName + "::" + methodName);
                return "missing:" + methodName;
            });
            CheckSet(offenders, "closed job retained-resource callers " + subject.FullName + ":" + name,
                access.Select(site => MethodId(site.Owner)).Distinct(StringComparer.Ordinal), expected);
            if (access.Count(site => site.Instruction.OpCode == OpCodes.Stfld) != writes
                || access.Any(site => site.Instruction.OpCode != OpCodes.Ldfld && site.Instruction.OpCode != OpCodes.Stfld
                    && !(name == "_released" && site.Instruction.OpCode == OpCodes.Ldflda)))
                offenders.Add("Unexpected job retained-resource publication or address escape: " + subject.FullName + ":" + name);
        }

        void RequireConstant(TypeDefinition subject, string method, int value)
        {
            if (bodies.Where(item => item.Owner.DeclaringType == subject && item.Owner.Name == method)
                    .SelectMany(item => item.Body.Body.Instructions).Count(instruction => JobApplicationConstant(instruction) == value) != 1)
                offenders.Add("Missing or changed finite job helper bound/lane: " + subject.FullName + "::" + method);
        }

        void RequireCall(string method, string type, string name)
        {
            if (bodies.Where(item => item.Owner.DeclaringType == resources && item.Owner.Name == method)
                    .SelectMany(item => MethodOperands(item.Body)).Count(target => target.DeclaringType.FullName == type && target.Name == name) != 1)
                offenders.Add("Missing actual retained resource cleanup/completion call: " + method + "::" + type + "::" + name);
        }
    }

    private static void AssertJobApplicationRetainedFieldMutations(ProductionCensus census, TypeDefinition owner,
        TypeDefinition resources, ModuleDefinition application)
    {
        var bodies = OwnedBodies(census).ToArray();
        foreach (var field in owner.Fields.Where(field => field.Name is "_authorization" or "_evidence")
                     .Concat(resources.Fields.Where(field => field.Name is "_connection" or "_transaction" or "_commands"
                         or "_reader" or "_actualTask" or "_cleanupConfirmed" or "_readerCleanupFailed")))
        {
            AssertMutation(census, "a public retained job resource " + field.Name, () =>
                ChangeAttributes(() => field.Attributes, value => field.Attributes = value,
                    (field.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public), JobApplicationSourceOffenders);
            AssertMutation(census, "a private forwarding accessor for retained job resource " + field.Name, () =>
            {
                var undo = AddBoundaryFieldGetter(field.DeclaringType, "ForwardJobApplicationResource", field.Name);
                field.DeclaringType.Methods.Single(method => method.Name == "ForwardJobApplicationResource").IsPrivate = true;
                return undo;
            }, JobApplicationSourceOffenders);
        }
        var laneSites = bodies.Where(item => item.Owner.DeclaringType == owner).SelectMany(item => item.Body.Body.Instructions)
            .Where(instruction => instruction.Operand is FieldReference field && SameType(field.DeclaringType, owner)
                && field.Name is "_authorization" or "_evidence").ToArray();
        laneSites.Should().NotBeEmpty("both approved lanes have actual readiness, issuance and cleanup ownership");
        foreach (var site in laneSites)
            AssertMutation(census, "a deleted actual authorization/evidence job lane gate or retained join", () =>
                ReplaceInstruction(site, OpCodes.Nop, null!), JobApplicationSourceOffenders);
        var reconciliationSites = bodies.Where(item => item.Owner.DeclaringType == owner
                && item.Owner.Name is "get_CleanupConfirmed" or "RequireBusinessReady" or "BeginAuxiliary" or "BeginConfinedLane")
            .SelectMany(item => item.Body.Body.Instructions).Where(instruction => instruction.Operand is MethodReference target
                && SameType(target.DeclaringType, resources) && target.Name == "TryRelease").ToArray();
        reconciliationSites.Should().HaveCount(12, "four readiness/acquisition gates each reconcile the three retained lanes");
        foreach (var site in reconciliationSites)
        {
            AssertMutation(census, "a deleted actual completed-lane reconciliation", () =>
                ReplaceInstruction(site, OpCodes.Nop, null!), JobApplicationSourceOffenders);
            AssertMutation(census, "a delegated completed-lane reconciliation", () =>
                ReplaceInstruction(site, OpCodes.Ldftn, site.Operand), JobApplicationSourceOffenders);
        }
        foreach (var method in resources.Methods.Where(method => method.Name is ".ctor" or "HoldCommand"))
        {
            var bound = method.Body.Instructions.Single(instruction => JobApplicationConstant(instruction) == 3);
            AssertMutation(census, "an enlarged existing business-helper command bound", () =>
                ReplaceInstruction(bound, OpCodes.Ldc_I4, 4), JobApplicationSourceOffenders);
        }
        foreach (var method in owner.Methods.Where(method => method.Name is "BeginAuthorizationRead" or "BeginAuthorizationEvidence"))
        {
            var lane = method.Body.Instructions.Single(instruction => JobApplicationConstant(instruction) is 0 or 1);
            AssertMutation(census, "a swapped named job auxiliary lane", () =>
                ReplaceInstruction(lane, OpCodes.Ldc_I4, method.Name == "BeginAuthorizationRead" ? 0 : 1), JobApplicationSourceOffenders);
        }
        var cleanupSites = bodies.Where(item => item.Owner.DeclaringType == resources
                && item.Owner.Name is "CleanupAsync" or "DisposeReaderAsync" or "TryRelease")
            .SelectMany(item => item.Body.Body.Instructions).Where(instruction => instruction.Operand is MethodReference target
                && (target.Name == "DisposeAsync" && target.DeclaringType.FullName.StartsWith("System.Data.Common.Db", StringComparison.Ordinal)
                    || target.Name == "get_IsCompleted" && target.DeclaringType.FullName == "System.Threading.Tasks.Task")).ToArray();
        cleanupSites.Should().HaveCount(5, "reader, commands, transaction, connection and actual task completion remain concrete");
        foreach (var site in cleanupSites)
            AssertMutation(census, "a deleted actual job helper disposal or task-completion witness", () =>
                ReplaceInstruction(site, OpCodes.Nop, null!), JobApplicationSourceOffenders);
        AssertMutation(census, "an extra retained job resource slot", () =>
        {
            var extra = new FieldDefinition("_additionalAuthorization", FieldAttributes.Private, application.ImportReference(resources));
            owner.Fields.Add(extra);
            return () => owner.Fields.Remove(extra);
        }, JobApplicationSourceOffenders);
    }

    private static int? JobApplicationConstant(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Ldc_I4_0 => 0,
        Code.Ldc_I4_1 => 1,
        Code.Ldc_I4_3 => 3,
        Code.Ldc_I4 => (int)instruction.Operand,
        Code.Ldc_I4_S => Convert.ToInt32(instruction.Operand, System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };
}
