using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Customization.Domain;

/// <summary>
/// One tenant's customization generation — the counter every cache key for this
/// module embeds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not an aggregate, and the distinction is load-bearing.</b>
/// <see href="../../../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043
/// § 7</see> places it in the same class as <c>outbox_messages</c> and
/// <c>idempotency_keys</c> — a durable, non-aggregate row that
/// <see href="../../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// already puts inside the business transaction. It carries no identity of its
/// own beyond the tenant it counts for, no audit columns, and no concurrency
/// token; its port does not derive from <c>IAggregateWriteStore</c> and names no
/// <c>Customization.Domain</c> type, so neither
/// <c>Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning</c> nor
/// <c>Every_Write_Port_Is_Countable_Or_Enumerated</c> has anything to say about a
/// handler that bumps it.
/// </para>
/// <para>
/// <b>It lives here rather than as a column on <c>tenants</c></b> because a module
/// writing another module's table is forbidden with no exception at all, and
/// because the bump belongs to the same transaction as the customization write it
/// invalidates for.
/// </para>
/// <para>
/// <b>Why a counter rather than prefix eviction.</b>
/// <see href="../../../../../docs/architecture/32-tenant-customization-model.md">§ 8.2</see>:
/// the published <c>ICacheService.RemoveByPrefixAsync</c> contract cannot be
/// honoured across instances by any candidate backend and was removed in Packet 5.
/// A generation folded into the key makes every stale key unreachable at once,
/// across every pod, without enumerating anything.
/// </para>
/// <para>
/// It is mapped as an entity so all four isolation layers reach it — the marker,
/// the EF filter, the policy and the architecture test — rather than being a raw
/// table only the migration knows about. The <b>bump</b> is still one statement,
/// because a read-modify-write loses one of two concurrent customization writes.
/// </para>
/// </remarks>
[TenantOwned]
public sealed class CustomizationGeneration : ITenantOwned
{
    private CustomizationGeneration()
    {
    }

    /// <summary>The tenant this counts for, and the row's whole identity.</summary>
    public TenantId TenantId { get; private set; }

    /// <summary>
    /// Advanced by one on every customization write, in that write's transaction.
    /// </summary>
    /// <remarks>
    /// Starts at 1 rather than 0 so a cache key is never composed against a
    /// generation a tenant with no row would also produce.
    /// </remarks>
    public long Generation { get; private set; }
}
