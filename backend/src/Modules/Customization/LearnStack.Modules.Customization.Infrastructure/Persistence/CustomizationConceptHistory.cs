using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace LearnStack.Modules.Customization.Infrastructure.Persistence;

/// <summary>Tenant-protected coordination and history for the two versioned concepts.</summary>
internal static class CustomizationConceptHistory
{
    internal static async Task LockAsync(
        CustomizationDbContext db, bool taxonomy, string key, CancellationToken cancellationToken)
    {
        var (tenant, connection, transaction) = RequireContext(db, key);
        // A stable namespace includes tenant and kind, but deliberately no revision.
        // A hash collision only serializes unrelated writers; it cannot grant access.
        var identity = $"customization:{tenant.Value:D}:{(taxonomy ? "taxonomy" : "content-type")}:{key}";
        await LockIdentityAsync(identity, connection, transaction, cancellationToken);
    }

    internal static async Task LockRegistrationsAsync(CustomizationDbContext db, CancellationToken cancellationToken)
    {
        var (tenant, connection, transaction) = RequireContext(db, BuiltInCustomizations.Card.Key);
        await LockIdentityAsync($"customization:{tenant.Value:D}:content-type-quota", connection, transaction, cancellationToken);
    }

    private static async Task LockIdentityAsync(string identity, NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var lockKey = BinaryPrimitives.ReadInt64BigEndian(digest);
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@lock_key)", connection, transaction);
        command.Parameters.AddWithValue("lock_key", lockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<bool> ExistsAsync(
        CustomizationDbContext db, bool taxonomy, string key, CancellationToken cancellationToken)
    {
        var (tenant, connection, transaction) = RequireContext(db, key);
        // This owner-local query includes tombstones without disabling EF tenant filters.
        // RLS remains active, and both tenant and semantic key are explicit parameters.
        var sql = taxonomy
            ? "SELECT EXISTS (SELECT 1 FROM tenant_level_taxonomies WHERE tenant_id = @tenant AND key = @key)"
            : "SELECT EXISTS (SELECT 1 FROM tenant_content_types WHERE tenant_id = @tenant AND key = @key)";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("key", key);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static (TenantId Tenant, NpgsqlConnection Connection, NpgsqlTransaction Transaction) RequireContext(
        CustomizationDbContext db, string key)
    {
        CustomizationKey.EnsureValid(key, nameof(key));
        var tenant = db.CurrentTenantId;
        if (!tenant.IsInitialized() || tenant.Value == Guid.Empty || tenant == TenantId.PlatformSentinel)
            throw new InvalidOperationException("Customization concept access requires a resolved tenant.");
        if (db.Database.CurrentTransaction?.GetDbTransaction() is not NpgsqlTransaction transaction
            || transaction.Connection is not { } connection)
            throw new InvalidOperationException("Customization concept access requires an active transaction.");
        return (tenant, connection, transaction);
    }
}
