using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Results;
using MediatR;
namespace LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;

public sealed record ReadOwnGrantedFieldConsentReceiptQuery([property: PiiSensitive] Guid EvidenceId)
    : IRequest<Result<None>>, IIdempotencyReceiptRead
{ public override string ToString() => nameof(ReadOwnGrantedFieldConsentReceiptQuery); }
public sealed record ReadOwnWithdrawnFieldConsentReceiptQuery([property: PiiSensitive] Guid EvidenceId)
    : IRequest<Result<None>>, IIdempotencyReceiptRead
{ public override string ToString() => nameof(ReadOwnWithdrawnFieldConsentReceiptQuery); }
