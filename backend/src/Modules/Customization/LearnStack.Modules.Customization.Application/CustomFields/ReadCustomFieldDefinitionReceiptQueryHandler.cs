using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Results;
using MediatR;
namespace LearnStack.Modules.Customization.Application.CustomFields;
/// <summary>Authorizes the original retained revision, including a discarded draft; returns no definition body.</summary>
public sealed class ReadCustomFieldDefinitionReceiptQueryHandler(ICustomFieldDefinitionStore store,
    CustomFieldDefinitionAdmission admission, IAuditSubject subject)
    : IRequestHandler<ReadCustomFieldDefinitionReceiptQuery, Result<None>>
{
    public async Task<Result<None>> Handle(ReadCustomFieldDefinitionReceiptQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DefinitionId == Guid.Empty || request.DefinitionId.Version != 7)
            return CustomFieldDefinitionAdmission.Fail<None>("lockey_validation_failed");
        var gate = await admission.ReadAsync(request, cancellationToken);
        if (gate.IsFailure) return gate;
        var row = await store.FindAsync(TenantCustomFieldDefId.From(request.DefinitionId), cancellationToken);
        if (row is null) return CustomFieldDefinitionAdmission.Fail<None>("lockey_not_found");
        gate = await admission.ReadAsync(request, cancellationToken);
        if (gate.IsFailure) return gate;
        subject.Designate(row);
        return Result.Ok(None.Value);
    }
}
