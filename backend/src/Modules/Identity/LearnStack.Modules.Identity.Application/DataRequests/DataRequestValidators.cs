using FluentValidation;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.Pagination;

namespace LearnStack.Modules.Identity.Application.DataRequests;

// Shape only: a canonical UUIDv7 target, a non-negative version, a bounded page, a canonical ticket spelling and a
// non-negative chunk index. Authority, scope and state are the handlers'.

internal sealed class ListOwnDataRequestsValidator : AbstractValidator<ListOwnDataRequestsQuery>
{
    public ListOwnDataRequestsValidator()
    {
        RuleFor(request => request.Limit).InclusiveBetween(1, CursorPagination.MaxLimit).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.Cursor).Must(DataRequestInput.Cursor).WithErrorCode("lockey_invalid_value");
    }
}

internal sealed class ListDataRequestsValidator : AbstractValidator<ListDataRequestsQuery>
{
    public ListDataRequestsValidator()
    {
        RuleFor(request => request.Limit).InclusiveBetween(1, CursorPagination.MaxLimit).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.Cursor).Must(DataRequestInput.Cursor).WithErrorCode("lockey_invalid_value");
    }
}

internal sealed class GetOwnDataRequestValidator : AbstractValidator<GetOwnDataRequestQuery>
{
    public GetOwnDataRequestValidator() =>
        RuleFor(request => request.RequestId).Must(DataRequestInput.Id).WithErrorCode("lockey_identifier_required");
}

internal sealed class GetDataRequestValidator : AbstractValidator<GetDataRequestQuery>
{
    public GetDataRequestValidator() =>
        RuleFor(request => request.RequestId).Must(DataRequestInput.Id).WithErrorCode("lockey_identifier_required");
}

internal sealed class ReadOwnDataRequestReceiptValidator : AbstractValidator<ReadOwnDataRequestReceiptQuery>
{
    public ReadOwnDataRequestReceiptValidator() =>
        RuleFor(request => request.RequestId).Must(DataRequestInput.Id).WithErrorCode("lockey_identifier_required");
}

internal sealed class ReadDataRequestReceiptValidator : AbstractValidator<ReadDataRequestReceiptQuery>
{
    public ReadDataRequestReceiptValidator() =>
        RuleFor(request => request.RequestId).Must(DataRequestInput.Id).WithErrorCode("lockey_identifier_required");
}

internal sealed class CreateDataExportForSubjectValidator : AbstractValidator<CreateDataExportForSubjectCommand>
{
    public CreateDataExportForSubjectValidator() =>
        RuleFor(request => request.MembershipId).Must(DataRequestInput.Id).WithErrorCode("lockey_identifier_required");
}

internal sealed class CancelOwnDataRequestValidator : AbstractValidator<CancelOwnDataRequestCommand>
{
    public CancelOwnDataRequestValidator()
    {
        RuleFor(request => request.RequestId).Must(DataRequestInput.Id).WithErrorCode("lockey_identifier_required");
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_invalid_value");
    }
}

internal sealed class CancelDataRequestValidator : AbstractValidator<CancelDataRequestCommand>
{
    public CancelDataRequestValidator()
    {
        RuleFor(request => request.RequestId).Must(DataRequestInput.Id).WithErrorCode("lockey_identifier_required");
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_invalid_value");
    }
}

internal sealed class RevokeOwnDataExportValidator : AbstractValidator<RevokeOwnDataExportCommand>
{
    public RevokeOwnDataExportValidator()
    {
        RuleFor(request => request.RequestId).Must(DataRequestInput.Id).WithErrorCode("lockey_identifier_required");
        RuleFor(request => request.ExpectedArtifactVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_invalid_value");
    }
}

internal sealed class RevokeDataExportValidator : AbstractValidator<RevokeDataExportCommand>
{
    public RevokeDataExportValidator()
    {
        RuleFor(request => request.RequestId).Must(DataRequestInput.Id).WithErrorCode("lockey_identifier_required");
        RuleFor(request => request.ExpectedArtifactVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_invalid_value");
    }
}

internal sealed class MintOwnDataExportTicketValidator : AbstractValidator<MintOwnDataExportTicketCommand>
{
    public MintOwnDataExportTicketValidator() =>
        RuleFor(request => request.RequestId).Must(DataRequestInput.Id).WithErrorCode("lockey_identifier_required");
}

internal sealed class ReadOwnDataExportChunkValidator : AbstractValidator<ReadOwnDataExportChunkCommand>
{
    public ReadOwnDataExportChunkValidator()
    {
        RuleFor(request => request.RequestId).Must(DataRequestInput.Id).WithErrorCode("lockey_identifier_required");
        RuleFor(request => request.Index).GreaterThanOrEqualTo(0).WithErrorCode("lockey_invalid_value");
        // The ticket is checked for its canonical spelling only; its value never reaches a message or a log.
        RuleFor(request => request.Ticket).Must(ticket => DataExportTicketCodec.TryDigest(ticket) is not null)
            .WithErrorCode("lockey_invalid_value").WithMessage("The ticket is not a canonical download ticket.");
    }
}

internal static class DataRequestInput
{
    internal static bool Id(Guid id) => id != Guid.Empty && id.Version == 7;

    internal static bool Cursor(string? value) => value is null || value.Length is > 0 and <= DataRequestCursorCodec.MaximumEncodedLength;
}
