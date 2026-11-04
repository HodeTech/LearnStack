namespace LearnStack.SharedKernel.Idempotency;

/// <summary>The closed original-target kinds admitted by ADR-0079's protected receipts.</summary>
public enum IdempotencyReplayTargetKind
{
    Membership,
    Role,
    Organization,
    Tenant,
}

/// <summary>Internal immutable result metadata; it grants no disclosure authority.</summary>
public sealed record IdempotencyReplayTarget(IdempotencyReplayTargetKind Kind, Guid Id)
{
    public override string ToString() => nameof(IdempotencyReplayTarget);
}
