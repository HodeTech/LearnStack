using LearnStack.Modules.Education.Application.Abstractions;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Education.Infrastructure.Persistence;

public sealed class SeedEducationState(EducationDbContext db, IUnitOfWork unit) : ISeedEducationState
{
    public async Task PrepareAsync(Guid courseId, Guid? lessonId, CancellationToken cancellationToken)
    {
        await LockAsync("SELECT id FROM public.courses WHERE tenant_id = @tenant AND organization_id IS NOT DISTINCT FROM @organization AND id = @id FOR UPDATE", courseId, cancellationToken);
        if (lessonId is { } id)
            await LockAsync("SELECT id FROM public.lessons WHERE tenant_id = @tenant AND organization_id IS NOT DISTINCT FROM @organization AND id = @id FOR UPDATE", id, cancellationToken);
    }

    public async Task<IReadOnlyList<Course>> ReadCoursesAsync(CancellationToken cancellationToken)
    {
        RequireOwner();
        return await db.Courses.AsNoTracking().Where(row => row.OrganizationId == db.CurrentOrganizationId)
            .Include(row => row.Translations).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Lesson>> ReadLessonsAsync(CancellationToken cancellationToken)
    {
        RequireOwner();
        return await db.Lessons.AsNoTracking().Where(row => row.OrganizationId == db.CurrentOrganizationId)
            .Include(row => row.Translations).ToListAsync(cancellationToken);
    }

    private async Task LockAsync(string sql, Guid id, CancellationToken cancellationToken)
    {
        RequireOwner();
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!)
        { CommandTimeout = 10 };
        command.Parameters.AddWithValue("tenant", db.CurrentTenantId.Value);
        command.Parameters.Add(new NpgsqlParameter("organization", NpgsqlDbType.Uuid)
        { Value = db.CurrentOrganizationId is { } organization ? organization.Value : DBNull.Value });
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private void RequireOwner()
    {
        if (!unit.HasActiveTransaction || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadWrite
            || !ReferenceEquals(db.Database.GetDbConnection(), unit.Connection)
            || !ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), unit.Transaction)
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != db.CurrentTenantId || db.CurrentTenantWideRead)
            throw new InvalidOperationException("Fixed content verification requires its ordinary owner.");
    }
}
