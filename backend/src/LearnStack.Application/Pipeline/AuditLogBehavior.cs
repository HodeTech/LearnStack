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

        // A frame per invocation, outermost or nested, so each intent belongs to the
        // request that declared it and each captured change to the request whose handler
        // wrote it (ADR-0044 Amendment 6 §§ 1, 3).
        capture.OpenFrame();

        // Thrown until the handler RETURNS: a behavior, the handler, a cancellation and a
        // faulted COMMIT all leave by an exception, and every one of them is a result the
        // row must not call a success.
        var result = AuditIntentResult.Thrown;

        try
        {
            await DeclareAsync(request, registration, cancellationToken).ConfigureAwait(false);

            var response = await next().ConfigureAwait(false);

            // Recorded rather than acted on. Every exit reconciles in one place below, so a
            // refusal, an exception and a cancellation cannot drift apart — and the
            // cancelled COMMIT, which reaches neither the catch nor this line, is covered
            // by the same code as the other two (ADR-0033 Amendment 4 § 2).
            result = response.IsFailure
                ? AuditIntentResult.Refused(OutcomeOf(response.Error!), response.Error!.Message.Key)
                : AuditIntentResult.Succeeded;

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
            // EVERY frame records what its own request returned, on the intents it declared.
            // Only the outermost frame used to keep an outcome at all, in a local, so an
            // inner refusal an outer handler absorbed was written by the owner as success
            // (ADR-0044 Amendment 6 § 3).
            capture.CloseFrame(result);

            // Only the OUTERMOST frame reconciles and clears. A joiner that cleared would
            // erase the outer request's intents and every snapshot before the owner had
            // committed (ADR-0033 Amendment 2 § 2).
            if (outermost)
            {
                try
                {
                    await ReconcileAsync().ConfigureAwait(false);
                }
                finally
                {
                    // Unconditional. A reconcile that threw used to skip both lines, which
                    // left the flow believing a frame was still open — so the NEXT request
                    // in it would never be outermost, never reconcile and never clear — and
                    // left this request's intents and snapshots in the scope's buffer.
                    //
                    // Not independently observable now, and better said than implied: the
                    // reconcile guards every intent, so it no longer throws, and the two
                    // cases that pin the cleanup — a store failing before it writes, a
                    // composer refusal — pass with or without this finally. It stays so the
                    // cleanup does not rest on the reconcile's own catch staying complete.
                    AuditFrame.Exit();
                    capture.Clear();
                }
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
    /// database, and each intent's own <b>result</b>, which is a fact about its request —
    /// <see cref="AuditDraftComposer.Reconciled"/> holds the rule that combines them.
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
    /// <para>
    /// <b>It never throws, and one row's failure never costs another its attempt.</b> This
    /// runs from a <c>finally</c>, where an exception replaces whatever the request was
    /// leaving with. Composition and the write are each guarded, per intent: a composer
    /// refusal and a store that failed before issuing a statement both escaped the narrower
    /// catch this had, replaced a <c>403</c> with a <c>500</c>, and skipped every intent
    /// after them. Everything reaching here is already failing, refused or in doubt, so
    /// ADR-0033 Amendment 1's rule — a standalone failure changes the response only for an
    /// operation that would otherwise have succeeded — leaves the response alone.
    /// </para>
    /// <para>
    /// <b>One alert per failure, from whoever saw it.</b> The store reports a failed write
    /// itself (IAuditStore), so that path logs a <c>Warning</c> saying the caller's status
    /// stands; a composer refusal and a draft the store refused before writing are programmer
    /// errors nothing else reports, and those log <c>Critical</c> here.
    /// </para>
    /// </remarks>
    private async Task ReconcileAsync()
    {
        var state = capture.State;

        foreach (var intent in capture.Intents)
        {
            var durable = intent.OperationClass == OperationClass.Must
                && state == AuditIntentState.Committed;

            if (durable)
            {
                continue;
            }

            AuditEntryDraft draft;

#pragma warning disable CA1031 // A finally that threw would replace the exception or refusal the caller is meant to see.
            try
            {
                // The SAME composer the in-transaction write uses. A private one here is how
                // the two diverged: only the happy-path row carried a snapshot, so every
                // denied, failed and indeterminate row said nothing about what was attempted
                // — which is the class of row ADR-0033 calls the common case it protects.
                //
                // A FRESH clock reading, not the intent's DeclaredAt: the in-transaction row
                // that may already exist under this id carries that, and the composite
                // primary key is what makes the pair legal rather than a 23505.
                draft = AuditDraftComposer.Reconciled(
                    intent, capture.ChangesOf(intent), state, clock.UtcNow);
            }
            catch (Exception refused)
            {
                // Critical, and swallowed. A composer refusal — two instances of the declared
                // aggregate and none designated — is a programmer error that nothing else
                // reports, and it must not cost the next intent its attempt nor replace the
                // caller's outcome.
                LogReconcileRowRefused(logger, intent.Operation, refused);

                continue;
            }

            try
            {
                // The class decides the posture, not the outcome. A MUST row that cannot
                // be written is a failure worth shouting about; a SHOULD row that cannot is
                // an accepted loss the module's matrix already records.
                // CancellationToken.None, and it is the whole point. The paths this
                // reconcile exists for are the ones where the request's token is ALREADY
                // cancelled — a client that disconnected mid-COMMIT above all — so handing
                // that token to the write abandons the row at
                // OpenConnectionAsync, before a statement is issued. Measured: one row
                // with a live token, zero with a cancelled one, and the cancelled case is
                // the only one this code path exists for.
                //
                // TransactionBehavior applies the same rule one behavior in, passing
                // CancellationToken.None to scope.FailAsync: the cleanup must never be
                // abandoned by what it is cleaning up after. Unbounded only in the sense
                // that this token is — Npgsql's own connection and command timeouts still
                // bound the write, so a wedged database cannot hold the request forever.
                if (intent.OperationClass == OperationClass.Must)
                {
                    await store.WriteStandaloneAsync(draft, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await store.WriteBestEffortAsync(draft, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (ArgumentException refused)
            {
                // A draft the store refused before any write — the platform sentinel — is a
                // caller error the store does not report (IAuditStore), so this is its alert.
                LogReconcileRowRefused(logger, intent.Operation, refused);
            }
            catch (Exception lost)
            {
                // Swallowed, and NOT a second Critical. The store reports every failure of the
                // write itself — Critical, counted, the `audit` health check unhealthy
                // (IAuditStore) — and best effort logs and drops its own; the Critical this
                // used to add was one alert twice (the fifth review of Packet 9). What this
                // line adds is the one fact the store cannot know: ADR-0033 Amendment 1
                // changes the response only when the operation would otherwise have
                // SUCCEEDED, and everything reaching here is already failing, refused or in
                // doubt, so the caller keeps its status.
                LogReconcileRowLost(logger, intent.Operation, lost);
            }
#pragma warning restore CA1031
        }
    }

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

                // Read HERE, with the tenant, and carried to both writers on the intent. The
                // in-transaction write had no way to reach them and wrote both columns NULL
                // on every successful row (ADR-0044 Amendment 6 § 2). The principal the
                // request carried, or null — never a substituted SystemActor.
                tenantContext.UserId,
                tenantContext.CorrelationId,
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
            LogLevel.Warning,
            new EventId(2, nameof(LogReconcileRowLost)),
            "The reconcile row for {Operation} could not be written, and the audit store has reported the failure. The caller keeps whatever status it already had: ADR-0033 Amendment 1 changes the response only for a write that would otherwise have SUCCEEDED, and turning a refusal into a 503 an anonymous caller can provoke tells them more, not less.");

    private static readonly Action<ILogger, string, Exception?> LogReconcileRowRefused =
        LoggerMessage.Define<string>(
            LogLevel.Critical,
            new EventId(3, nameof(LogReconcileRowRefused)),
            "The reconcile row for {Operation} was refused before any write — a programmer error no audit store reports, such as two instances of the declared aggregate with none designated. The row is not on the record, and the caller keeps whatever status it already had.");
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
