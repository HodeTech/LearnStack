using System.Runtime.ExceptionServices;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.Logging;

namespace LearnStack.Application.Audit;

/// <summary>
/// The shared audit execution lifecycle for MediatR requests and direct transaction
/// owners: classify, declare, record each invocation's result, then reconcile once at
/// the outermost audit frame.
/// </summary>
/// <remarks>
/// <para>
/// Scoped with <see cref="IAuditStateCapture"/>. The capture's existing frame stack is
/// the ownership authority; another DI scope has its own capture even when it runs
/// inside the same async flow. Producer invocation frames and transaction ownership
/// remain separate from this audit lifecycle (ADR-0057).
/// </para>
/// <para>
/// The transaction owner writes pending MUST rows and reports the commit boundary.
/// This coordinator preserves ADR-0033's reconciliation, classification and outcome
/// rules; it introduces no additional writer or transaction.
/// </para>
/// </remarks>
public sealed class AuditExecutionCoordinator(
    IAuditCatalog catalog,
    IAuditConfigService classifier,
    IAuditStateCapture capture,
    IAuditStore store,
    ITenantContext tenantContext,
    IClock clock,
    IGuidFactory guidFactory,
    ILogger<AuditExecutionCoordinator> logger)
{
    /// <summary>
    /// Classifies the declared operation before invoking its deferred work. A direct
    /// owner constructs its handler inside <paramref name="next"/>, after classification
    /// and after its transaction coordinator has opened the unit of work.
    /// </summary>
    public async Task<TResponse> ExecuteAsync<TResponse>(
        Type operationType,
        IProvisionsTenant? provisioning,
        Func<Task<TResponse>> next,
        CancellationToken cancellationToken)
        where TResponse : IResultBase
    {
        ArgumentNullException.ThrowIfNull(operationType);
        ArgumentNullException.ThrowIfNull(next);
        var jobSourceOwner = JobApplicationSourceOwner.Current;

        // Unregistered is the rejection, and it happens BEFORE the handler runs: an
        // operation nobody classified is a deployment defect, and letting it commit while
        // the corpus cannot say whether it should have been audited is the failure the
        // fail-closed rule exists to prevent. `Off` is a different answer — registered and
        // silent — which is why TryGet and not a count of entries tells them apart.
        if (!catalog.TryGet(operationType, out var registration))
        {
            LogUnclassified(logger, tenantContext switch
            {
                EventTenantContext => "delivery",
                JobTenantContext => "job",
                _ => operationType.FullName ?? operationType.Name,
            }, null);

            return Result.FailFor<TResponse>(AuditErrors.UnclassifiedOperation);
        }

        var outermost = !capture.HasOpenFrame;

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
            await DeclareAsync(provisioning, registration, cancellationToken).ConfigureAwait(false);
            jobSourceOwner?.RequireBusinessReady();

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
                    // The capture owns both attribution and frame ownership. Cleanup is
                    // scoped to this execution's capture, so another DI scope on the same
                    // async flow cannot suppress its own reconciliation.
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
    /// does neither in a way this coordinator's <c>catch</c> can see. What separates them is
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
                LogReconcileRowRefused(logger, intent.Operation, DiagnosticException(refused));

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
                LogReconcileRowRefused(logger, intent.Operation, DiagnosticException(refused));
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
                LogReconcileRowLost(logger, intent.Operation, DiagnosticException(lost));
            }
#pragma warning restore CA1031
        }
    }

    // Delivery diagnostics use the declared operation and fixed event code only.
    // A nested command shares this context, so its failure cannot leak payloads
    // through a provider/composer exception before the transport sees the fault.
    private Exception? DiagnosticException(Exception failure) =>
        AsyncExecutionDiagnostics.IsTrusted(tenantContext) ? null : failure;

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
        IProvisionsTenant? provisioning, AuditRegistration registration, CancellationToken cancellationToken)
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
        else if (provisioning is not null)
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
                clock.UtcNow)
            {
                CausationId = tenantContext switch
                {
                    EventTenantContext delivery => delivery.EventId,
                    JobTenantContext job => job.IncomingEventId,
                    _ => null,
                },
                CausalActorUserId = tenantContext is EventTenantContext or JobTenantContext ? tenantContext.CausalActorUserId : null,
                JobId = (tenantContext as JobTenantContext)?.JobId,
                ParentJobId = (tenantContext as JobTenantContext)?.ParentJobId,
            });
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
        "forbidden" or "resource_scope_violation" or "feature_disabled" or "limit_exceeded" => AuditOutcome.Denied,
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
