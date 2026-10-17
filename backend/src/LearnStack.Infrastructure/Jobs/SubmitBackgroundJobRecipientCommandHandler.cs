using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Creates only the fixed stored occurrence input; it constructs no feature request or service.</summary>
public sealed class SubmitBackgroundJobRecipientCommandHandler(PostgresBackgroundJobScheduler scheduler)
    : IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>
{
    public async Task<Result<BackgroundJobId>> Handle(SubmitBackgroundJobRecipientCommand request, CancellationToken cancellationToken)
    {
        BackgroundJobExecutionAdmission.RequireRecipient(request);
        if (request.Completion is not PostgresBackgroundJobRecipientCompletion completion)
            throw new InvalidOperationException("Recipient submission requires its closed PostgreSQL completion.");
        var route = await completion.ReadAndLockAsync(cancellationToken).ConfigureAwait(false);
        return route switch
        {
            BackgroundJobRecipientRead.Existing => Result.Ok(request.ProvisionalChild!.Value),
            BackgroundJobRecipientRead.Ready => Result.Ok(await scheduler.SubmitRecipientAsync(request, cancellationToken).ConfigureAwait(false)),
            _ => Result.Fail<BackgroundJobId>(BackgroundJobExecutionAdmission.Refused),
        };
    }
}
