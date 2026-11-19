using System.Data.Common;
using System.Runtime.CompilerServices;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// Opens an audited, bounded scope on the <c>learnstack_platform</c> role for one admitted
/// operator act.
/// </summary>
/// <remarks>
/// <para>
/// <b>Admission first.</b> The caller holds a <see cref="PlatformAdmission"/> from
/// <see cref="IPlatformOperatorGate"/>, so the permission was evaluated before this call
/// and a refused operator never reaches a connection (ADR-0036, ADR-0101 § 7). The reason,
/// the operator and the correlation id come from the admission, and the compiler-supplied
/// caller attributes record where the scope was entered.
/// </para>
/// <para>
/// <b>Entry is audited, and refusing to record it refuses the entry.</b> One
/// <c>platform.admin_scope.enter</c> row is written on the scope's own connection in a
/// transaction that commits before the caller's work begins (ADR-0044 § 10), so an act that
/// later fails or is abandoned is still on the record. It carries the operator in
/// <c>metadata.operator</c> and no actor user id, because an operator is not a user.
/// </para>
/// <para>
/// <b>The handle exposes no connection.</b> A reference to a <c>BYPASSRLS</c> connection
/// captured before the commit would run in autocommit afterwards, so the handle offers a
/// bounded facade instead. Completion and abandonment close the connection (ADR-0101 § 8).
/// </para>
/// </remarks>
public interface IPlatformAdminScope
{
    /// <summary>Enters the scope for an admitted act.</summary>
    /// <param name="admission">The gate's proof for one operation that needs the platform role.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="callerMember">Compiler supplied.</param>
    /// <param name="callerFile">Compiler supplied.</param>
    /// <param name="callerLine">Compiler supplied.</param>
    Task<IPlatformAdminScopeHandle> EnterAsync(
        PlatformAdmission admission,
        CancellationToken cancellationToken = default,
        [CallerMemberName] string? callerMember = null,
        [CallerFilePath] string? callerFile = null,
        [CallerLineNumber] int callerLine = 0);
}

/// <summary>A positional SQL parameter for the scope's bounded facade.</summary>
/// <param name="Name">The parameter name, without a prefix.</param>
/// <param name="Value">The value; null binds SQL NULL.</param>
public sealed record PlatformSqlParameter(string Name, object? Value);

/// <summary>The state change one act records as its MUST result row.</summary>
/// <param name="TenantId">
/// The real target tenant, or <see cref="TenantId.PlatformSentinel"/> for a platform-wide act.
/// </param>
/// <param name="EntityType">The affected entity type, when there is one.</param>
/// <param name="EntityId">The affected entity id, when there is one.</param>
/// <param name="BeforeState">The before state as JSON, when there is one.</param>
/// <param name="AfterState">The after state as JSON, when there is one.</param>
public sealed record PlatformResult(
    TenantId TenantId,
    string? EntityType,
    string? EntityId,
    string? BeforeState,
    string? AfterState);

/// <summary>
/// The work one scope entry allows: a transaction on the platform role, as a facade.
/// </summary>
/// <remarks>
/// <b>One scope, one transaction, one commit.</b> After <see cref="CommitAsync"/> or disposal
/// the handle refuses every call and the underlying connection is closed, so nothing a
/// caller captured can run an autocommit statement on a connection that sees every tenant.
/// Disposing without committing rolls back.
/// </remarks>
public interface IPlatformAdminScopeHandle : IAsyncDisposable
{
    /// <summary>Executes one non-query statement on the work transaction.</summary>
    Task<int> ExecuteAsync(
        string sql, IReadOnlyList<PlatformSqlParameter>? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>Runs one query on the work transaction and maps each row before the reader closes.</summary>
    Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        IReadOnlyList<PlatformSqlParameter>? parameters,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the act's MUST result row on the work transaction, so it commits with the
    /// state change or not at all.
    /// </summary>
    Task WriteResultAsync(PlatformResult result, CancellationToken cancellationToken = default);

    /// <summary>Commits the work transaction and closes the scope.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);
}
