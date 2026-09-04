using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;

namespace LearnStack.Infrastructure.MultiTenancy;

/// <summary>
/// Reads <c>organizations</c> on the composite key <c>(tenant_id, id)</c>, under the
/// tenant's own Row Level Security context, in a transaction of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a transaction of its own — the seventh sanctioned setter.</b> The question
/// is asked at the request edge, deciding whether a claimed organization may be part
/// of the context at all. That is strictly before the MediatR pipeline reaches
/// <c>TransactionBehavior</c> at step 6, so there is no ambient unit of work to
/// enlist on; <see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040
/// Amendment 3</see> adds this to the closed set of <c>app.tenant_id</c> setters for
/// exactly that reason, and requires it to obey the same rule as the four before it:
/// its own short transaction, on its own connection, connected as
/// <c>learnstack_app</c>.
/// </para>
/// <para>
/// <b>Why the policy does the work and the <c>WHERE</c> clause only helps.</b> The
/// row is admitted by <c>organizations_isolation</c>, which compares the row's
/// <c>tenant_id</c> against <c>app.tenant_id</c>. So the answer is not "the query
/// found a row whose tenant column matched" — that would be a comparison in
/// application code, outside the policy, and it is the shape a lookup by the
/// surrogate primary key invites. With the announcement made first, an organization
/// belonging to another tenant is invisible: the read returns nothing, and nothing
/// is the answer.
/// </para>
/// <para>
/// <b>No cache, deliberately.</b> ADR-0036 § Consequences budgets for this read to be
/// cached, and it should be — when there is traffic to size it against. Its only
/// caller is the reconciliation matrix's row 7, which needs a validated claim and is
/// therefore unreachable until Phase 02b. A TTL chosen now would be a number picked
/// with nothing to measure, copied forward, and outliving the guess; the same
/// argument <c>HostResolutionOptions</c> makes for keeping its own numbers beside
/// their measurement. The cache lands with the traffic.
/// </para>
/// <para>
/// <b>An outage is an outage.</b> Nothing here catches a database failure, matching
/// <c>CachedHostToTenantResolver</c>: the exception propagates and the request is a
/// <c>500</c>. Converting it to a refusal would render an outage as "this
/// organization does not belong to you", which is a lie to the caller and an
/// invisible incident to the operator. The anonymous-path argument against a
/// <c>500</c> does not apply — every caller that can reach this holds a validated
/// token, so there is no host-existence oracle to protect.
/// </para>
/// </remarks>
public sealed class OrganizationScopeValidator(Lazy<NpgsqlDataSource> dataSource)
    : IOrganizationScopeValidator
{
    private readonly Lazy<NpgsqlDataSource> _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <inheritdoc />
    public async Task<bool> BelongsToTenantAsync(
        TenantId tenantId,
        OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        // Refused before a connection is opened. Vogen validates the SHAPE of an id,
        // not that it names anything: TenantId.From(Guid.Empty) is a legal,
        // initialized id — measured — and the domain already refuses the all-zero
        // tenant by hand. Announcing one here would be a well-formed uuid matching
        // rows only a bug could have written, so this is fail-closed either way; it
        // is refused explicitly so the answer is "no" rather than "no, by accident".
        if (!tenantId.IsInitialized() || tenantId.Value == Guid.Empty
            || !organizationId.IsInitialized() || organizationId.Value == Guid.Empty)
        {
            return false;
        }

        await using var connection =
            await _dataSource.Value.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        // Four carriers call this a "short READ-ONLY transaction" — the port's own
        // doc, the Standards 11 setter table, the glossary and ADR-0040 Amendment 3 —
        // and learnstack_app holds write grants on organizations, so nothing but this
        // statement made it true. Read-only is the property that makes a seventh
        // member of a closed set of app.tenant_id setters uncontroversial;
        // set_config(..., true) is still permitted inside one.
        await using (var readOnly = new NpgsqlCommand(
            "SET TRANSACTION READ ONLY", connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        }

        // set_config(..., true) and not SET LOCAL: PostgreSQL's SET takes no bind
        // parameter, so `SET LOCAL app.tenant_id = $1` is a syntax error and the
        // only alternative would be interpolating a caller-supplied identifier into
        // DDL-shaped text. The value here came from a token claim, which is exactly
        // the input that must never be concatenated.
        await using (var announce = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction))
        {
            // ToString on the Guid, not on the id: set_config's signature is
            // (text, text, boolean) and there is no uuid overload — measured, a uuid
            // parameter raises 42883 on the first call. TenantId.ToString() would be
            // worse than wrong: on Vogen 7 an uninitialized id renders as the literal
            // "[UNINITIALIZED]", which reaches the policy cast as
            // '[UNINITIALIZED]'::uuid and raises 22P02.
            announce.Parameters.AddWithValue(
                "tenant", tenantId.Value.ToString());
            await announce.ExecuteNonQueryAsync(cancellationToken);
        }

        // Both key columns, never `id` alone. pk_organizations is on the surrogate
        // id, so a lookup by it alone is a well-formed query that returns another
        // tenant's row for the policy to then hide — or, run before the announcement,
        // hands it back. ux_organizations_tenant_id_id serves this shape.
        await using var read = new NpgsqlCommand(
            """
            SELECT 1 FROM organizations
            WHERE tenant_id = @tenant AND id = @organization AND deleted_at IS NULL
            """,
            connection,
            transaction);
        read.Parameters.AddWithValue("tenant", tenantId.Value);
        read.Parameters.AddWithValue("organization", organizationId.Value);

        var found = await read.ExecuteScalarAsync(cancellationToken) is not null;

        await transaction.CommitAsync(cancellationToken);
        return found;
    }
}
