using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Results;
using MediatR;
namespace LearnStack.Modules.Customization.Application.Contracts.CustomFields;

public sealed record ReadCustomFieldDefinitionReceiptQuery(Guid DefinitionId) : IRequest<Result<None>>, IIdempotencyReceiptRead;
