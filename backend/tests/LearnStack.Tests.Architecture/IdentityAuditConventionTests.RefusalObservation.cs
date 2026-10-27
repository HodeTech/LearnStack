using Mono.Cecil;
using Mono.Cecil.Cil;
using EvidenceCensus = LearnStack.Tests.Architecture.TrustedOperationSourceConventionTests.Census;

namespace LearnStack.Tests.Architecture;

public sealed partial class IdentityAuditConventionTests
{
    private const string RefusalObservation = CentralEvidence + "/RefusalObservation";
    private const string EvidenceScope = CentralEvidence + "/ExecutionScope";
    private const string ObservedContexts = "System.Collections.Generic.Dictionary`2<" + EvidenceInvocation + "," + AuditContext + ">";

    private static void EvidenceObservationOffenders(EvidenceCensus census, List<string> errors)
    {
        var observation = census.Type(RefusalObservation);
        if (!observation.IsNestedAssembly || !observation.IsSealed
            || observation.Methods.Count(method => method.IsConstructor) != 1
            // A primary constructor is Public in IL; the enclosing internal nested
            // type and exact constructor callers keep its effective scope closed.
            || !observation.Methods.Single(method => method.IsConstructor).IsPublic
            || observation.Properties.Any(property => property.SetMethod is not null)
            || !EvidenceSame(observation.Fields.Select(field => field.FieldType.FullName),
                [CentralEvidence, EvidenceScope, PolicyOwner, EvidenceInvocation, "System.Type", "System.Object", "System.Int32"])
            || observation.Fields.Any(field => !field.IsPrivate || field.IsStatic)
            || census.Methods.SelectMany(method => method.Body.Instructions.Select(instruction => (Method: method, Instruction: instruction)))
                .Any(item => item.Instruction.Operand is FieldReference field && field.DeclaringType.FullName == RefusalObservation
                    && (item.Instruction.OpCode == OpCodes.Stfld && census.Owner(item.Method) != RefusalObservation + "::.ctor"
                        || item.Instruction.OpCode == OpCodes.Ldflda && (field.Name != "_consumed" || census.Owner(item.Method) != RefusalObservation + "::TryConsume"))))
            errors.Add("The internal observation has only constructor-bound private audit references and its one-use counter");
        foreach (var name in new[] { "CaptureRefusalObservation", "RecordRefusalResult" })
        {
            if (!census.Method(CentralEvidence, name).IsAssembly) errors.Add("Audit observation is internal to the common execution owner: " + name);
            EvidenceCallers(census, errors, CentralEvidence, name, [(PolicyOwner, "ExecuteAsync", 1), (EvidenceTransactionOwner, "ExecuteFrameAsync", 1)]);
        }
        EvidenceCallers(census, errors, RefusalObservation, ".ctor", [(CentralEvidence, "CaptureRefusalObservation", 1)]);
        EvidenceCallers(census, errors, RefusalObservation, "TryConsume", [(CentralEvidence, "RecordRefusalResult", 1)]);
        EvidenceCallers(census, errors, RefusalObservation, "get_Invocation", [(RefusalObservation, "TryConsume", 1), (CentralEvidence, "RecordRefusalResult", 2)]);
        EvidenceCallers(census, errors, EvidenceScope, "IsOpen", [(CentralEvidence, "CaptureRefusalObservation", 1), (RefusalObservation, "TryConsume", 1)]);
        EvidenceCallers(census, errors, PolicyOwner, "HasOpenObservationFrame", [(RefusalObservation, "TryConsume", 1)]);
        EvidenceCallers(census, errors, PolicyOwner, "get_CurrentInvocation", [(CentralEvidence, "CaptureRefusalObservation", 1)]);
        EvidenceCallers(census, errors, CentralEvidence, "RecordObservedDenial", [(CentralEvidence, "RecordDenial", 1), (CentralEvidence, "RecordRefusalResult", 1)]);
        EvidenceCallers(census, errors, CentralEvidence, "EndExecutionAsync", [(EvidenceScope, "DisposeAsync", 1)]);
        var observationFrames = new[] { census.Body(PolicyOwner, "ExecuteAsync").DeclaringType.FullName,
            census.Body(EvidenceTransactionOwner, "ExecuteFrameAsync").DeclaringType.FullName };
        if (!EvidenceSame(census.Types.SelectMany(type => type.Fields.Where(field => field.FieldType.FullName == RefusalObservation)
                .Select(_ => type.FullName)), observationFrames))
            errors.Add("Only the two actual common handler awaits retain an opaque observation");
        foreach (var (type, method, target, name) in ObservationRequiredCalls)
            EvidenceRequire(census, errors, type, method, target, name);
        if (ObservationCaptureGates(census.Body(CentralEvidence, "CaptureRefusalObservation")).Length != 8
            || ObservationConsumeGates(census.Body(RefusalObservation, "TryConsume")).Length != 5
            || ObservationFrameGates(census.Body(PolicyOwner, "HasOpenObservationFrame")).Length != 5
            || ObservationScopeGates(census).Length != 2)
            errors.Add("Audit observation keeps exact scope, request/type, current top-frame, closed ancestry and replay gates");
        foreach (var (type, method, transactional) in new[] { (PolicyOwner, "ExecuteAsync", false), (EvidenceTransactionOwner, "ExecuteFrameAsync", true) })
            if (ObservationFlow(census.Body(type, method), transactional) is null)
                errors.Add("An actual scope captures before its handler await and consumes that exact observation/request/result before completion: " + type);
        var record = census.Body(CentralEvidence, "RecordRefusalResult");
        var strings = record.Body.Instructions.Select(instruction => instruction.Operand).OfType<string>()
            .Where(value => value is "forbidden" or "not_found" or "resource_scope_violation" or "feature_disabled" or "limit_exceeded").Take(5);
        if (!strings.SequenceEqual(["forbidden", "not_found", "resource_scope_violation", "feature_disabled", "limit_exceeded"], StringComparer.Ordinal)
            || !ObservationLatestContext(record)) errors.Add("Only a closed actual refusal consumes the invocation's latest verified frozen context");
    }

