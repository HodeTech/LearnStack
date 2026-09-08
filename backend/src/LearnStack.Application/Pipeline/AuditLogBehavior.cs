using System.Runtime.ExceptionServices;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.Logging;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// MediatR pipeline behavior — step 3 of the canonical eight-step order
/// (<see href="../../../../docs/decisions/0032-exception-handling-logging-and-observability.md">ADR-0032
/// § Sub-decision 2</see>). It classifies the request, parks one intent per audited
/// <c>(resource, operation)</c>, and reports the outcome.
/// </summary>
/// <remarks>
/// <para>
/// <b>It writes nothing on the success path.</b> A MUST-class row belongs on the business
/// transaction, immediately before <c>COMMIT</c>, and <c>TransactionBehavior</c> — which
/// owns that boundary — is what flushes it
/// (<see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033</see>).
/// What this behavior owns is the classification, the intents, and every path where there
/// is no business transaction to ride: a refusal, an exception, and a SHOULD or MAY row.
/// </para>
/// <para>
/// <b>Two guarantees the Packet 3 shell established and this must not lose:</b> the
/// rethrow goes through <see cref="ExceptionDispatchInfo"/>, so handlers and the L1
/// boundary see the original stack; and the wrap order is AuditLog outside TenantContext,
/// Authorization, Transaction, OutboxFlush and the handler, which
/// <c>MediatR_Pipeline_Order_Matches_Canonical_Sequence</c> asserts. Being outside the
/// transaction is what lets the reconcile step run <em>after</em> the commit resolved.
/// </para>
/// <para>
/// <b>It stays in <c>LearnStack.Application</c>.</b> The Packet 3 remark said it would
/// move to <c>LearnStack.Infrastructure.Audit</c>; ADR-0044 § 11 settles the other way,
/// because that move inverts the Application → Infrastructure dependency
/// <c>MediatRPipelineRegistration.CanonicalBehaviorOrder</c> and its architecture test
/// depend on, and that project's csproj forbids it besides. Only the ports it names moved.
/// </para>
/// </remarks>
public sealed class AuditLogBehavior<TRequest, TResponse>(
    IAuditCatalog catalog,
    IAuditConfigService classifier,
    IAuditStateCapture capture,
    IAuditStore store,
    ITenantContext tenantContext,
    IClock clock,
    IGuidFactory guidFactory,
    ILogger<AuditLogBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResultBase
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        // Unregistered is the rejection, and it happens BEFORE the handler runs: an
        // operation nobody classified is a deployment defect, and letting it commit while
        // the corpus cannot say whether it should have been audited is the failure the
        // fail-closed rule exists to prevent. `Off` is a different answer — registered and
        // silent — which is why TryGet and not a count of entries tells them apart.
        if (!catalog.TryGet(typeof(TRequest), out var registration))
        {
            LogUnclassified(logger, typeof(TRequest).FullName ?? typeof(TRequest).Name, null);

            return Result.FailFor<TResponse>(AuditErrors.UnclassifiedOperation);
        }

        var outermost = AuditFrame.Enter();
        Error? refusal = null;

        try
        {
            await DeclareAsync(request, registration, cancellationToken).ConfigureAwait(false);

            var response = await next().ConfigureAwait(false);

            // Noted rather than acted on. Every exit reconciles in one place below, so a
            // refusal, an exception and a cancellation cannot drift apart — and the
            // cancelled COMMIT, which reaches neither the catch nor this line, is covered
            // by the same code as the other two (ADR-0033 Amendment 4 § 2).
            if (response.IsFailure)
            {
                refusal = response.Error;
            }

            return response;
        }
#pragma warning disable CA1031 // ADR-0032 § Sub-decision 2 binds the audit-then-rethrow contract here.
        // Cancellation is excluded deliberately, and the exclusion no longer costs a row:
        // the reconcile moved into the finally, which a cancellation does run.
        catch (Exception failure) when (failure is not OperationCanceledException)
#pragma warning restore CA1031
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw; // unreachable; the line above is the rethrow.
        }
        finally
        {
            // Only the OUTERMOST frame reconciles and clears. A joiner that cleared would
            // erase the outer request's intents and every snapshot before the owner had
            // committed (ADR-0033 Amendment 2 § 2).
            if (outermost)
            {
                await ReconcileAsync(refusal, cancellationToken).ConfigureAwait(false);

                AuditFrame.Exit();
                capture.Clear();
            }
        }
    }

    /// <summary>
    /// Writes whatever the business transaction did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One place for every exit, because the exits are not distinguishable from inside the
    /// pipeline: a refusal returns, an exception throws, and a cancelled <c>COMMIT</c>
    /// does neither in a way this behavior's <c>catch</c> can see. What separates them is
    /// the <b>state</b> the owning unit-of-work frame recorded, which is a fact about the
    /// database rather than about the control flow.
    /// </para>
    /// <para>
    /// <b>A MUST intent is re-written only when it is not already durable.</b>
    /// <see cref="AuditIntentState.Committed"/> means the row went with the business
    /// write; anything else means it did not, and the attempt is still the record.
    /// <see cref="AuditIntentState.Indeterminate"/> prefers a duplicate to a loss — the
    /// row goes out anyway, and the <c>23505</c> that may follow is positive evidence the
    /// first one landed.
    /// </para>
    /// <para>
    /// <b>A SHOULD or MAY intent is never in-transaction.</b>
    /// <c>WritePendingAsync</c> flushes MUST alone, so these are written here, best effort,
    /// with the outcome the request actually had.
    /// </para>
    /// </remarks>
    private async Task ReconcileAsync(Error? refusal, CancellationToken cancellationToken)
    {
        if (capture.Intents.Count == 0)
        {
            return;
        }

        var state = capture.State;

        foreach (var intent in capture.Intents)
        {
            var durable = intent.OperationClass == OperationClass.Must
                && state == AuditIntentState.Committed;

            if (durable)
            {
                continue;
            }

            var outcome = Outcome(state, refusal);
            var draft = Compose(intent, outcome, refusal);

            try
            {
                // The class decides the posture, not the outcome. A MUST row that cannot
                // be written is a failure worth shouting about; a SHOULD row that cannot is
                // an accepted loss the module's matrix already records.
                if (intent.OperationClass == OperationClass.Must)
                {
                    await store.WriteStandaloneAsync(draft, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await store.WriteBestEffortAsync(draft, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (AuditWriteFailedException lost)
            {
                // Critical, and swallowed. ADR-0033 Amendment 1: a standalone write failure
                // changes the response only when the operation would otherwise have
                // SUCCEEDED. Everything reaching here is already failing, being refused, or
                // in doubt — and turning a refusal into a 503 an anonymous caller can
                // provoke tells them more, not less.
                LogReconcileRowLost(logger, intent.Operation, lost);
            }
            catch (OperationCanceledException lost)
            {
                // The request's token is already cancelled — which is precisely the case
                // this reconcile exists for — so the write cannot use it and must not be
                // abandoned by it either. Logged rather than rethrown: a finally that threw
                // would replace the exception the caller is meant to see.
                LogReconcileRowLost(logger, intent.Operation, lost);
            }
        }
    }

    /// <summary>
    /// The outcome a re-written row carries.
    /// </summary>
    /// <remarks>
    /// <see cref="AuditIntentState.Indeterminate"/> wins over a refusal: when the
    /// <c>COMMIT</c>'s fate is unknown the row's honest content is that it is unknown, and
    /// a reader filtering for <c>indeterminate</c> is asking a question about the database
    /// rather than about the caller.
    /// </remarks>
    private static AuditOutcome Outcome(AuditIntentState state, Error? refusal) => state switch
    {
        AuditIntentState.Indeterminate => AuditOutcome.Indeterminate,
        _ when refusal is not null => OutcomeOf(refusal),
        AuditIntentState.Committed => AuditOutcome.Success,
        AuditIntentState.RolledBack => AuditOutcome.Failed,
        _ => AuditOutcome.Failed,
    };

    /// <summary>
    /// Parks one intent per audited <c>(resource, operation)</c>.
    /// </summary>
    /// <remarks>
    /// Plural, and that is the correction ADR-0033 Amendment 2 § 1 makes:
    /// <c>ProvisionTenantCommand</c> writes two aggregate roots on one transaction and the
    /// Tenancy matrix classifies both MUST, so a singular reading would simply never write
    /// the <c>Organization</c> row it promises.
    /// </remarks>
    private async Task DeclareAsync(
        TRequest request, AuditRegistration registration, CancellationToken cancellationToken)
    {
        if (registration.WritesNoRow)
        {
            return;
        }

        // ADR-0044 § 2's four cases, decided HERE because step 3 is the only place all
        // four are decidable — and because the store must never resolve a tenant of its
        // own. The row's tenant is the one the transaction ANNOUNCES, which is the only
        // value its own WITH CHECK accepts.
        TenantId? tenantId;
        OrganizationId? organizationId;

        if (tenantContext.IsResolved)
        {
            tenantId = tenantContext.TenantId;
            organizationId = tenantContext.OrganizationId;
        }
        else if (request is IProvisionsTenant provisioning)
        {
            // The tenant does not exist yet, so the context cannot resolve it — and the
            // transaction announces exactly this value through
            // SetProvisioningTenantContextAsync. Reading the context instead gives the
            // all-zero tenant, and the row is then refused by the policy it was written
            // under. Measured: `make seed` failed here with 42501 before this branch
            // existed, on the one command ADR-0042 sanctions.
            tenantId = provisioning.ProvisioningTenantId;

            // NULL, and not by choice: SetProvisioningTenantContextAsync announces
            // app.organization_id as the empty string, so any other value fails the
            // org-scoped WITH CHECK.
            organizationId = null;
        }
        else
        {
            // The fourth case: no row at all. There is no tenant whose admin could read
            // it, and ADR-0036 forbids inventing one.
            return;
        }

        foreach (var entry in registration.Entries)
        {
            var classification = await classifier
                .ClassifyAsync(tenantId, entry, cancellationToken)
                .ConfigureAwait(false);

            if (classification == AuditClassification.Off)
            {
                continue;
            }

            capture.DeclareIntent(new AuditIntent(
                AuditEntryId.From(guidFactory.NewUuidV7()),
                tenantId.Value,
                organizationId,
                entry.ModuleName,
                entry.Operation,
                entry.OperationType,
                // The DECLARED tier, always. An override narrows to Off or leaves the tier
                // alone (ADR-0033 Amendment 4 § 1), so a non-Off classification is the
                // declared one and the intent and the catalogue cannot disagree.
                entry.OperationClass,
                entry.EntityType,
                clock.UtcNow));
        }
    }

    private AuditEntryDraft Compose(AuditIntent intent, AuditOutcome outcome, Error? error) =>
        new()
        {
            Id = intent.Id,
            TenantId = intent.TenantId,
            OrganizationId = intent.OrganizationId,
            ActorUserId = tenantContext.UserId,
            ActorEmail = null,
            ModuleName = intent.ModuleName,
            Operation = intent.Operation,
            OperationType = intent.OperationType,
            OperationClass = intent.OperationClass,
            EntityType = intent.EntityType?.Name,
            EntityId = null,
            Outcome = outcome,
            ErrorKey = error?.Message.Key,
            Reason = null,
            BeforeState = null,
            AfterState = null,
            Changes = null,
            CorrelationId = tenantContext.CorrelationId,
            IpAddress = null,
            UserAgent = null,
            // A FRESH reading, not the intent's DeclaredAt. The in-transaction row that
            // may already exist under this id carries DeclaredAt, and the composite
            // primary key is what makes the pair legal rather than a 23505.
            Timestamp = clock.UtcNow,
            Metadata = null,
        };

    /// <summary>
    /// Denied for a refusal the authorization layer would answer 403 to, Failed otherwise.
    /// </summary>
    /// <remarks>
    /// Keyed on the three codes <c>HttpStatusMap</c> answers <c>403</c> to, so the row's
    /// outcome and the response's status cannot disagree. A validation failure is
    /// <c>Failed</c>: it is a malformed request rather than a refused one, and counting it
    /// as a denial would bury the probes the <c>denied</c> class exists to surface.
    /// </remarks>
    private static AuditOutcome OutcomeOf(Error error) => error.Code switch
    {
        "forbidden" or "resource_scope_violation" or "feature_disabled" => AuditOutcome.Denied,
        _ => AuditOutcome.Failed,
    };

    private static readonly Action<ILogger, string, Exception?> LogUnclassified =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, nameof(LogUnclassified)),
            "{RequestType} reached the audit behavior with no catalogue registration and was refused. Every request is registered, silence included: declare it in the module's IAuditCatalogSource, with Off if it audits nothing.");

    private static readonly Action<ILogger, string, Exception?> LogReconcileRowLost =
        LoggerMessage.Define<string>(
            LogLevel.Critical,
            new EventId(2, nameof(LogReconcileRowLost)),
            "The reconcile row for {Operation} could not be written. The caller keeps whatever status it already had: ADR-0033 Amendment 1 changes the response only for a write that would otherwise have SUCCEEDED, and turning a refusal into a 503 an anonymous caller can provoke tells them more, not less.");
}

/// <summary>
/// Which <see cref="AuditLogBehavior{TRequest,TResponse}"/> frame is the outermost one.
/// </summary>
/// <remarks>
/// <para>
/// Non-generic on purpose. A static inside the behavior would be per closed generic, so
/// two different request types in one nested flow would each believe they were outermost
/// — and both would clear the buffer, the second one erasing intents the first had not
/// written yet.
/// </para>
/// <para>
/// <see cref="AsyncLocal{T}"/> rather than the capture itself, because the question is
/// about the async flow rather than about the buffer: the buffer is per DI scope and a
/// handler that sends a second request shares it, which is exactly the case this exists to
/// get right.
/// </para>
/// </remarks>
internal static class AuditFrame
{
    private static readonly AsyncLocal<bool> Active = new();

    /// <summary>Marks a frame open. <c>true</c> when this frame is the outermost.</summary>
    public static bool Enter()
    {
        if (Active.Value)
        {
            return false;
        }

        Active.Value = true;

        return true;
    }

    /// <summary>Closes the outermost frame.</summary>
    public static void Exit() => Active.Value = false;
}
