namespace LearnStack.SharedKernel.Idempotency;

/// <summary>A fixed storage refusal. It proves neither business rollback nor whether a COMMIT reached PostgreSQL.</summary>
public sealed class IdempotencyStoreException() : Exception("Idempotency storage is unavailable.");