    private static readonly (string Type, string Method, string Target, string Name)[] ObservationRequiredCalls =
    [
        (CentralEvidence, "CaptureRefusalObservation", EvidenceScope, "IsOpen"),
        (CentralEvidence, "CaptureRefusalObservation", CentralEvidence, "HasAttribution"),
        (CentralEvidence, "CaptureRefusalObservation", ObservedContexts, "ContainsKey"),
        (CentralEvidence, "CaptureRefusalObservation", EvidencePolicy, "get_RequestType"),
        (CentralEvidence, "CaptureRefusalObservation", "System.Object", "GetType"),
        (CentralEvidence, "RecordRefusalResult", RefusalObservation, "TryConsume"),
        (CentralEvidence, "RecordRefusalResult", "LearnStack.SharedKernel.Results.IResultBase", "get_IsFailure"),
        (CentralEvidence, "RecordRefusalResult", ObservedContexts, "TryGetValue"),
        (CentralEvidence, "RecordDenial", CentralEvidence, "HasAttribution"),
        (RefusalObservation, "TryConsume", "System.Threading.Interlocked", "CompareExchange"),
        (RefusalObservation, "TryConsume", "System.Type", "op_Equality"),
        (EvidenceScope, "IsOpen", "System.Threading.Volatile", "Read"),
        (EvidenceScope, "DisposeAsync", "System.Threading.Interlocked", "Exchange"),
        (PolicyOwner, "HasOpenObservationFrame", "System.Threading.Volatile", "Read"),
        (PolicyOwner, "HasOpenObservationFrame", PolicyOwner + "/Frame", "get_Closed"),
        (PolicyOwner, "HasOpenObservationFrame", EvidenceInvocation + "/MintingSource", "IsCurrent"),
    ];

