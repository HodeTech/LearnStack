using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Infrastructure.Jobs;

public sealed class ReadBackgroundJobEnvelopeQueryHandler(IUnitOfWork unit,
    BackgroundJobDefinitionRegistry definitions, BackgroundJobRuntimeOptions options,
    ITenantContext tenantContext) : IRequestHandler<ReadBackgroundJobEnvelopeQuery, Result<BackgroundJobEnvelope>>
{
    public async Task<Result<BackgroundJobEnvelope>> Handle(ReadBackgroundJobEnvelopeQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        BackgroundJobExecutionAdmission.RequireRead(request.Claim);
        if (unit.Mode != TransactionMode.ReadOnly || tenantContext.IsResolved)
            throw new InvalidOperationException("Envelope validation requires an unresolved physical read-only owner.");
        var transaction = unit.Transaction;
        var announcement = unit.GetTenantContextAnnouncement(transaction);
        await using var command = BackgroundJobEnvelopeSql.Command(unit,
            "SELECT " + BackgroundJobEnvelopeSql.Projection + " FROM background_jobs WHERE id = @id AND tenant_id = @tenant",
            request.Claim, options);
        BackgroundJobEnvelope? envelope;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            envelope = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? BackgroundJobEnvelopeSql.Read(reader) : null;
        if (envelope is null || !BackgroundJobEnvelopeSql.Matches(envelope, request.Claim)
            || BackgroundJobEnvelopeSql.RoutedDefinition(envelope, request.Claim, definitions) is not { } definition)
            return Result<BackgroundJobEnvelope>.Fail(BackgroundJobExecutionAdmission.Refused);
        var payload = BackgroundJobPayloadContract.Read(envelope.Parameters, definition.ParameterType, BackgroundJobPayloadKind.Parameters);
        if (BackgroundJobPayloadContract.Serialize(payload, definition.ParameterType, BackgroundJobPayloadKind.Parameters) != envelope.Parameters)
            return Result<BackgroundJobEnvelope>.Fail(BackgroundJobExecutionAdmission.Refused);
        BackgroundJobExecutionAdmission.RequireRead(request.Claim);
        if (!ReferenceEquals(unit.Transaction, transaction) || announcement != unit.GetTenantContextAnnouncement(transaction)
            || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadOnly || tenantContext.IsResolved)
            throw new InvalidOperationException("Envelope validation lost its physical read-only owner.");
        return Result<BackgroundJobEnvelope>.Ok(envelope);
    }
}
