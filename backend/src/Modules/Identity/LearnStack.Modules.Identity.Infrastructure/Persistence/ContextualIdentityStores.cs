using System.Data.Common;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>One scoped graph preflight on the existing owner; never interprets a physical COMMIT failure.</summary>
public sealed class ContextualIdentityStorageControl(IdentityDbContext db, IUnitOfWork unit)
{
    private bool _validated;
    private bool _reportedPlanFault;

    public void PrepareFreshRead()
    {
        if (_validated || db.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("An Identity graph cannot be reread after mutation or validation.");
        EnrollCurrentTenantOwner(db, unit);
        // Pre/post-wait reads must materialize fresh facts; a cached tracked entity
        // would retain the earlier classification even after SQL returned new values.
        db.ChangeTracker.Clear();
    }

    internal static void EnrollCurrentTenantOwner(IdentityDbContext context, IUnitOfWork owner)
    {
        // Binding lookup may have created this scoped model on an earlier global
        // owner. Re-enroll only the same connection's already-announced tenant
        // transaction; this supplies no context-setting or read-scope authority.
        if (!owner.HasActiveTransaction || owner.IsRollbackOnly
            || owner is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || !owner.IsTenantContextIssuedOn(owner.Transaction)
            || owner.GetTenantContextAnnouncement(owner.Transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != context.CurrentTenantId || announcement.OrganizationId != context.CurrentOrganizationId
            || !ReferenceEquals(context.Database.GetDbConnection(), owner.Connection)
            || context.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new TenantContextMissingException("Identity storage requires its clean announced tenant owner.");
        if (context.Database.CurrentTransaction is not { } current
            || !ReferenceEquals(current.GetDbTransaction(), owner.Transaction))
            context.Database.UseTransaction(owner.Transaction);
    }

    internal static async Task<T> ReadAsync<T>(Func<Task<T>> read)
    {
        // Only provider failures cross this boundary. Cancellation and source
        // invariants keep their original meaning; reads never classify a COMMIT.
        try { return await read(); }
        catch (InvalidOperationException failure) when (
            failure.InnerException is NpgsqlException { IsTransient: true } or TimeoutException)
        {
            throw new IdentityStorageProviderException();
        }
        catch (Exception failure) when (failure is DbException or TimeoutException)
        {
            throw new IdentityStorageProviderException();
        }
    }

    internal Task LockRoleAsync(RoleId roleId, CancellationToken cancellationToken) =>
        LockRootAsync(roleId.Value, membership: false, cancellationToken);

    internal Task LockMembershipAsync(MembershipId membershipId, CancellationToken cancellationToken) =>
        LockRootAsync(membershipId.Value, membership: true, cancellationToken);

    private async Task LockRootAsync(Guid id, bool membership, CancellationToken cancellationToken)
    {
        if (unit is IUnitOfWorkWriteGuard writeGuard) writeGuard.RequireWriteAllowed();
        EnrollCurrentTenantOwner(db, unit);
        if (_validated || unit.Mode != TransactionMode.ReadWrite)
            throw new InvalidOperationException("Identity root locking requires its writable tenant owner.");
        try
        {
            await using var command = new NpgsqlCommand(membership ? """
                SELECT public.fn_identity_assert_tenant_fence(@tenant,false);
                SELECT id FROM public.memberships
                WHERE tenant_id=@tenant AND id=@id AND organization_id IS NOT DISTINCT FROM @organization
                FOR NO KEY UPDATE;
                """ : """
                SELECT public.fn_identity_assert_tenant_fence(@tenant,false);
                SELECT id FROM public.roles WHERE tenant_id=@tenant AND id=@id FOR NO KEY UPDATE;
                """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!)
            { CommandTimeout = 10 };
            command.Parameters.AddWithValue("tenant", db.CurrentTenantId.Value);
            command.Parameters.AddWithValue("id", id);
            if (membership)
                command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid,
                    db.CurrentOrganizationId is { } organization ? organization.Value : DBNull.Value);
            // This statement supplies only retained exclusion. Materialize the
            // complete graph with a separate READ COMMITTED statement afterward.
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException failure) when (IsOwned(failure))
        {
            unit.MarkRollbackOnly();
            throw Translate(failure);
        }
        catch (Exception failure) when (failure is DbException or TimeoutException)
        {
            unit.MarkRollbackOnly();
            throw new IdentityStorageProviderException();
        }
        catch
        {
            unit.MarkRollbackOnly();
            throw;
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (unit is IUnitOfWorkWriteGuard writeGuard) writeGuard.RequireWriteAllowed();
        if (_validated) throw new InvalidOperationException("An Identity graph cannot change after validation.");
        // The common owner must roll back this complete aggregate act on every
        // failure. A private EF subtransaction supplies no recoverable boundary
        // and gives a new Role a child xmin, defeating the exact first-act proof.
        // Confine the setting to this save; global Identity keeps its own behavior.
        var savepoints = db.Database.AutoSavepointsEnabled;
        db.Database.AutoSavepointsEnabled = false;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException failure) when (failure.InnerException is PostgresException postgres && IsOwned(postgres))
        {
            unit.MarkRollbackOnly();
            throw Translate((PostgresException)failure.InnerException!);
        }
        catch (InvalidOperationException failure) when (
            failure.InnerException is DbUpdateException { InnerException: PostgresException postgres } && IsOwned(postgres))
        {
            // Npgsql's non-retrying execution strategy wraps transient save
            // failures. Translate only the same exact owned provider states.
            unit.MarkRollbackOnly();
            throw Translate((PostgresException)failure.InnerException.InnerException!);
        }
        catch (InvalidOperationException failure) when (failure.InnerException is
            DbUpdateException { InnerException: NpgsqlException { IsTransient: true } or TimeoutException }
            or NpgsqlException { IsTransient: true } or TimeoutException)
        {
            unit.MarkRollbackOnly();
            throw new IdentityStorageProviderException();
        }
        catch (DbUpdateConcurrencyException)
        {
            unit.MarkRollbackOnly();
            throw new IdentityFoundationStorageException(IdentityFoundationStorageFailure.Conflict);
        }
        catch (Exception failure) when (failure is DbUpdateException or DbException or TimeoutException)
        {
            unit.MarkRollbackOnly();
            throw new IdentityStorageProviderException();
        }
        catch
        {
            unit.MarkRollbackOnly();
            throw;
        }
        finally { db.Database.AutoSavepointsEnabled = savepoints; }
    }

    public async Task ValidateGraphAsync(CancellationToken cancellationToken)
    {
        if (unit is IUnitOfWorkWriteGuard writeGuard) writeGuard.RequireWriteAllowed();
        if (_validated || !unit.HasActiveTransaction || unit.Mode != TransactionMode.ReadWrite
            || !unit.IsTenantContextIssuedOn(unit.Transaction))
            throw new InvalidOperationException("Identity graph validation requires its active tenant owner.");
        try
        {
            await using var command = unit.Connection.CreateCommand();
            command.Transaction = unit.Transaction;
            command.CommandText = "SET CONSTRAINTS ALL IMMEDIATE";
            command.CommandTimeout = 10;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _validated = true;
        }
        catch (PostgresException failure) when (IsOwned(failure))
        {
            unit.MarkRollbackOnly();
            throw Translate(failure);
        }
        catch (Exception failure) when (failure is DbException or TimeoutException)
        {
            unit.MarkRollbackOnly();
            throw new IdentityStorageProviderException();
        }
    }

    // These states are translated only while saving/preflighting the owned graph,
    // never at the physical COMMIT boundary whose outcome may be uncertain.
    private static bool IsOwned(PostgresException failure) => failure.SqlState is "P0301" or "P0302" or "P0303" or "P0304"
        or PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.SerializationFailure
        or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.QueryCanceled
        || failure.SqlState is "23505" or "23514" or "23503"
            && failure.ConstraintName is { } name
            && (name.Contains("membership", StringComparison.Ordinal) || name.Contains("roles", StringComparison.Ordinal)
                || name.Contains("role_permission_grants", StringComparison.Ordinal));

    private IdentityFoundationStorageException Translate(PostgresException failure)
    {
        var reason = failure.SqlState switch
        {
            "P0301" => IdentityFoundationStorageFailure.Plan,
            "P0302" => IdentityFoundationStorageFailure.Target,
            "P0303" => IdentityFoundationStorageFailure.Invariant,
            "P0304" or "23514" or "23503" => IdentityFoundationStorageFailure.Graph,
            _ => IdentityFoundationStorageFailure.Conflict,
        };
        if (reason == IdentityFoundationStorageFailure.Plan && !_reportedPlanFault)
        {
            IdentityLockObservation.PlanFault(failure.ConstraintName == "identity_user_fence" ? IdentityLockKind.User : IdentityLockKind.Tenant);
            _reportedPlanFault = true;
        }
        return new IdentityFoundationStorageException(reason);
    }
}

public sealed class RoleWriteStore(IdentityDbContext db, ContextualIdentityStorageControl control) : IRoleWriteStore
{
    public Task LockAsync(RoleId roleId, CancellationToken cancellationToken = default) => control.LockRoleAsync(roleId, cancellationToken);

    public async Task<Role?> FindAsync(RoleId roleId, CancellationToken cancellationToken = default)
    {
        control.PrepareFreshRead();
        return await ContextualIdentityStorageControl.ReadAsync(() => db.Roles.Include(role => role.Grants)
            .SingleOrDefaultAsync(role => role.Id == roleId, cancellationToken));
    }

    public Task<bool> HasMembershipHistoryAsync(CancellationToken cancellationToken = default) =>
        ContextualIdentityStorageControl.ReadAsync(() => db.Memberships.AnyAsync(cancellationToken));
    public Task ValidateGraphAsync(CancellationToken cancellationToken = default) => control.ValidateGraphAsync(cancellationToken);
    public Task AddAsync(Role aggregate, CancellationToken cancellationToken = default)
    {
        db.Roles.Add(aggregate);
        return control.SaveAsync(cancellationToken);
    }
    public Task UpdateAsync(Role aggregate, CancellationToken cancellationToken = default)
    {
        // The database keeps its restrictive parent FK. Explicitly mark only
        // removed tracked children before EF detects the severed required link;
        // retained instances stay tracked and new children are discovered at save.
        var detectChanges = db.ChangeTracker.AutoDetectChangesEnabled;
        try
        {
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            WriteStoreTracking.EnsureTracked(db, aggregate);
            foreach (var grant in db.ChangeTracker.Entries<RolePermissionGrant>().Where(entry =>
                entry.Entity.TenantId == aggregate.TenantId && entry.Entity.RoleId == aggregate.Id
                && !aggregate.Grants.Contains(entry.Entity)).ToArray())
                grant.State = EntityState.Deleted;
        }
        finally { db.ChangeTracker.AutoDetectChangesEnabled = detectChanges; }
        return control.SaveAsync(cancellationToken);
    }
}

public sealed class MembershipWriteStore(IdentityDbContext db, ContextualIdentityStorageControl control) : IMembershipWriteStore
{
    public Task LockAsync(MembershipId membershipId, CancellationToken cancellationToken = default) =>
        control.LockMembershipAsync(membershipId, cancellationToken);

    public async Task<Membership?> FindAsync(MembershipId membershipId, CancellationToken cancellationToken = default)
    {
        control.PrepareFreshRead();
        return await ContextualIdentityStorageControl.ReadAsync(() => db.Memberships.Include(member => member.Profile)
            .Include(member => member.RoleAssignments).SingleOrDefaultAsync(member => member.Id == membershipId, cancellationToken));
    }

    public Task<bool> HasMembershipHistoryAsync(CancellationToken cancellationToken = default) =>
        ContextualIdentityStorageControl.ReadAsync(() => db.Memberships.AnyAsync(cancellationToken));
    public Task ValidateGraphAsync(CancellationToken cancellationToken = default) => control.ValidateGraphAsync(cancellationToken);
    public Task AddAsync(Membership aggregate, CancellationToken cancellationToken = default)
    {
        db.Memberships.Add(aggregate);
        return control.SaveAsync(cancellationToken);
    }
    public Task UpdateAsync(Membership aggregate, CancellationToken cancellationToken = default)
    {
        WriteStoreTracking.EnsureTracked(db, aggregate);
        return control.SaveAsync(cancellationToken);
    }
}

public sealed class RoleReadStore(IdentityDbContext db) : IRoleReadStore
{
    public Task<Role?> ReadAsync(RoleId roleId, CancellationToken cancellationToken = default) =>
        ContextualIdentityStorageControl.ReadAsync(() => db.Roles.AsNoTracking().Include(role => role.Grants)
            .SingleOrDefaultAsync(role => role.Id == roleId, cancellationToken));
}
