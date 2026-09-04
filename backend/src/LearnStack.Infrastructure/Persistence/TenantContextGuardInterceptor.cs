using System.Data.Common;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>
/// Refuses any command a module <c>DbContext</c> issues on a transaction no sanctioned
/// setter announced the tenant on.
/// </summary>
/// <remarks>
/// <para>
/// <b>It turns a silence into a failure, and it is not the isolation boundary.</b> With
/// <c>app.tenant_id</c> unset every Row Level Security predicate is <c>NULL</c>, so a
/// tenant-owned read returns zero rows and a write is refused — fail-closed already. The
/// problem is that it is quiet: the symptom reaching an operator is missing data, not a
/// fault, and missing data gets investigated as a bug in the feature. Removing this
/// interceptor removes a diagnostic, never a protection.
/// </para>
/// <para>
/// <b>Keyed on the transaction, not on the table.</b> Some corpus sentences describe it
/// as guarding tenant-owned tables; the rule's own name —
/// <c>Tenant_Context_Guard_Fires_Only_On_An_Unmarked_Transaction</c> — and the packet
/// plan describe the transaction, and that is what shipped. Matching table names would
/// mean parsing command text to decide whether a guard applies, which is a parser
/// standing between every query and the database, wrong on the first CTE. Every command
/// from a module context belongs to a request that had a tenant to announce.
/// </para>
/// <para>
/// <b>It never sees a raw <c>NpgsqlCommand</c>, and that is what keeps the exemption
/// list empty.</b> EF interception covers commands EF itself issues. So the
/// <c>set_config</c> pair <c>NpgsqlUnitOfWork</c> sends needs no self-exemption, and
/// <c>CachedHostToTenantResolver</c>, <c>OrganizationScopeValidator</c> and
/// <c>PlatformAdminScope</c> are invisible by construction — which matters most for the
/// last of those: it is a <c>BYPASSRLS</c> connection that deliberately announces no
/// tenant, and an exemption written by hand is an exemption someone widens.
/// </para>
/// <para>
/// <b>Six overrides because EF has no aggregate hook</b> and does not route the
/// synchronous APIs through the asynchronous ones. A LINQ query and a
/// <c>SaveChanges</c> INSERT both arrive on <c>ReaderExecuting</c>; raw SQL arrives on
/// <c>NonQueryExecuting</c>. Covering only one pair would leave the other silent, which
/// is the failure this exists to end.
/// </para>
/// </remarks>
public sealed class TenantContextGuardInterceptor : DbCommandInterceptor
{
    private readonly IUnitOfWork _unitOfWork;

    public TenantContextGuardInterceptor(IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        _unitOfWork = unitOfWork;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Guard(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Guard(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Guard(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Guard(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Guard(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Guard(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    /// <summary>Throws unless a sanctioned setter announced this command's transaction.</summary>
    private void Guard(DbCommand command)
    {
        if (_unitOfWork.IsTenantContextIssuedOn(command.Transaction))
        {
            return;
        }

        throw new TenantContextMissingException(
            "A module DbContext issued a command on a transaction no sanctioned setter "
            + "announced app.tenant_id on. Every Row Level Security predicate is NULL "
            + "there, so this read would have returned zero rows and this write would "
            + "have been refused — safely, and without saying so. The announcement is "
            + "IUnitOfWork.SetTenantContextAsync, issued by TransactionBehavior as the "
            + "first statement inside the ambient transaction at pipeline step 6; a "
            + "command arriving here without it is on a transaction something else "
            + "opened. See Security Standards § Tenant Context.");
    }
}
