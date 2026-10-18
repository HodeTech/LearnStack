using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Infrastructure.Jobs;

internal enum JobExternalExecutionStatus { EntryRefused, Reported, ProviderFault }

/// <summary>Memory observation and actual cleanup only; no durable checkpoint or quiescence capability.</summary>
internal sealed record JobExternalExecutionResult(JobExternalExecutionStatus Status,
    BackgroundJobRetainedExternalObservation? Observation, bool CleanupFailed)
{
    internal IReadOnlyList<JobStartCleanupOwnership> RetainedCleanup { get; init; } = [];
    internal bool ProviderIssued { get; init; }
}

public sealed partial class JobExecutor
{
    internal Task<JobExternalExecutionResult> ExecuteExternalAsync(JobExternalReadHandle read,
        JobExternalCheckpointResult arm, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(arm);
        if (arm.Arm is null || arm.Outcome is not null)
            throw new ArgumentException("Execute requires its exact original Arm result.", nameof(arm));
        return InvokeExternalAsync(read, arm, null, token);
    }

    internal Task<JobExternalExecutionResult> ReconcileExternalAsync(BackgroundJobExternalRecoveryBinding recovery,
        JobExternalReadHandle read, CancellationToken token) => InvokeExternalAsync(read, null, recovery, token);

