using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>Refuses even empty flushes before any audit capture or domain-event staging.</summary>
public sealed class UnitOfWorkWriteBarrierInterceptor(IUnitOfWork unit) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (unit is IUnitOfWorkWriteGuard guard) guard.RequireWriteAllowed();
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (unit is IUnitOfWorkWriteGuard guard) guard.RequireWriteAllowed();
        return ValueTask.FromResult(result);
    }
}
