using System.Data.Common;
using System.Runtime.CompilerServices;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// The one sanctioned path to a connection that bypasses Row Level Security.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second connection, never <c>SET ROLE</c>.</b>
/// <see href="../../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md">ADR-0003</see>
/// gives three reasons and each rules the alternative out on its own:
/// <c>learnstack_app</c> is not a member of <c>learnstack_platform</c>, and a membership
/// grant would make the bypass a standing capability of the application role reachable
/// from any code path that emits raw SQL; a plain <c>SET ROLE</c> survives <c>COMMIT</c>
/// and would persist on a PgBouncer transaction-pooled server connection into the next
/// tenant's request; and per-role settings such as <c>statement_timeout</c> are applied
/// at login and do not follow a role switch.
/// </para>
/// <para>
/// <b>It is not one of the tenant-context writers, and not a setter of
/// <c>app.tenant_id</c>.</b> It sets no tenant context at all — there is no policy to
/// announce to, because the role bypasses them — so it is outside both closed sets:
/// the four writers of <c>ITenantContextAccessor.Current</c>
/// (<see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Rules</see>, which names it as explicitly not one) and the seven out-of-band
/// setters (<see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040
/// Amendment 3</see>, whose closing property is that every one of them connects as
/// <c>learnstack_app</c>).
/// </para>
/// <para>
/// <b>What Packet 7 ships is a log line, not an audit trail.</b> Entry is recorded
/// through <c>ILogger</c> at <c>Warning</c> with the reason and the calling site.
/// <c>audit_log</c> and <c>IAuditStore</c> arrive in Packet 9, which replaces the log
/// line with a <c>SecurityEvent</c> row written as <c>learnstack_platform</c> before the
/// operation runs. Until then this path is <b>logged</b> and it is not audited; the
/// corpus calls it audited because that is what it will be, and a reader deciding
/// whether cross-tenant access is retained under audit retention today must not read
/// that word as a description of this packet.
/// </para>
/// </remarks>
public interface IPlatformAdminScope
{
    /// <summary>
    /// Opens a platform-role connection and transaction, or refuses.
    /// </summary>
    /// <param name="reason">
    /// Why this cross-tenant access is happening. Required, non-blank, and the value
    /// Packet 9 will write durably — so it is a short operator-authored slug naming the
    /// operation, never a caller-supplied string and never anything carrying personal
    /// data beyond the identifier the operation is already about.
    /// </param>
    /// <param name="cancellationToken">Cancels the open, not an operation already inside.</param>
    /// <param name="callerMember">Supplied by the compiler; do not pass.</param>
    /// <param name="callerFile">Supplied by the compiler; do not pass.</param>
    /// <param name="callerLine">Supplied by the compiler; do not pass.</param>
    /// <remarks>
    /// The three <c>Caller*</c> parameters are how "the caller" is known at all. There is
    /// no principal in the process — authentication is Phase 02b and
    /// <c>AuthorizationBehavior</c> is still a pass-through — so the alternative to the
    /// compiler filling these in is a log line that records only that <i>someone</i>
    /// entered. These are the first use of the attributes anywhere in this solution.
    /// </remarks>
    Task<IPlatformAdminScopeHandle> EnterAsync(
        string reason,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string? callerMember = null,
        [CallerFilePath] string? callerFile = null,
        [CallerLineNumber] int callerLine = 0);
}

/// <summary>
/// An open platform-role connection and its transaction, for as long as it is held.
/// </summary>
/// <remarks>
/// <para>
/// <b>A connection, not a <c>DbContext</c>.</b> ADR-0003 and
/// <see href="../../../../docs/architecture/09-tenant-isolation.md">architecture/09</see>
/// both say "a second connection"; two editable carriers said "a DI scope whose
/// <c>DbContext</c> is built on that data source" and have been corrected to match,
/// because only this shape compiles against what is already shipped — every module
/// <c>DbContext</c> is bound to <c>IUnitOfWork.Connection</c>, which comes from the
/// application data source that the composition root guards to be
/// <c>learnstack_app</c> by name. Nothing is foreclosed: EF can be built on this
/// connection by whoever needs it.
/// </para>
/// <para>
/// <b>Disposing without committing rolls back.</b> The same posture the ambient unit of
/// work takes, and it matters more here: a leaked handle holds a <c>BYPASSRLS</c> pooled
/// connection for the life of the request.
/// </para>
/// <para>
/// <c>System.Data.Common</c> types rather than Npgsql ones because this assembly
/// references no database driver — the same reason <c>IUnitOfWork.Connection</c> is a
/// <see cref="DbConnection"/>.
/// </para>
/// </remarks>
public interface IPlatformAdminScopeHandle : IAsyncDisposable
{
    /// <summary>The open connection, authenticated as the platform role.</summary>
    DbConnection Connection { get; }

    /// <summary>The transaction every command in this scope runs on.</summary>
    DbTransaction Transaction { get; }

    /// <summary>Commits. Without it, disposal rolls back.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);
}
