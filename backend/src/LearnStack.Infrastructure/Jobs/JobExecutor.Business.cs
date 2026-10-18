using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Infrastructure.Jobs;

public sealed partial class JobExecutor
{
    internal async Task<JobBusinessExecutionResult> ExecuteBusinessAsync(ConfirmedStartReceipt receipt,
        BackgroundJobCommitGate gate, CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(gate);
        var entryRefused = !gate.TryStartExecution();
        if (entryRefused && !gate.IsClosed)
            throw new InvalidOperationException("A job commit gate belongs to exactly one execution.");

        var telemetry = Observation;
        var stageStarted = telemetry.StartStage();
        var previous = accessor.Current;
        var previousActivity = Activity.Current;
        AsyncServiceScope? scope = null;
        IUnitOfWork? unit = null;
        IDisposable? authority = null;
        IDisposable? sourceBinding = null;
        Activity? activity = null;
        IResultBase? response = null;
        Exception? failure = null;
        var cleanupFailed = false;
        JobExternalReadResult? finalizationRead = null;
        var retained = new List<JobStartCleanupOwnership>();
        // The callback changes one memory gate. Runtime owns private cancellation and its actual callbacks.
        var registration = cancellationToken.Register(CloseBusinessGate, gate);
        try
        {
            if (entryRefused)
                throw new OperationCanceledException("Job root issuance is closed.", cancellationToken);
            if (receipt.Stage == BackgroundJobExecutionStage.Finalize)
            {
                // A selected outcome is read by its separate actual app owner,
                // whose cleanup must finish before this ordinary scope starts.
                finalizationRead = await ReadExternalStateAsync(receipt, cancellationToken).ConfigureAwait(false);
                cleanupFailed |= finalizationRead.CleanupFailed;
                retained.AddRange(finalizationRead.RetainedCleanup);
                if (finalizationRead.Handle is null || cleanupFailed)
                    throw new InvalidOperationException("Finalization requires its acknowledged, cleaned app read.");
            }
            sourceBinding = receipt.SourceOwner.Enter();
            receipt.SourceOwner.RequireFreshScopeReady();
            cancellationToken.ThrowIfCancellationRequested();
            var context = CreateContext(receipt.Envelope);
            accessor.Current = context;
            _ = ActivityContext.TryParse(receipt.Envelope.Traceparent, null, isRemote: true, out var parent);
            activity = new Activity("job.execution").SetIdFormat(ActivityIdFormat.W3C)
                .SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
            activity.Start();
            scope = scopes.CreateAsyncScope();
            unit = scope.Value.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var completion = scope.Value.ServiceProvider.GetRequiredService<BackgroundJobCompletionParticipant>();
            var selected = receipt.Stage == BackgroundJobExecutionStage.Finalize
                ? (completion as PostgresBackgroundJobCompletion
                    ?? throw new InvalidOperationException("Finalization requires its closed completion owner."))
                    .BindFinalizationRead(receipt, finalizationRead!.Handle!)
                : null;
            completion.Arm(receipt, gate);
            authority = admission.Enter(receipt, unit, completion);
            var definitions = scope.Value.ServiceProvider.GetRequiredService<BackgroundJobDefinitionRegistry>();
            var definition = receipt.Stage is BackgroundJobExecutionStage.Prepare or BackgroundJobExecutionStage.Finalize
                ? BackgroundJobEnvelopeSql.ExternalDefinition(receipt.Envelope, definitions)
                : BackgroundJobEnvelopeSql.Definition(receipt.Envelope, definitions);
            if (definition is null || definition.Readiness != BackgroundJobDefinitionReadiness.Active
                || receipt.Stage == BackgroundJobExecutionStage.Prepare && definition.Profile != BackgroundJobExecutionProfile.External)
                throw new InvalidOperationException("The admitted definition is no longer invokable.");
            var parameters = BackgroundJobPayloadContract.Read(receipt.Envelope.Parameters, definition.ParameterType,
                BackgroundJobPayloadKind.Parameters);
            BackgroundJobExecutionAdmission.RequireContext(context);
            object request;
            if (selected is not null)
            {
                var profile = definition.ProfileRegistration;
                var finalizationType = typeof(BackgroundJobFinalization<,>).MakeGenericType(profile.PlanType!, profile.OutcomeType!);
                var plan = BackgroundJobPayloadContract.Read(selected.PlanJson!, profile.PlanType!, BackgroundJobPayloadKind.Plan);
                var outcome = selected.OutcomeJson is { } json
                    ? BackgroundJobPayloadContract.Read(json, profile.OutcomeType!, BackgroundJobPayloadKind.Outcome) : null;
                var finalization = Activator.CreateInstance(finalizationType,
                    [plan, outcome, selected.FinalizationStatus!.Value, selected.FinalizationReason!.Value])
                    ?? throw new InvalidOperationException("The registered finalization data constructor is unavailable.");
                var constructor = profile.FinalizeRequestType!.GetConstructor([definition.ParameterType, finalizationType])
                    ?? throw new InvalidOperationException("The registered finalization root constructor is unavailable.");
                request = constructor.Invoke([parameters, finalization]);
            }
            else
            {
                var constructor = definition.ProfileRegistration.RootRequestType.GetConstructor([definition.ParameterType])
                    ?? throw new InvalidOperationException("The registered root constructor is unavailable.");
                request = constructor.Invoke([parameters]);
            }
            if (receipt.Stage is BackgroundJobExecutionStage.Transactional or BackgroundJobExecutionStage.Finalize
                    && request is not IRequest<Result<None>>
                || receipt.Stage is not (BackgroundJobExecutionStage.Transactional or BackgroundJobExecutionStage.Prepare
                    or BackgroundJobExecutionStage.Finalize))
                throw new InvalidOperationException("The registered root has the wrong stage/result contract.");
            // Both parameter and root constructors run under sticky ordinary authority.
            BackgroundJobExecutionAdmission.RequireContext(context);
            receipt.SourceOwner.RequireBusinessReady();
            cancellationToken.ThrowIfCancellationRequested();
            response = await scope.Value.ServiceProvider.GetRequiredService<ISender>()
                .Send(request, cancellationToken).ConfigureAwait(false) as IResultBase
                ?? throw new InvalidOperationException("The registered root returned no Result contract.");
        }
        catch (Exception caught)
        {
            failure = caught;
            if (!entryRefused && gate.CommitAcknowledged) cleanupFailed = true;
        }
        finally
        {
            if (scope is { } owned)
                cleanupFailed |= !await CleanupScopeAsync(owned, unit, null, retained).ConfigureAwait(false);
            try { activity?.Dispose(); }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(null, null, null, activity, null));
            }
            try
            {
                Activity.Current = previousActivity;
                accessor.Current = previous;
            }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(scope, unit, null, activity, authority, sourceBinding));
            }
            try { authority?.Dispose(); }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(null, null, null, null, authority));
            }
            try { sourceBinding?.Dispose(); }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(null, null, null, null, null, sourceBinding));
            }
            // Old inherited bindings close before the auxiliary snapshot. The
            // same clean owner may serve a strictly sequential capacity checkpoint.
            if (receipt.Stage == BackgroundJobExecutionStage.Finalize || receipt.Stage != BackgroundJobExecutionStage.Prepare
                && (receipt.Envelope.CapacityGroup is null || gate.CommitAcknowledged) || cleanupFailed)
                receipt.SourceOwner.CloseIssuance();
            cleanupFailed |= !await receipt.SourceOwner.JoinAuxiliaryCleanupAsync().ConfigureAwait(false);
            if (cleanupFailed) receipt.SourceOwner.CloseIssuance();
            try { registration.Dispose(); }
            catch (Exception caught) when (BackgroundJobControlResources.Catchable(caught))
            {
                cleanupFailed = true;
                receipt.SourceOwner.CloseIssuance();
                retained.Add(new(null, null, null, null, null, registration: registration));
            }
        }

        var commitStarted = !entryRefused && gate.CommitStarted;
        var acknowledged = !entryRefused && gate.CommitAcknowledged;
        var uncertain = commitStarted && !acknowledged && !gate.KnownRollback;
        var knownRollback = !entryRefused && (gate.KnownRollback || !commitStarted && !acknowledged && !cleanupFailed);
        var decision = BackgroundJobFailurePolicy.Classify(response, failure, acknowledged, uncertain,
            cancellationToken.IsCancellationRequested || gate.IsClosed);
        var observed = acknowledged ? JobObservedOutcome.Acknowledged
            : uncertain ? JobObservedOutcome.UnknownCommit
            : entryRefused ? JobObservedOutcome.Refused
            : decision.Disposition switch
            {
                BackgroundJobFailureDisposition.Retry => JobObservedOutcome.Retry,
                BackgroundJobFailureDisposition.Terminal => JobObservedOutcome.Terminal,
                _ => JobObservedOutcome.Retained,
            };
        telemetry.RecordStage(stageStarted, receipt.Envelope.DefinitionKey, receipt.Envelope.Profile,
            BackgroundJobObservation.BusinessStage(receipt.Stage), observed, cleanupFailed, decision.ErrorCode);
        return new(decision, acknowledged, commitStarted, knownRollback, uncertain, cleanupFailed,
            receipt, gate, !entryRefused, retained.AsReadOnly(), finalizationRead);
    }

    private static void CloseBusinessGate(object? state) => ((BackgroundJobCommitGate)state!).Close();
}
