using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Infrastructure.Idempotency;

public sealed partial class PostgresIdempotencyStore : ICustomFieldIdempotencyMetadataReader
{
    public Task<CustomFieldIdempotencyKeyMetadata> ReadKeyMetadataAsync(TenantId tenantId, string key,
        CancellationToken cancellationToken)
    {
        Guard(tenantId, key);
        return OwnAsync(tenantId, async (connection, transaction, token) =>
        {
            // Physical expired rows still bind read-key custody. The query returns
            // no body/receipt, never claims a key and takes no capacity fence.
            var id = await ScalarAsync<string?>(connection, transaction, """
                SELECT CASE
                    WHEN fingerprint COLLATE "C" ~ '^cfh1:[a-z0-9-]{1,32}:[0-9a-f]{64}$'
                        THEN split_part(fingerprint, ':', 2)
                    WHEN fingerprint COLLATE "C" ~ '^[0-9a-f]{64}$' THEN ''
                    ELSE '?' END
                FROM public.idempotency_keys WHERE key=@key
                """, token, ("key", key));
            if (id is null) return new CustomFieldIdempotencyKeyMetadata(false, null);
            if (id.Length == 0) return new CustomFieldIdempotencyKeyMetadata(true, null);
            if (CustomFieldIdempotencyFingerprint.ValidKeyId(id))
                return new CustomFieldIdempotencyKeyMetadata(true, id);
            throw new IdempotencyStoreException();
        }, cancellationToken);
    }
}