    private static void EvidenceObservationDirtyControls(EvidenceCensus census)
    {
        foreach (var (type, method) in new[] { (RefusalObservation, ".ctor"), (RefusalObservation, "TryConsume"), (PolicyOwner, "HasOpenObservationFrame") })
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn })
                EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceExtraCall(census, census.Method(type, method), opcode));
        foreach (var (type, method, target, name) in ObservationRequiredCalls)
            EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceRemoveCall(census.Body(type, method), target, name));
        foreach (var branch in ObservationCaptureGates(census.Body(CentralEvidence, "CaptureRefusalObservation"))
            .Concat(ObservationConsumeGates(census.Body(RefusalObservation, "TryConsume")))
            .Concat(ObservationFrameGates(census.Body(PolicyOwner, "HasOpenObservationFrame")))
            .Concat(ObservationScopeGates(census)))
            EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceReplace(branch, OpCodes.Nop, null));
        foreach (var (type, method, transactional) in new[] { (PolicyOwner, "ExecuteAsync", false), (EvidenceTransactionOwner, "ExecuteFrameAsync", true) })
        {
            var body = census.Body(type, method);
            var flow = ObservationFlow(body, transactional)!.Value;
            EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceReplace(flow.Observation, OpCodes.Ldnull, null));
            EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceReplace(flow.Operation, OpCodes.Ldnull, null));
            EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceReplace(flow.Await, OpCodes.Nop, null));
        }
        var record = census.Body(CentralEvidence, "RecordRefusalResult");
        var code = record.Body.Instructions.First(instruction => instruction.Operand as string == "forbidden");
        EvidenceDirty(census, EvidenceProvenanceOffenders, () => EvidenceReplace(code, OpCodes.Ldstr, "unrelated_failure"));
        EvidenceDirty(census, EvidenceProvenanceOffenders, () =>
        {
            var type = census.Type(RefusalObservation);
            var attributes = type.Attributes;
            type.IsNestedPublic = true;
            return () => type.Attributes = attributes;
        });
        EvidenceDirty(census, EvidenceProvenanceOffenders, () =>
        {
            var owner = census.Type(EvidenceSource);
            var extra = new FieldDefinition("UnownedObservation", FieldAttributes.Private, owner.Module.ImportReference(census.Type(RefusalObservation)));
            owner.Fields.Add(extra);
            return () => owner.Fields.Remove(extra);
        });
    }

    private static Instruction[] ObservationCaptureGates(MethodDefinition body)
    {
        var gates = new List<Instruction>();
        var current = EvidenceCalls(body, PolicyOwner, "get_CurrentInvocation").SingleOrDefault();
        if (current?.Next is not { } retain || EvidenceStoredLocal(retain) is not { } invocation
            || !body.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Isinst && instruction.Operand is TypeReference type && type.FullName == PolicyOwner)) return [];
        foreach (var (type, name, branchOffset) in new[] { (EvidenceScope, "IsOpen", 1), (EvidenceInvocation, "get_EntryKind", 2),
            (CentralEvidence, "HasAttribution", 1), (ObservedContexts, "ContainsKey", 1) })
        {
            var call = EvidenceCalls(body, type, name).SingleOrDefault();
            var branch = call;
            for (var step = 0; step < branchOffset; step++) branch = branch?.Next;
            if (branch?.Operand is not Instruction target || target.OpCode != (name == "ContainsKey" ? OpCodes.Ldarg_0 : OpCodes.Ldnull)) return [];
            gates.Add(branch);
        }
        foreach (var comparison in EvidenceCalls(body, "System.Type", "op_Inequality"))
        {
            if (comparison.Previous?.OpCode != OpCodes.Ldarg_2 || comparison.Next is not { OpCode.Code: Code.Brtrue or Code.Brtrue_S, Operand: Instruction rejected }
                || rejected.OpCode != OpCodes.Ldnull) return [];
            gates.Add(comparison.Next);
        }
        var operation = EvidenceCalls(body, EvidenceInvocation, "get_Operation").SingleOrDefault();
        if (operation?.Previous is not { } owner || EvidenceLocal(owner) != invocation || operation.Next?.OpCode != OpCodes.Ldarg_3
            || operation.Next.Next is not { OpCode.Code: Code.Bne_Un or Code.Bne_Un_S, Operand: Instruction refused } reference || refused.OpCode != OpCodes.Ldnull) return [];
        gates.Add(reference);
        var policy = EvidenceCalls(body, EvidencePolicy, "get_RequestType").SingleOrDefault();
        var actual = EvidenceCalls(body, "System.Object", "GetType").SingleOrDefault();
        if (policy?.Previous is not { } policyOwner || !EvidenceIsCall(policyOwner, EvidenceInvocation, "get_Policy")
            || policyOwner.Previous is not { } policyInvocation || EvidenceLocal(policyInvocation) != invocation
            || actual?.Previous?.OpCode != OpCodes.Ldarg_3) return [];
        if (retain.Next?.Next is not { OpCode.Code: Code.Brfalse or Code.Brfalse_S, Operand: Instruction absent } present || absent.OpCode != OpCodes.Ldnull) return [];
        gates.Add(present);
        return [.. gates];
    }

    private static Instruction[] ObservationConsumeGates(MethodDefinition body)
    {
        var branches = body.Body.Instructions.Where(instruction => instruction.OpCode.Code is Code.Bne_Un or Code.Bne_Un_S or Code.Brfalse or Code.Brfalse_S).ToArray();
        if (branches.Length != 5 || branches.Any(branch => branch.Operand is not Instruction { OpCode.Code: Code.Ldc_I4_0 })
            || !branches.Take(1).All(branch => branch.Previous?.OpCode == OpCodes.Ldarg_1
                && branch.Previous.Previous?.Operand is FieldReference field && field.FieldType.FullName == CentralEvidence)
            || branches[3].Previous?.OpCode != OpCodes.Ldarg_3
            || branches[3].Previous.Previous?.Operand is not FieldReference operation || operation.FieldType.FullName != "System.Object"
            || branches[1].Previous is not { } scope || !EvidenceIsCall(scope, EvidenceScope, "IsOpen")
            || branches[2].Previous is not { } type || !EvidenceIsCall(type, "System.Type", "op_Equality")
            || branches[4].Previous is not { } frame || !EvidenceIsCall(frame, PolicyOwner, "HasOpenObservationFrame")) return [];
        var replay = EvidenceCalls(body, "System.Threading.Interlocked", "CompareExchange").SingleOrDefault();
        if (replay?.Previous?.OpCode != OpCodes.Ldc_I4_0 || replay.Previous.Previous?.OpCode != OpCodes.Ldc_I4_1
            || replay.Previous.Previous.Previous is not { OpCode.Code: Code.Ldflda, Operand: FieldReference consumed }
            || consumed.DeclaringType.FullName != RefusalObservation || consumed.Name != "_consumed"
            || replay.Next?.OpCode != OpCodes.Ldc_I4_0 || replay.Next.Next?.OpCode != OpCodes.Ceq) return [];
        return branches;
    }

    private static Instruction[] ObservationFrameGates(MethodDefinition body)
    {
        var branches = body.Body.Instructions.Where(instruction => instruction.OpCode.Code is Code.Brtrue or Code.Brtrue_S or Code.Brfalse or Code.Brfalse_S or Code.Bne_Un or Code.Bne_Un_S).ToArray();
        if (branches.Length != 5 || branches.Any(branch => branch.Operand is not Instruction { OpCode.Code: Code.Ldc_I4_0 })
            || branches[3].Previous?.OpCode != OpCodes.Ldarg_1 || branches[4].Previous?.OpCode != OpCodes.Ldarg_2
            || branches[3].Previous.Previous is not { } invocation || !EvidenceIsCall(invocation, PolicyOwner + "/Frame", "get_Invocation")
            || branches[4].Previous.Previous is not { } operation || !EvidenceIsCall(operation, EvidenceInvocation, "get_Operation")) return [];
        var closed = EvidenceCalls(body, "System.Threading.Volatile", "Read").SingleOrDefault();
        var top = body.Body.Instructions.SingleOrDefault(instruction => instruction.Operand is MethodReference call
            && call.DeclaringType.FullName == "System.Threading.AsyncLocal`1<" + PolicyOwner + "/Frame>" && call.Name == "get_Value");
        if (closed?.Previous is not { OpCode.Code: Code.Ldflda, Operand: FieldReference state } || state.Name != "_closed" || state.DeclaringType.FullName != PolicyOwner
            || top?.Previous is not { OpCode.Code: Code.Ldfld, Operand: FieldReference current } || current.Name != "_current" || current.DeclaringType.FullName != PolicyOwner
            || top.Next is not { } store || EvidenceStoredLocal(store) is not { } frame
            || invocation.Previous is not { } candidate || EvidenceLocal(candidate) != frame
            || operation.Previous?.OpCode != OpCodes.Ldarg_1) return [];
        return branches;
    }

    private static Instruction[] ObservationScopeGates(EvidenceCensus census)
    {
        var open = census.Body(EvidenceScope, "IsOpen");
        var dispose = census.Body(EvidenceScope, "DisposeAsync");
        var read = EvidenceCalls(open, "System.Threading.Volatile", "Read").SingleOrDefault();
        var close = EvidenceCalls(dispose, "System.Threading.Interlocked", "Exchange").SingleOrDefault();
        if (read?.Previous is not { OpCode.Code: Code.Ldflda, Operand: FieldReference closed } || closed.DeclaringType.FullName != EvidenceScope || closed.Name != "_closed"
            || read.Next?.OpCode != OpCodes.Ldc_I4_0 || read.Next.Next?.OpCode != OpCodes.Ceq
            || read.Previous.Previous.Previous is not { OpCode.Code: Code.Bne_Un or Code.Bne_Un_S } owner
            || owner.Previous?.OpCode != OpCodes.Ldarg_1 || owner.Previous.Previous?.Operand is not FieldReference actualOwner
            || actualOwner.DeclaringType.FullName != EvidenceScope || actualOwner.Name != "_owner"
            || close?.Previous?.OpCode != OpCodes.Ldc_I4_1 || close.Previous.Previous?.Operand is not FieldReference sameClosed || sameClosed.FullName != closed.FullName
            || close.Next is not { OpCode.Code: Code.Brfalse or Code.Brfalse_S, Operand: Instruction end } closedOnce
            || !EvidenceCalls(dispose, CentralEvidence, "EndExecutionAsync").Any(call => dispose.Body.Instructions.IndexOf(call) > dispose.Body.Instructions.IndexOf(end))) return [];
        return [owner, closedOnce];
    }

    private static bool ObservationLatestContext(MethodDefinition body)
    {
        var lookup = EvidenceCalls(body, ObservedContexts, "TryGetValue").SingleOrDefault();
        if (lookup?.Previous is not { OpCode.Code: Code.Ldloca or Code.Ldloca_S, Operand: VariableDefinition context }
            || lookup.Previous.Previous is not { } key || !EvidenceIsCall(key, RefusalObservation, "get_Invocation")
            || key.Previous?.OpCode != OpCodes.Ldarg_1 || key.Previous.Previous is not { OpCode.Code: Code.Ldfld, Operand: FieldReference contexts }
            || contexts.Name != "_contexts" || contexts.DeclaringType.FullName != CentralEvidence
            || lookup.Next is not { OpCode.Code: Code.Brtrue or Code.Brtrue_S } || lookup.Next.Next?.OpCode != OpCodes.Ret) return false;
        var record = EvidenceCalls(body, CentralEvidence, "RecordObservedDenial").SingleOrDefault();
        return record is not null && body.Body.Instructions.SkipWhile(instruction => instruction != lookup).TakeWhile(instruction => instruction != record)
            .Any(instruction => EvidenceIsCall(instruction, RefusalObservation, "get_Invocation") && instruction.Next is { } loaded && EvidenceLocal(loaded) == context.Index);
    }

    private static (Instruction Observation, Instruction Operation, Instruction Await)? ObservationFlow(MethodDefinition body, bool transactional)
    {
        var capture = EvidenceCalls(body, CentralEvidence, "CaptureRefusalObservation").SingleOrDefault();
        var record = EvidenceCalls(body, CentralEvidence, "RecordRefusalResult").SingleOrDefault();
        if (capture?.Previous is not { OpCode.Code: Code.Ldfld, Operand: FieldReference operation } captureOperation
            || operation.Name != "operation" || capture.Next is not { OpCode.Code: Code.Stfld, Operand: FieldReference retained }
            || retained.FieldType.FullName != RefusalObservation || record?.Previous?.OpCode != OpCodes.Box
            || record.Previous.Previous is not { OpCode.Code: Code.Ldfld, Operand: FieldReference response }
            || record.Previous.Previous.Previous.Previous is not { OpCode.Code: Code.Ldfld, Operand: FieldReference consumedOperation } operationLoad
            || consumedOperation.FullName != operation.FullName || !ObservationSameTypeInput(captureOperation.Previous.Previous, operationLoad.Previous.Previous)) return null;
        var scopes = body.Body.Instructions.TakeWhile(instruction => instruction != capture).Reverse().Take(15)
            .Where(instruction => instruction.OpCode == OpCodes.Ldfld && instruction.Operand is FieldReference field && field.FieldType.FullName == EvidenceScope).ToArray();
        var begin = EvidenceCalls(body, CentralEvidence, "BeginExecution").SingleOrDefault();
        if (scopes.Length != 1 || scopes[0].Operand is not FieldReference scope
            || begin?.Next?.Operand is not FieldReference startedScope || begin.Next.OpCode != OpCodes.Stfld || startedScope.FullName != scope.FullName) return null;
        var consumed = body.Body.Instructions.TakeWhile(instruction => instruction != record).Reverse().Take(20)
            .SingleOrDefault(instruction => instruction.OpCode == OpCodes.Ldfld && instruction.Operand is FieldReference field && field.FullName == retained.FullName);
        var next = body.Body.Instructions.SkipWhile(instruction => instruction != capture).TakeWhile(instruction => instruction != record)
            .SingleOrDefault(instruction => instruction.Operand is MethodReference method && method.Name == "Invoke"
                && instruction.Previous?.Operand is FieldReference field && field.Name == "next");
        var awaited = next is null ? null : body.Body.Instructions.SkipWhile(instruction => instruction != next).TakeWhile(instruction => instruction != record).FirstOrDefault(EvidenceAwaitAck);
        if (consumed is null || awaited?.Next is not { } resultStore || EvidenceStoredLocal(resultStore) is not { } result
            || !body.Body.Instructions.SkipWhile(instruction => instruction != resultStore).TakeWhile(instruction => instruction != record).Any(instruction =>
                instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field && field.FullName == response.FullName
                && instruction.Previous is { } loaded && EvidenceLocal(loaded) == result)) return null;
        if (transactional)
        {
            var failure = EvidenceCalls(body, "LearnStack.SharedKernel.Results.IResultBase", "get_IsFailure").SingleOrDefault(call =>
                call.Previous?.OpCode == OpCodes.Constrained && call.Previous.Previous?.Operand is FieldReference field && field.FullName == response.FullName);
            var rollback = EvidenceCalls(body, "LearnStack.SharedKernel.Persistence.IUnitOfWorkScope", "FailAsync").FirstOrDefault(call =>
                body.Body.Instructions.IndexOf(call) > body.Body.Instructions.IndexOf(record));
            if (failure?.Next is not { OpCode.Code: Code.Brfalse or Code.Brfalse_S, Operand: Instruction success }
                || rollback is null || body.Body.Instructions.IndexOf(failure) >= body.Body.Instructions.IndexOf(record)
                || body.Body.Instructions.IndexOf(rollback) >= body.Body.Instructions.IndexOf(success)) return null;
        }
        else if (EvidenceCalls(body, CentralEvidence, "BeforeSuccessAsync").SingleOrDefault() is not { } completion
            || body.Body.Instructions.IndexOf(record) >= body.Body.Instructions.IndexOf(completion)) return null;
        return (consumed, operationLoad, awaited);
    }

    private static bool ObservationSameTypeInput(Instruction first, Instruction second) =>
        first.OpCode == OpCodes.Ldfld && second.OpCode == OpCodes.Ldfld
            && first.Operand is FieldReference left && second.Operand is FieldReference right && left.FullName == right.FullName && left.FieldType.FullName == "System.Type"
        || EvidenceIsCall(first, EvidencePolicy, "get_RequestType") && EvidenceIsCall(second, EvidencePolicy, "get_RequestType")
            && first.Previous?.Operand is FieldReference leftPolicy && second.Previous?.Operand is FieldReference rightPolicy
            && leftPolicy.FullName == rightPolicy.FullName && leftPolicy.FieldType.FullName == EvidencePolicy;
}
