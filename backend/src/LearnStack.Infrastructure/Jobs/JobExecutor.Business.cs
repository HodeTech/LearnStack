using System.Diagnostics;
using LearnStack.Application.Jobs;
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
        if (!gate.TryStartExecution())
            throw new InvalidOperationException("A job commit gate belongs to exactly one execution.");

        var previous = accessor.Current;
        var previousActivity = Activity.Current;
        AsyncServiceScope? scope = null;
        IUnitOfWork? unit = null;
        IDisposable? authority = null;
        IDisposable? sourceBinding = null;
        Activity? activity = null;
        Result<None>? response = null;
        Exception? failure = null;
        var cleanupFailed = false;
        var retained = new List<JobStartCleanupOwnership>();
        // The callback changes one memory gate. Runtime owns private cancellation and its actual callbacks.
        using var registration = cancellationToken.Register(CloseBusinessGate, gate);
        try
        {
            sourceBinding = receipt.SourceOwner.Enter();
            receipt.SourceOwner.RequireFreshScopeReady();
            cancellationToken.ThrowIfCancellationRequested();
            var context = CreateContext(receipt.Envelope);
            accessor.Current = context;
            _ = ActivityContext.TryParse(receipt.Envelope.Traceparent, null, isRemote: true, out var parent);
            activity = new Activity("job.execution").SetIdFormat(ActivityIdFormat.W3C)
                .SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags).Start();
            scope = scopes.CreateAsyncScope();
            unit = scope.Value.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var completion = scope.Value.ServiceProvider.GetRequiredService<BackgroundJobCompletionParticipant>();
            completion.Arm(receipt, gate);
            authority = admission.Enter(receipt, unit, completion);
            var definitions = scope.Value.ServiceProvider.GetRequiredService<BackgroundJobDefinitionRegistry>();
            var definition = BackgroundJobEnvelopeSql.Definition(receipt.Envelope, definitions)
                ?? throw new InvalidOperationException("The admitted definition is no longer invokable.");
            var parameters = BackgroundJobPayloadContract.Read(receipt.Envelope.Parameters, definition.ParameterType,
                BackgroundJobPayloadKind.Parameters);
            BackgroundJobExecutionAdmission.RequireContext(context);
            var constructor = definition.ProfileRegistration.RootRequestType.GetConstructor([definition.ParameterType])
                ?? throw new InvalidOperationException("The registered root constructor is unavailable.");
            var request = constructor.Invoke([parameters]) as IRequest<Result<None>>
                ?? throw new InvalidOperationException("The registered root has the wrong result contract.");
            // Both parameter and root constructors run under sticky ordinary authority.
            BackgroundJobExecutionAdmission.RequireContext(context);
            receipt.SourceOwner.RequireBusinessReady();
            cancellationToken.ThrowIfCancellationRequested();
            response = await scope.Value.ServiceProvider.GetRequiredService<ISender>()
                .Send(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception caught)
        {
            failure = caught;
            if (gate.CommitAcknowledged) cleanupFailed = true;
        }
        finally
        {
            if (scope is { } owned)
                cleanupFailed |= !await CleanupScopeAsync(owned, unit, null, retained).ConfigureAwait(false);
            cleanupFailed |= !receipt.SourceOwner.CleanupConfirmed;
            receipt.SourceOwner.CloseIssuance();
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
        }

        var uncertain = gate.CommitStarted && !gate.CommitAcknowledged && !gate.KnownRollback;
        var knownRollback = gate.KnownRollback || !gate.CommitStarted && !gate.CommitAcknowledged && !cleanupFailed;
        var decision = BackgroundJobFailurePolicy.Classify(response, failure, gate.CommitAcknowledged, uncertain,
            cancellationToken.IsCancellationRequested || gate.IsClosed);
        return new(decision, gate.CommitAcknowledged, gate.CommitStarted, knownRollback, uncertain, cleanupFailed)
        {
            RetainedCleanup = retained.AsReadOnly(),
            SourceOwner = receipt.SourceOwner,
        };
    }

    private static void CloseBusinessGate(object? state) => ((BackgroundJobCommitGate)state!).Close();
}
