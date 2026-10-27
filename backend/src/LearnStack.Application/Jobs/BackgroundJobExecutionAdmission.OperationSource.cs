using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Application.Jobs;

public sealed partial class BackgroundJobExecutionAdmission
{
    /// <summary>Captures the already admitted request frame; it never reclaims a root.</summary>
    internal static object? CaptureOperationEvidence(Type requestType, ITenantContext context)
    {
        if (JobApplicationSourceOwner.Current is not { } source || CurrentFrame.Value is not { } frame
            || CurrentRequest.Value is not { } request || request.RequestType != requestType
            || !ReferenceEquals(request.Execution, frame)) return null;
        var evidence = new OperationEvidence(frame, request, source, context);
        return IsOperationEvidenceCurrent(evidence) ? evidence : null;
    }

    internal static bool IsOperationEvidenceCurrent(object? evidence)
    {
        if (evidence is not OperationEvidence captured || !ReferenceEquals(CurrentFrame.Value, captured.Execution)
            || !ReferenceEquals(JobApplicationSourceOwner.Current, captured.SourceOwner)
            || !captured.SourceOwner.IsCurrentBinding || !IsLive(captured.Execution)
            || !captured.Request.IsOpen) return false;
        var found = false;
        for (var request = CurrentRequest.Value; request is not null; request = request.Parent)
            if (ReferenceEquals(request, captured.Request)) found = true;
        if (!found) return false;
        var frame = captured.Execution;
        if (frame.StartGate?.Invoke() == false || frame.CapacityCleanup?.Gate.IsClosed == true
            || frame.CapacityCleanup?.KnownRollback == true || frame.CapacityCleanup?.CommitAcknowledged == true
            || frame.ExternalCheckpoint?.Authority.Run.IsClosed == true
            || frame.ExternalCheckpoint?.Gate?.IsClosed == true || frame.Recipient?.Gate.IsClosed == true
            || frame.Unit?.IsRollbackOnly == true) return false;
        var owner = frame.Receipt?.SourceOwner ?? frame.CapacityCleanup?.SourceOwner
            ?? frame.ExternalCheckpoint?.Authority.SourceOwner ?? frame.Recipient?.SourceOwner;
        if (owner is not null && !ReferenceEquals(owner, captured.SourceOwner)) return false;
        try
        {
            RequireContext(captured.Context);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class OperationEvidence(Frame execution, RequestFrame request,
        JobApplicationSourceOwner sourceOwner, ITenantContext context)
    {
        internal Frame Execution { get; } = execution;
        internal RequestFrame Request { get; } = request;
        internal JobApplicationSourceOwner SourceOwner { get; } = sourceOwner;
        internal ITenantContext Context { get; } = context;
        public override string ToString() => nameof(OperationEvidence);
    }
}

/// <summary>The scoped adapter exposes only the current job owner's closed request evidence.</summary>
public sealed class BackgroundJobOperationSource : TrustedOperationDescriptor.MintingSource, ITrustedOperationSource
{
    public TrustedOperationDescriptor? Capture(Type requestType, ITenantContext context)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(context);
        if (!CanIssue || BackgroundJobExecutionAdmission.CaptureOperationEvidence(requestType, context) is not { } evidence)
            return null;
        return ForOperation(AuthorizationSourceKind.BackgroundJob, requestType, context, evidence);
    }

    protected override bool IsEvidenceCurrent(object? evidence) =>
        BackgroundJobExecutionAdmission.IsOperationEvidenceCurrent(evidence);
}
