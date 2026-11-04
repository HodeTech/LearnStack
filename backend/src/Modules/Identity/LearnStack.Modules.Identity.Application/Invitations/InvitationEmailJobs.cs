using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Invitations;

public sealed record PrepareTenantInvitationEmailCommand(InvitationEmailParameters Parameters) : IRequest<Result<InvitationEmailPlan>>;
public sealed record PrepareOrganizationInvitationEmailCommand(InvitationEmailParameters Parameters) : IRequest<Result<InvitationEmailPlan>>;
public sealed record FinalizeTenantInvitationEmailCommand(InvitationEmailParameters Parameters,
    BackgroundJobFinalization<InvitationEmailPlan, InvitationEmailOutcome> Finalization) : IRequest<Result<None>>;
public sealed record FinalizeOrganizationInvitationEmailCommand(InvitationEmailParameters Parameters,
    BackgroundJobFinalization<InvitationEmailPlan, InvitationEmailOutcome> Finalization) : IRequest<Result<None>>;

public interface IInvitationEmailJobStore
{
    BackgroundJobId RequireCurrentJob();
    Task<Invitation?> FindLockedAsync(InvitationId invitationId, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>Four scope-specific roots share business mechanics, never their scope classification.</summary>
public sealed class InvitationEmailJobHandlers(IInvitationEmailJobStore store, IIdentityMutationLocks locks,
    ITenantContext tenant, IAuditSubject audit, IClock clock)
    : IRequestHandler<PrepareTenantInvitationEmailCommand, Result<InvitationEmailPlan>>,
      IRequestHandler<PrepareOrganizationInvitationEmailCommand, Result<InvitationEmailPlan>>,
      IRequestHandler<FinalizeTenantInvitationEmailCommand, Result<None>>,
      IRequestHandler<FinalizeOrganizationInvitationEmailCommand, Result<None>>
{
    public Task<Result<InvitationEmailPlan>> Handle(PrepareTenantInvitationEmailCommand request, CancellationToken cancellationToken) =>
        PrepareAsync(request.Parameters, cancellationToken);
    public Task<Result<InvitationEmailPlan>> Handle(PrepareOrganizationInvitationEmailCommand request, CancellationToken cancellationToken) =>
        PrepareAsync(request.Parameters, cancellationToken);
    public Task<Result<None>> Handle(FinalizeTenantInvitationEmailCommand request, CancellationToken cancellationToken) =>
        FinalizeAsync(request.Parameters, request.Finalization, cancellationToken);
    public Task<Result<None>> Handle(FinalizeOrganizationInvitationEmailCommand request, CancellationToken cancellationToken) =>
        FinalizeAsync(request.Parameters, request.Finalization, cancellationToken);
    private async Task<Invitation?> ReadAsync(InvitationEmailParameters parameters, CancellationToken token)
    {
        if (parameters.InvitationId.Version != 7 || parameters.GenerationId.Version != 7 || parameters.SpoolId.Version != 7)
            return null;
        _ = store.RequireCurrentJob();
        await locks.AcquireAsync(new([], tenant.TenantId, IdentityTenantLockMode.Shared), token).ConfigureAwait(false);
        var invitation = await store.FindLockedAsync(InvitationId.From(parameters.InvitationId), token).ConfigureAwait(false);
        if (invitation is not null) audit.Designate(invitation);
        return invitation;
    }
    private async Task<Result<InvitationEmailPlan>> PrepareAsync(InvitationEmailParameters parameters, CancellationToken token)
    {
        var invitation = await ReadAsync(parameters, token).ConfigureAwait(false);
        if (invitation is null || invitation.Status != InvitationStatus.Pending || invitation.IsExpired(clock.UtcNow)
            || invitation.GenerationId.Value != parameters.GenerationId || invitation.CurrentDeliveryJobId != store.RequireCurrentJob())
            return Result<InvitationEmailPlan>.Fail(IdentityFailures.Refused());
        return Result<InvitationEmailPlan>.Ok(new(parameters.InvitationId, parameters.GenerationId, parameters.SpoolId));
    }
    private async Task<Result<None>> FinalizeAsync(InvitationEmailParameters parameters,
        BackgroundJobFinalization<InvitationEmailPlan, InvitationEmailOutcome> finalization, CancellationToken token)
    {
        if (finalization.Plan != new InvitationEmailPlan(parameters.InvitationId, parameters.GenerationId, parameters.SpoolId))
            return Result<None>.Fail(IdentityFailures.Refused());
        var invitation = await ReadAsync(parameters, token).ConfigureAwait(false);
        if (invitation is null) return Result<None>.Fail(IdentityFailures.Refused());
        var job = store.RequireCurrentJob();
        var generation = InvitationGenerationId.From(parameters.GenerationId);
        Result<None> result;
        if (finalization.Outcome is { } outcome)
            result = invitation.RecordSubmission(generation, job, InvitationEmailReceiptId.From(outcome.ReceiptId),
                finalization.Status == BackgroundJobFinalizationStatus.Succeeded ? InvitationSubmissionStatus.SubmissionAccepted : InvitationSubmissionStatus.Failed,
                clock.UtcNow, tenant.UserId ?? UserId.SystemActor);
        else if (finalization.Status == BackgroundJobFinalizationStatus.Failed
            && finalization.Reason is BackgroundJobFinalizationReason.PermanentFailure or BackgroundJobFinalizationReason.NormalAttemptsExhausted)
            result = invitation.RecordWorkflowFailure(generation, job, clock.UtcNow, tenant.UserId ?? UserId.SystemActor);
        else return Result<None>.Fail(IdentityFailures.Refused());
        if (result.IsSuccess) await store.SaveAsync(token).ConfigureAwait(false);
        return result;
    }
}
