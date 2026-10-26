using System.Collections.Immutable;
using System.Data.Common;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
        // Pre/post-wait reads must materialize fresh facts; a cached tracked entity
        // would retain the earlier classification even after SQL returned new values.
        db.ChangeTracker.Clear();
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
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

    private static bool IsOwned(PostgresException failure) => failure.SqlState is "P0301" or "P0302" or "P0303" or "P0304"
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
    public async Task<Role?> FindAsync(RoleId roleId, CancellationToken cancellationToken = default)
    {
        control.PrepareFreshRead();
        return await db.Roles.Include(role => role.Grants).SingleOrDefaultAsync(role => role.Id == roleId, cancellationToken);
    }

    public Task<bool> HasMembershipHistoryAsync(CancellationToken cancellationToken = default) => db.Memberships.AnyAsync(cancellationToken);
    public Task ValidateGraphAsync(CancellationToken cancellationToken = default) => control.ValidateGraphAsync(cancellationToken);
    public Task AddAsync(Role aggregate, CancellationToken cancellationToken = default)
    {
        db.Roles.Add(aggregate);
        return control.SaveAsync(cancellationToken);
    }
    public Task UpdateAsync(Role aggregate, CancellationToken cancellationToken = default)
    {
        WriteStoreTracking.EnsureTracked(db, aggregate);
        return control.SaveAsync(cancellationToken);
    }
}

public sealed class MembershipWriteStore(IdentityDbContext db, ContextualIdentityStorageControl control) : IMembershipWriteStore
{
    public async Task<Membership?> FindAsync(MembershipId membershipId, CancellationToken cancellationToken = default)
    {
        control.PrepareFreshRead();
        return await db.Memberships.Include(member => member.Profile).Include(member => member.RoleAssignments)
            .SingleOrDefaultAsync(member => member.Id == membershipId, cancellationToken);
    }

    public Task<bool> HasMembershipHistoryAsync(CancellationToken cancellationToken = default) => db.Memberships.AnyAsync(cancellationToken);
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

public sealed class SeedStateReader(IdentityDbContext db) : ISeedStateReader
{
    public async Task<RoleSeedDto?> ReadRoleAsync(RoleId roleId, CancellationToken cancellationToken = default)
    {
        var role = await db.Roles.AsNoTracking().Include(role => role.Grants)
            .SingleOrDefaultAsync(role => role.Id == roleId, cancellationToken);
        return role is null ? null : new(role.Id.Value, role.TenantId, role.BuiltInKey, role.PermissionScope,
            role.Version, role.DeletedAt, role.DeletedBy,
            role.Grants.Select(grant => new PermissionDefinition(grant.PermissionScope, grant.PermissionKey)).ToImmutableArray());
    }

    public async Task<MembershipSeedDto?> ReadMembershipAsync(MembershipId membershipId, CancellationToken cancellationToken = default)
    {
        var member = await db.Memberships.AsNoTracking().Include(member => member.Profile).Include(member => member.RoleAssignments)
            .SingleOrDefaultAsync(member => member.Id == membershipId, cancellationToken);
        return member is null ? null : new(member.Id.Value, member.TenantId, member.OrganizationId, member.UserId,
            member.Status.ToString(), member.Version, member.DeletedAt, member.DeletedBy,
            member.Profile.DisplayName, member.Profile.PreferredLocale,
            member.RoleAssignments.Select(assignment => assignment.RoleId.Value).ToImmutableArray());
    }
}

public sealed class RoleReadStore(IdentityDbContext db) : IRoleReadStore
{
    public Task<Role?> ReadAsync(RoleId roleId, CancellationToken cancellationToken = default) =>
        db.Roles.AsNoTracking().Include(role => role.Grants).SingleOrDefaultAsync(role => role.Id == roleId, cancellationToken);
}