    /// <summary>
    /// Actual provider and cleanup task only. The native invocation owns its
    /// private cancellation and finite wait, retaining this task until it really ends.
    /// </summary>
    private async Task<JobExternalExecutionResult> InvokeExternalAsync(JobExternalReadHandle read,
        JobExternalCheckpointResult? armed, BackgroundJobExternalRecoveryBinding? recovery, CancellationToken token)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        if ((armed is null) == (recovery is null)) throw new ArgumentException("Provider entry has one exact origin.");
        var telemetry = Observation;
        var stageStarted = telemetry.StartStage();
        var metricStage = armed is null ? JobObservedStage.Reconcile : JobObservedStage.Execute;
        if (recovery?.Receipt.Purpose == BackgroundJobExternalRecoveryPurpose.TerminalNoStart)
        {
            telemetry.RecordStage(stageStarted, null, "external", metricStage, JobObservedOutcome.Refused, false);
            return new(JobExternalExecutionStatus.EntryRefused, null, false);
        }
        var invocation = new ExternalInvocationObservation();
        var arm = armed?.Arm;
        var original = arm?.Receipt;
        BackgroundJobEnvelope? envelopeForMetrics = original?.Envelope;
        var run = original?.Run ?? recovery!.Run;
        var source = original?.SourceOwner ?? recovery!.SourceOwner;
        var previous = accessor.Current;
        var previousActivity = Activity.Current;
        AsyncServiceScope? scope = null;
        IDisposable? denial = null;
        Activity? activity = null;
        CancellationTokenRegistration? registration = null;
        BackgroundJobRetainedExternalObservation? observation = null;
        var status = JobExternalExecutionStatus.EntryRefused;
        var cleanupFailed = false;
        var armWindowExpired = false;
        var retained = new List<JobStartCleanupOwnership>();
        try
        {
            token.ThrowIfCancellationRequested();
            registration = token.UnsafeRegister(StopExternalRead, run);
            if (run.IsClosed) throw new OperationCanceledException("External provider issuance is closed.", token);
            if (!source.CleanupConfirmed || JobApplicationSourceOwner.Current is not null)
                throw new InvalidOperationException("Provider entry requires its positively clean source outside an application owner.");
            var confirmedArm = arm is not null && arm.Gate.Confirmation?.AllowsOriginalExecute(arm) == true;
            if (armed is not null && (arm is null || !armed.Issued || armed.CleanupFailed || armed.RetainedCleanup.Count != 0
                || !armed.CommitStarted || !armed.CommitAcknowledged && !confirmedArm || armed.KnownRollback
                || !ReferenceEquals(armed.SourceOwner, source) || !ReferenceEquals(arm.Gate.Run, run)
                || !confirmedArm && (!arm.CommitAcknowledged || !arm.Gate.CommitAcknowledged) || !arm.Gate.CommitStarted
                || arm.Gate.KnownRollback || arm.Gate.Kind != BackgroundJobCheckpointKind.Arm))
                throw new InvalidOperationException("Execute requires the exact issued, durably resolved and cleaned Arm command.");
            if (arm is not null && !confirmedArm && !arm.Gate.HasLiveAcknowledgedArm)
            {
                armWindowExpired = true;
                throw new OperationCanceledException("The acknowledged Arm's observed entry window has expired.");
            }
            var snapshot = original is not null ? read.Require(original) : read.Require(recovery!);
            envelopeForMetrics = snapshot.Envelope;
            if (original is not null && (snapshot.PlanJson is null || snapshot.NoStartRecorded || snapshot.OutcomeJson is not null
                || snapshot.OutcomeState is not null || snapshot.FailurePolicy is not null
                || snapshot.PlanFingerprint != arm!.PlanFingerprint || snapshot.OperationId != arm.OperationId))
                throw new InvalidOperationException("Execute requires its original app-read prepared plan.");
            // A pre-Arm read legitimately has no intent yet; that write's actual ACK
            // supplies Execute permission. Recovery instead reads the existing intent.
            var needed = original is not null || snapshot.OutcomeJson is null && snapshot.ExecuteIntentRecorded
                && !snapshot.NoStartRecorded && snapshot.PlanJson is not null;
            if (needed)
            {
                var envelope = snapshot.Envelope!;
                accessor.Current = null;
                denial = BackgroundJobExecutionAdmission.EnterExternal();
                _ = ActivityContext.TryParse(envelope.Traceparent, null, isRemote: true, out var parent);
                activity = new Activity(original is null ? "job.external_reconcile" : "job.external_execute")
                    .SetIdFormat(ActivityIdFormat.W3C).SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
                activity.Start();
                scope = scopes.CreateAsyncScope();
                var registry = scope.Value.ServiceProvider.GetRequiredService<BackgroundJobDefinitionRegistry>();
                if (!registry.TryGet(envelope.DefinitionKey, out var definition)
                    || definition.Readiness != BackgroundJobDefinitionReadiness.Active
                    || definition.Profile != BackgroundJobExecutionProfile.External
                    || definition.Queue != envelope.Queue || definition.CapacityGroup != envelope.CapacityGroup
                    || (definition.Scope == BackgroundJobScope.TenantWide) != (envelope.OrganizationId is null)
                    || !registry.Infrastructure.TryGetAdapter(definition.ProfileRegistration.ExternalPortType!, out var binding))
                    throw new InvalidOperationException("Provider entry requires its frozen registered definition and adapter.");
                var plan = BackgroundJobPayloadContract.Read(snapshot.PlanJson!, definition.ProfileRegistration.PlanType!, BackgroundJobPayloadKind.Plan);
                var canonical = BackgroundJobPayloadContract.Serialize(plan, definition.ProfileRegistration.PlanType!, BackgroundJobPayloadKind.Plan);
                var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
                if (canonical != snapshot.PlanJson || fingerprint != snapshot.PlanFingerprint
                    || BackgroundJobExternalIdentity.OperationId(envelope.JobId, fingerprint) != snapshot.OperationId)
                    throw new InvalidOperationException("Provider entry requires its canonical app-read operation identity.");
                var adapter = scope.Value.ServiceProvider.GetRequiredService(binding.PortType);
                if (adapter.GetType() != binding.ImplementationType)
                    throw new InvalidOperationException("The resolved provider must equal its frozen concrete binding.");
                var context = new BackgroundJobExternalContext(envelope.JobId,
                    original?.AttemptId ?? recovery!.Receipt.AttemptId, snapshot.OperationId!.Value,
                    original?.Deadline ?? recovery!.Receipt.Deadline);
                Task<BackgroundJobRetainedExternalObservation?> call;
                try
                {
                    var method = original is null ? nameof(CallExternalReconcileAsync) : nameof(CallExternalExecuteAsync);
                    call = (Task<BackgroundJobRetainedExternalObservation?>)typeof(JobExecutor)
                        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
                        .MakeGenericMethod(definition.ProfileRegistration.PlanType!, definition.ProfileRegistration.OutcomeType!)
                        .Invoke(null, [adapter, plan, context, fingerprint, original is null ? recovery : arm!.Gate, invocation, token])!;
                }
                catch (TargetInvocationException wrapped) when (wrapped.InnerException is { } failure)
                {
                    ExceptionDispatchInfo.Capture(failure).Throw();
                    throw;
                }
                // Capture occurs in the typed bridge before cleanup, even after stop.
                // Neither a report nor actual completion grants a durable checkpoint.
                observation = await call.ConfigureAwait(false);
                status = observation is null ? JobExternalExecutionStatus.EntryRefused : JobExternalExecutionStatus.Reported;
            }
        }
        catch (OperationCanceledException) when (denial is null && (run.IsClosed || token.IsCancellationRequested || armWindowExpired))
        {
            status = JobExternalExecutionStatus.EntryRefused;
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            // No returned report means no invented outcome, policy or quiescence.
            status = JobExternalExecutionStatus.ProviderFault;
        }
        finally
        {
            if (scope is { } owned)
                cleanupFailed |= !await CleanupScopeAsync(owned, null, null, retained).ConfigureAwait(false);
            try { activity?.Dispose(); }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(null, null, null, activity, null));
            }
            try { Activity.Current = previousActivity; accessor.Current = previous; }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(scope, null, null, activity, denial, registration: registration));
            }
            try { denial?.Dispose(); }
            catch
            {
                cleanupFailed = true;
                retained.Add(new(null, null, null, null, denial));
            }
            if (registration is { } callback)
            {
                try { await callback.DisposeAsync().ConfigureAwait(false); }
                catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
                {
                    cleanupFailed = true;
                    retained.Add(new(null, null, null, null, null, registration: callback));
                }
            }
            if (cleanupFailed) { source.CloseIssuance(); run.Close(); }
        }
        if (token.IsCancellationRequested) run.Close();
        var observed = status == JobExternalExecutionStatus.EntryRefused ? JobObservedOutcome.Refused
            : status == JobExternalExecutionStatus.ProviderFault ? invocation.Issued
                ? JobObservedOutcome.ProviderFault : JobObservedOutcome.UnissuedProviderFault
            : observation?.State switch
            {
                BackgroundJobExternalState.Succeeded => JobObservedOutcome.ProviderSucceeded,
                BackgroundJobExternalState.Failed => JobObservedOutcome.ProviderFailed,
                BackgroundJobExternalState.Cancelled => observation.FailurePolicy == BackgroundJobExternalFailurePolicy.TransientRepeatable
                    ? JobObservedOutcome.ProviderCancelledRepeatable : JobObservedOutcome.ProviderCancelledNotRepeatable,
                BackgroundJobExternalState.Running => JobObservedOutcome.Running,
                BackgroundJobExternalState.NeverStarted => JobObservedOutcome.NeverStarted,
                _ => JobObservedOutcome.UnknownEffect,
            };
        telemetry.RecordStage(stageStarted, envelopeForMetrics?.DefinitionKey, "external", metricStage,
            observed, cleanupFailed, observation?.FailurePolicy?.ToString());
        return new(status, observation, cleanupFailed) { RetainedCleanup = retained.AsReadOnly(), ProviderIssued = invocation.Issued };
    }

    private static async Task<BackgroundJobRetainedExternalObservation?> CallExternalExecuteAsync<TPlan, TOutcome>(
        object adapter, object plan, BackgroundJobExternalContext captureContext, string fingerprint,
        BackgroundJobCommitGate arm, ExternalInvocationObservation invocation, CancellationToken token)
        where TPlan : IBackgroundJobPlan where TOutcome : IBackgroundJobOutcome
    {
        var operation = (IBackgroundJobExternalOperation<TPlan, TOutcome>)adapter;
        var typedPlan = (TPlan)plan;
        var providerContext = captureContext with { };
        token.ThrowIfCancellationRequested();
        if (!arm.Run.TryIssueExternalExecute(arm)) return null;
        invocation.MarkIssued();
        var report = await operation.ExecuteAsync(providerContext, typedPlan, token).ConfigureAwait(false);
        return BackgroundJobRetainedExternalObservation.Capture(captureContext, fingerprint, report);
    }

    private static async Task<BackgroundJobRetainedExternalObservation?> CallExternalReconcileAsync<TPlan, TOutcome>(
        object adapter, object plan, BackgroundJobExternalContext captureContext, string fingerprint,
        BackgroundJobExternalRecoveryBinding recovery, ExternalInvocationObservation invocation, CancellationToken token)
        where TPlan : IBackgroundJobPlan where TOutcome : IBackgroundJobOutcome
    {
        var operation = (IBackgroundJobExternalOperation<TPlan, TOutcome>)adapter;
        var typedPlan = (TPlan)plan;
        var providerContext = captureContext with { };
        token.ThrowIfCancellationRequested();
        if (!recovery.Run.TryIssueExternalReconcile(recovery)) return null;
        invocation.MarkIssued();
        var report = await operation.ReconcileAsync(providerContext, typedPlan, token).ConfigureAwait(false);
        return BackgroundJobRetainedExternalObservation.Capture(captureContext, fingerprint, report);
    }

    // Memory provenance only. This records the exact bridge's successful entry
    // CAS; it confers no provider, checkpoint, retry or quiescence permission.
    private sealed class ExternalInvocationObservation
    {
        private int _issued;
        internal bool Issued => Volatile.Read(ref _issued) != 0;
        internal void MarkIssued() => Interlocked.Exchange(ref _issued, 1);
    }
}
