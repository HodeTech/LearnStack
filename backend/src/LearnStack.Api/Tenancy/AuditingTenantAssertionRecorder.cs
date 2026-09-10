using System.Globalization;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.Logging;

namespace LearnStack.Api.Tenancy;

/// <summary>
/// The Packet 9 recorder: the metric and the log line of
/// <see cref="LoggingTenantAssertionRecorder"/>, plus the <c>audit_log</c> row.
/// </summary>
/// <remarks>
/// <para>
/// <b>A decorator, because the two are different jobs.</b> The counter and the warning are
/// the real-time signal an operator alerts on; the row is the durable record a compliance
/// reviewer reads afterwards. Keeping the first where it already lives means the metrics
/// ADR-0036 fixes do not move, the architecture rule that says only one file may name
/// those counters keeps holding, and a deployment with no application credential — a
/// platform-hosts-only one — still counts what it rejects.
/// </para>
/// <para>
/// <b>Two events, not one, because the two tiers have different amplification
/// profiles</b>
/// (<see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Recording a rejected assertion</see>). A mismatch carrying a validated principal is
/// bounded by token issuance and the actor is the finding, so every occurrence is a row
/// with no coalescing and no per-tenant ceiling. An anonymous mismatch is bounded by
/// nothing, so it is not itself audited — the <b>burst</b> is, once per
/// <c>(resolved tenant, dimension, window)</c>.
/// </para>
/// <para>
/// <b>The authenticated tier is dormant in Phase 02a.</b> There is no
/// <c>UseAuthentication</c> yet, so <c>IsAuthenticated</c> is constant-false and every
/// caller takes the anonymous branch. The branch ships anyway: which tenant the row
/// carries is a one-way door, and writing it for the first time under Phase 02b's
/// authenticated traffic is the worst moment to decide it.
/// </para>
/// <para>
/// <b>A failed write never changes the response.</b> A rejected assertion has no
/// uncommitted-but-unaudited state change and no ungranted-but-unaudited disclosure, so
/// fail-closed has nothing to protect — while a <c>503</c> appearing under load an
/// anonymous caller can generate is a remotely triggerable availability signal that same
/// caller controls. The store has already logged <c>Critical</c>, counted
/// <c>learnstack_audit_standalone_write_failures_total</c> and taken the <c>audit</c>
/// health check unhealthy (ADR-0033 Amendments 1 and 3); this catch adds nothing to that
/// and deliberately does not repeat it.
/// </para>
/// </remarks>
public sealed class AuditingTenantAssertionRecorder(
    LoggingTenantAssertionRecorder inner,
    IAuditCatalog catalog,
    IAuditStore store,
    TenantAssertionBurstDetector bursts,
    IClock clock,
    IGuidFactory guidFactory,
    ILogger<AuditingTenantAssertionRecorder> logger)
    : ITenantAssertionRecorder
{
    /// <summary>Written per occurrence, for a mismatch carrying a validated principal.</summary>
    public const string RejectOperation = "tenancy.tenant_assertion.reject";

    /// <summary>Written once per <c>(resolved tenant, dimension, window)</c>.</summary>
    public const string BurstOperation = "tenancy.tenant_assertion.anonymous_burst";

    private readonly LoggingTenantAssertionRecorder _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));

    private readonly IAuditCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IAuditStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly TenantAssertionBurstDetector _bursts =
        bursts ?? throw new ArgumentNullException(nameof(bursts));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly IGuidFactory _guidFactory =
        guidFactory ?? throw new ArgumentNullException(nameof(guidFactory));

    private readonly ILogger<AuditingTenantAssertionRecorder> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task RecordRejectionAsync(
        TenantAssertionRejection rejection, CancellationToken cancellationToken = default)
    {
        // The signal first, and unconditionally. It costs no I/O and it is the half that
        // survives an audit store being unreachable.
        _inner.Record(rejection);

        // The authenticated tier writes every occurrence; the anonymous tier writes only
        // the crossing. RecordAndCheckCrossing is called ONLY on the anonymous branch —
        // feeding authenticated mismatches into the same counter would let a token holder
        // consume the anonymous budget, and the two tiers are separated precisely because
        // one of them is bounded and the other is not.
        var operation = rejection.IsAuthenticated
            ? RejectOperation
            : _bursts.RecordAndCheckCrossing(rejection.ResolvedTenantId, rejection.Dimension)
                ? BurstOperation
                : null;

        if (operation is null)
        {
            return;
        }

        await WriteAsync(operation, rejection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void RecordUnresolved(TenantAssertionDimension dimension) =>
        // Counted, never recorded, and there is nothing to add here. `audit_log` is
        // tenant-owned; with no resolved tenant the only way to write a row is to invent
        // one, and a sentinel tenant is an unauthenticated, unbounded write target no
        // tenant admin watches. ADR-0036 states the absence as the rule rather than as a
        // gap, which is why this override exists only to say so.
        _inner.RecordUnresolved(dimension);

    private async Task WriteAsync(
        string operation, TenantAssertionRejection rejection, CancellationToken cancellationToken)
    {
        if (!_catalog.TryGetOffPath(operation, out var declared))
        {
            // A deployment defect, and still not a reason to change the response — the
            // response is already a refusal. Critical because the detector this file
            // exists to be is silently off.
            LogUndeclared(_logger, operation, null);
            return;
        }

        var draft = new AuditEntryDraft
        {
            Id = AuditEntryId.From(_guidFactory.NewUuidV7()),

            // The RESOLVED tenant, always. `audit_log` is tenant-owned and the standalone
            // write announces app.tenant_id from this draft — so writing the ASSERTED id
            // would set that GUC to an attacker-chosen value and hand an anonymous caller
            // a primitive that writes rows into a tenant of its choosing. The resolved
            // tenant is also the one whose boundary was defended and whose admin needs to
            // see the event, and WITH CHECK is then satisfied by construction.
            TenantId = TenantId.From(rejection.ResolvedTenantId),

            // Tenant-wide, not organization-scoped. The finding is about the tenant
            // boundary, and a tenant-wide row is the one a tenant-scoped read sees without
            // announcing an organization — an org-scoped row would be invisible to exactly
            // the reader this event is for.
            OrganizationId = null,

            // No principal exists in this process until Phase 02b. `IsAuthenticated` is
            // the tier, not an actor, and inventing one would put a fabricated identity on
            // an append-only table.
            ActorUserId = null,
            ActorEmail = null,

            ModuleName = declared.ModuleName,
            Operation = declared.Operation,
            OperationType = declared.OperationType,
            OperationClass = declared.OperationClass,

            // No aggregate. The event is about a request, not about a row — and the
            // catalogue declares no entity type for either slug.
            EntityType = null,
            EntityId = null,

            // Denied, and it is the outcome the response already gives: the assertion was
            // refused. `failed` would say the platform could not answer.
            Outcome = AuditOutcome.Denied,
            ErrorKey = null,
            Reason = null,

            BeforeState = null,
            AfterState = null,
            Changes = null,

            // Nothing correlates this to a trace yet: the recorder is reached from
            // middleware that runs before the MediatR pipeline, and ITenantContext's
            // correlation id is the pipeline's. Phase 02b's authenticated tier is where a
            // principal and a correlation both become available.
            CorrelationId = null,

            // NEVER the source IP and never the effective host. Both are attacker-chosen,
            // and ADR-0036 keeps them out of the metric labels for the same reason.
            IpAddress = null,
            UserAgent = null,

            Timestamp = _clock.UtcNow,
            Metadata = Metadata(operation, rejection),
        };

        try
        {
            await _store.WriteStandaloneAsync(draft, cancellationToken).ConfigureAwait(false);
        }
        catch (AuditWriteFailedException)
        {
            // Swallowed on purpose — see the class remarks. The store has already logged
            // Critical, counted the failure and taken the health check unhealthy.
        }
    }

    /// <summary>
    /// The asserted value, as the row's <c>metadata</c> document.
    /// </summary>
    /// <remarks>
    /// A <see cref="Guid"/>, so it is an opaque identifier and never attacker-authored
    /// free text. It goes in <c>metadata</c> rather than in <c>tenant_id</c> for the
    /// reason the tenant column gives, and the key names the dimension it came from so a
    /// reader does not have to consult a second column to know which header lied.
    /// </remarks>
    private static string Metadata(string operation, TenantAssertionRejection rejection)
    {
        var key = rejection.Dimension == TenantAssertionDimension.Tenant
            ? "assertedTenantId"
            : "assertedOrganizationId";

        return AuditJson.CapObject(
            "{\"dimension\":" + AuditJson.Quote(rejection.Dimension.ToString())
            + ",\"" + key + "\":" + AuditJson.Quote(
                rejection.AssertedValue.ToString(null, CultureInfo.InvariantCulture))
            + ",\"authenticated\":" + (rejection.IsAuthenticated ? "true" : "false")
            + ",\"event\":" + AuditJson.Quote(operation) + "}");
    }

    private static readonly Action<ILogger, string, Exception?> LogUndeclared =
        LoggerMessage.Define<string>(
            LogLevel.Critical,
            new EventId(4002, nameof(LogUndeclared)),
            "'{Operation}' is not in the audit catalogue, so a rejected tenant assertion went unrecorded. The response is unchanged — a refusal stays a refusal — but the cross-tenant detector is off (ADR-0036 § Recording a rejected assertion).");
}
