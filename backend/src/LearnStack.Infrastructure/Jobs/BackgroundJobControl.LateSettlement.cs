namespace LearnStack.Infrastructure.Jobs;

/// <summary>Process observations of timed-out stages after positive actual invocation cleanup, not durable job outcomes.</summary>
public sealed record BackgroundJobLateSettlementSnapshot(
    long Framework,
    long Transactional,
    long Prepare,
    long ExternalRead,
    long Arm,
    long Execute,
    long Outcome,
    long Finalize,
    long PrepareHistory,
    long ArmHistory,
    long OutcomeHistory,
    long FinalizeHistory,
    long RecoveryAcquire,
    long RecoveryConfirmation,
    long Reconcile,
    long NoStart,
    long RecoveryDisposition);

internal sealed partial class BackgroundJobControl
{
    private long _lateFramework;
    private long _lateTransactional;
    private long _latePrepare;
    private long _lateExternalRead;
    private long _lateArm;
    private long _lateExecute;
    private long _lateOutcome;
    private long _lateFinalize;
    private long _latePrepareHistory;
    private long _lateArmHistory;
    private long _lateOutcomeHistory;
    private long _lateFinalizeHistory;
    private long _lateRecoveryAcquire;
    private long _lateRecoveryConfirmation;
    private long _lateReconcile;
    private long _lateNoStart;
    private long _lateRecoveryDisposition;

    internal void ObserveLateSettlement(BackgroundJobInvocationStage stage)
    {
        switch (stage)
        {
            case BackgroundJobInvocationStage.Framework: Interlocked.Increment(ref _lateFramework); break;
            case BackgroundJobInvocationStage.Transactional: Interlocked.Increment(ref _lateTransactional); break;
            case BackgroundJobInvocationStage.Prepare: Interlocked.Increment(ref _latePrepare); break;
            case BackgroundJobInvocationStage.ExternalRead: Interlocked.Increment(ref _lateExternalRead); break;
            case BackgroundJobInvocationStage.Arm: Interlocked.Increment(ref _lateArm); break;
            case BackgroundJobInvocationStage.Execute: Interlocked.Increment(ref _lateExecute); break;
            case BackgroundJobInvocationStage.Outcome: Interlocked.Increment(ref _lateOutcome); break;
            case BackgroundJobInvocationStage.Finalize: Interlocked.Increment(ref _lateFinalize); break;
            case BackgroundJobInvocationStage.PrepareHistory: Interlocked.Increment(ref _latePrepareHistory); break;
            case BackgroundJobInvocationStage.ArmHistory: Interlocked.Increment(ref _lateArmHistory); break;
            case BackgroundJobInvocationStage.OutcomeHistory: Interlocked.Increment(ref _lateOutcomeHistory); break;
            case BackgroundJobInvocationStage.FinalizeHistory: Interlocked.Increment(ref _lateFinalizeHistory); break;
            case BackgroundJobInvocationStage.RecoveryAcquire: Interlocked.Increment(ref _lateRecoveryAcquire); break;
            case BackgroundJobInvocationStage.RecoveryConfirmation: Interlocked.Increment(ref _lateRecoveryConfirmation); break;
            case BackgroundJobInvocationStage.Reconcile: Interlocked.Increment(ref _lateReconcile); break;
            case BackgroundJobInvocationStage.NoStart: Interlocked.Increment(ref _lateNoStart); break;
            case BackgroundJobInvocationStage.RecoveryDisposition: Interlocked.Increment(ref _lateRecoveryDisposition); break;
        }
    }

    private BackgroundJobLateSettlementSnapshot CaptureLateSettlements() => new(
        Interlocked.Read(ref _lateFramework),
        Interlocked.Read(ref _lateTransactional),
        Interlocked.Read(ref _latePrepare),
        Interlocked.Read(ref _lateExternalRead),
        Interlocked.Read(ref _lateArm),
        Interlocked.Read(ref _lateExecute),
        Interlocked.Read(ref _lateOutcome),
        Interlocked.Read(ref _lateFinalize),
        Interlocked.Read(ref _latePrepareHistory),
        Interlocked.Read(ref _lateArmHistory),
        Interlocked.Read(ref _lateOutcomeHistory),
        Interlocked.Read(ref _lateFinalizeHistory),
        Interlocked.Read(ref _lateRecoveryAcquire),
        Interlocked.Read(ref _lateRecoveryConfirmation),
        Interlocked.Read(ref _lateReconcile),
        Interlocked.Read(ref _lateNoStart),
        Interlocked.Read(ref _lateRecoveryDisposition));
}
