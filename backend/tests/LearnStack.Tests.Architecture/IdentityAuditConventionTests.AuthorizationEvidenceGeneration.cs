using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class IdentityAuditConventionTests
{
    private const string EvidenceUnit = "LearnStack.SharedKernel.Persistence.IUnitOfWork";
    private const string EvidenceBatch = CentralEvidence + "/CommitBatch";
    private const string NullableGeneration = "System.Nullable`1<System.Int64>";

    private static IEnumerable<Instruction> EvidencePrematureAcknowledgementGuards(MethodDefinition body)
    {
        foreach (var active in EvidenceCalls(body, EvidenceUnit, "get_HasActiveTransaction"))
        {
            if (active.Previous is not { OpCode.Code: Code.Ldfld, Operand: FieldReference unit }
                || unit.DeclaringType.FullName != CentralEvidence || unit.FieldType.FullName != EvidenceUnit
                || active.Previous.Previous?.OpCode != OpCodes.Ldarg_0
                || active.Previous.Previous.Previous is not { OpCode.Code: Code.Brfalse or Code.Brfalse_S, Operand: Instruction accepted } committed
                || committed.Previous?.OpCode != OpCodes.Ldarg_2
                || active.Next is not { OpCode.Code: Code.Brfalse or Code.Brfalse_S } closed
                || closed.Operand != accepted
                || closed.Next?.Operand as string != "A live transaction cannot acknowledge authorization evidence."
                || closed.Next.Next?.OpCode != OpCodes.Newobj || closed.Next.Next.Next?.OpCode != OpCodes.Throw
                || closed.Next.Next.Next.Next != accepted || accepted.OpCode != OpCodes.Ldarg_1
                || accepted.Next?.OpCode != OpCodes.Ldc_I4_1 || accepted.Next.Next is not { } finish
                || !EvidenceIsCall(finish, EvidenceBatch, "set_Finished")) continue;
            yield return closed;
        }
    }

    private static IEnumerable<(Instruction TransactionMismatch, Instruction GenerationEqual)> EvidencePendingGenerationGuards(MethodDefinition body)
    {
        var writes = EvidenceCalls(body, "LearnStack.SharedKernel.Audit.IAuditStore", "WriteAuthorizationPendingAsync").ToArray();
        if (writes.Length != 1) yield break;
        foreach (var transaction in EvidenceCalls(body, EvidenceBatch, "get_Transaction"))
        {
            if (transaction.Next is not { OpCode.Code: Code.Bne_Un or Code.Bne_Un_S, Operand: Instruction failure } mismatch
                || transaction.Previous is not { OpCode.Code: Code.Ldfld, Operand: FieldReference batch }
                || batch.FieldType.FullName != EvidenceBatch || transaction.Previous.Previous?.OpCode != OpCodes.Ldarg_0
                || transaction.Previous.Previous.Previous is not { } currentTransaction
                || !EvidenceIsCall(currentTransaction, EvidenceUnit, "get_Transaction")
                || currentTransaction.Previous is not { OpCode.Code: Code.Ldfld, Operand: FieldReference owner }
                || owner.FieldType.FullName != EvidenceUnit || currentTransaction.Previous.Previous?.OpCode != OpCodes.Ldarg_0
                || !EvidenceAwaited(body, writes[0], currentTransaction)) continue;
            var announcement = EvidenceCalls(body, EvidenceUnit, "GetTenantContextAnnouncement").SingleOrDefault(call =>
                body.Body.Instructions.IndexOf(call) > body.Body.Instructions.IndexOf(mismatch));
            if (announcement is null || announcement.Previous is not { } capturedTransaction
                || !EvidenceIsCall(capturedTransaction, EvidenceBatch, "get_Transaction")
                || capturedTransaction.Previous?.Operand is not FieldReference sameBatch || sameBatch.FullName != batch.FullName
                || capturedTransaction.Previous.Previous?.OpCode != OpCodes.Ldarg_0
                || capturedTransaction.Previous.Previous.Previous?.Operand is not FieldReference sameOwner || sameOwner.FullName != owner.FullName
                || capturedTransaction.Previous.Previous.Previous.Previous?.OpCode != OpCodes.Ldarg_0) continue;
            var generation = EvidenceCalls(body, EvidenceBatch, "get_Generation").SingleOrDefault();
            if (generation is null || generation.Previous?.Operand is not FieldReference generationBatch || generationBatch.FullName != batch.FullName
                || generation.Next is not { } generationStore || EvidenceStoredLocal(generationStore) is not { } generationIndex) continue;
            var snapshot = EvidenceCalls(body, "LearnStack.SharedKernel.Persistence.TenantContextAnnouncement", "get_Generation")
                .SingleOrDefault(call => body.Body.Instructions.IndexOf(call) > body.Body.Instructions.IndexOf(announcement));
            if (snapshot is null || snapshot.Next?.OpCode != OpCodes.Newobj || snapshot.Next.Next is not { } nullableStore
                || EvidenceStoredLocal(nullableStore) is not { } nullableIndex) continue;
            var equality = body.Body.Instructions.FirstOrDefault(instruction => instruction.OpCode == OpCodes.Ceq
                && instruction.Previous is { } loaded && EvidenceLocal(loaded) == generationIndex
                && loaded.Previous is { } unwrap && EvidenceIsCall(unwrap, NullableGeneration, "GetValueOrDefault")
                && unwrap.Previous is { } nullable && EvidenceLocal(nullable) == nullableIndex);
            if (equality?.Next is not { } address || EvidenceLocal(address) != nullableIndex
                || address.Next is not { } hasValue || !EvidenceIsCall(hasValue, NullableGeneration, "get_HasValue")
                || hasValue.Next?.OpCode != OpCodes.And
                || hasValue.Next.Next is not { OpCode.Code: Code.Brtrue or Code.Brtrue_S, Operand: Instruction accepted }
                || hasValue.Next.Next.Next != failure || failure.OpCode != OpCodes.Ldarg_0
                || failure.Next?.Operand is not FieldReference failedOwner || failedOwner.FullName != owner.FullName
                || failure.Next.Next is not { } poison || !EvidenceIsCall(poison, EvidenceUnit, "MarkRollbackOnly")
                || poison.Next?.Operand as string != "Authorization evidence lost its original physical owner generation."
                || poison.Next.Next?.OpCode != OpCodes.Ldnull || poison.Next.Next.Next?.OpCode != OpCodes.Newobj
                || poison.Next.Next.Next.Next?.OpCode != OpCodes.Throw || poison.Next.Next.Next.Next.Next != accepted) continue;
            yield return (mismatch, equality);
        }
    }
}
