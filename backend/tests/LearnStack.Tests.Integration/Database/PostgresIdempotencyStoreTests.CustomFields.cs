using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Idempotency;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class PostgresIdempotencyStoreTests
{
    [Fact]
    public async Task Metadata_read_keeps_expired_physical_key_id_without_changing_or_disclosing_the_row()
    {
        var tenant = Tenant();
        var other = Tenant();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source);
        var fingerprint = "cfh1:retained:" + new string('a', 64);
        var claim = await store.TryClaimAsync(tenant, "retained-key", fingerprint, CancellationToken.None);
        Assert.Equal(IdempotencyClaim.Acquired, claim.Outcome);
        await ExpireAsync(tenant, "retained-key");
        var before = await SnapshotAsync(tenant);
        var metadata = await store.ReadKeyMetadataAsync(tenant, "retained-key", CancellationToken.None);
        Assert.True(metadata.Exists);
        Assert.Equal("retained", metadata.KeyId);
        Assert.Equal(before, await SnapshotAsync(tenant));
        var foreign = await store.ReadKeyMetadataAsync(other, "retained-key", CancellationToken.None);
        Assert.False(foreign.Exists);
        Assert.Null(foreign.KeyId);
    }

    [Fact]
    public async Task Historical_and_malformed_metadata_are_distinguished_without_returning_a_digest()
    {
        var tenant = Tenant();
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var store = Store(source);
        var historical = new string('b', 64);
        Assert.Equal(IdempotencyClaim.Acquired,
            (await store.TryClaimAsync(tenant, "old-keyed-slot", historical, CancellationToken.None)).Outcome);
        var before = await SnapshotAsync(tenant);
        var metadata = await store.ReadKeyMetadataAsync(tenant, "old-keyed-slot", CancellationToken.None);
        Assert.True(metadata.Exists);
        Assert.Null(metadata.KeyId);
        Assert.Equal(before, await SnapshotAsync(tenant));
        Assert.Equal(IdempotencyClaim.Acquired,
            (await store.TryClaimAsync(tenant, "bad-format-slot", "cfh1:broken", CancellationToken.None)).Outcome);
        await Assert.ThrowsAsync<IdempotencyStoreException>(() =>
            store.ReadKeyMetadataAsync(tenant, "bad-format-slot", CancellationToken.None));
    }
}
