using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Application.Authorization;

/// <summary>Framework checkpoints require their actual retained job request frame, never a SystemActor UUID.</summary>
public sealed class BackgroundJobOperationPolicySource : IOperationPolicySource
{
    public IEnumerable<PermissionRegistration> Definitions => [];
    public IEnumerable<OperationPolicy> Policies =>
    [
        OperationPolicy.ForTrusted<ReadBackgroundJobEnvelopeQuery>("kernel.job.read_envelope", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<AdmitBackgroundJobAttemptCommand>("kernel.job.admit_attempt", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<ReadBackgroundJobExternalStateQuery>("kernel.job.read_external", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<RecordBackgroundJobArmCommand>("kernel.job.record_arm", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<RecordBackgroundJobOutcomeCommand>("kernel.job.record_outcome", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<RecordBackgroundJobCapacityCleanupCommand>("kernel.job.record_cleanup", AuthorizationSourceKind.BackgroundJob),
        OperationPolicy.ForTrusted<SubmitBackgroundJobRecipientCommand>("kernel.job.submit_recipient", AuthorizationSourceKind.BackgroundJob),
    ];
}
