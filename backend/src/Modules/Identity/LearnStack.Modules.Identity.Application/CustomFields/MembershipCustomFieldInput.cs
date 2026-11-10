using System.Text.Json;
using FluentValidation;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Domain;

namespace LearnStack.Modules.Identity.Application.CustomFields;

public static class MembershipCustomFieldInput
{
    public const int MaximumSubmissionBytes = 4 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static bool Valid(object request)
    {
        var (version, patch, target) = request switch
        {
            SetMembershipCustomFieldsCommand command => (command.ExpectedVersion, command.Patch, (Guid?)command.MembershipId),
            SetOwnMembershipCustomFieldsCommand command => (command.ExpectedVersion, command.Patch, null),
            _ => (-1L, null, null),
        };
        if (version < 0 || target is { } id && !ValidId(id) || patch is null || patch.Set.IsDefault || patch.Remove.IsDefault
            || patch.Set.Length > MembershipCustomFieldValues.MaximumSlots || patch.Remove.Length > MembershipCustomFieldValues.MaximumSlots
            || patch.Set.Length + patch.Remove.Length == 0
            || patch.Set.Any(value => value is null || !MembershipCustomFieldValues.ValidKey(value.ConceptKey)
                || !ValidId(value.DefinitionId) || value.SchemaVersion < 1 || !MembershipCustomFieldValues.ValidEnvelope(value.Envelope)
                || value.ConsentEvidenceId is { } evidence && !ValidId(evidence))
            || patch.Remove.Any(value => value is null || !MembershipCustomFieldValues.ValidKey(value.ConceptKey)
                || !ValidId(value.DefinitionId) || value.SchemaVersion < 1)
            || patch.Set.Select(value => value.ConceptKey).Distinct(StringComparer.Ordinal).Count() != patch.Set.Length
            || patch.Set.Select(value => value.DefinitionId).Distinct().Count() != patch.Set.Length
            || patch.Remove.Select(value => value.ConceptKey).Distinct(StringComparer.Ordinal).Count() != patch.Remove.Length) return false;
        return JsonSerializer.SerializeToUtf8Bytes(request, request.GetType(), Options).Length <= MaximumSubmissionBytes;
    }

    public static bool ValidId(Guid id) => id != Guid.Empty && id.Version == 7;
}

public sealed class SetMembershipCustomFieldsCommandValidator : AbstractValidator<SetMembershipCustomFieldsCommand>
{
    public SetMembershipCustomFieldsCommandValidator() => RuleFor(request => request)
        .Must(MembershipCustomFieldInput.Valid).WithMessage("lockey_validation_failed");
}
public sealed class SetOwnMembershipCustomFieldsCommandValidator : AbstractValidator<SetOwnMembershipCustomFieldsCommand>
{
    public SetOwnMembershipCustomFieldsCommandValidator() => RuleFor(request => request)
        .Must(MembershipCustomFieldInput.Valid).WithMessage("lockey_validation_failed");
}
public sealed class GetMembershipCustomFieldsQueryValidator : AbstractValidator<GetMembershipCustomFieldsQuery>
{
    public GetMembershipCustomFieldsQueryValidator() => RuleFor(request => request.MembershipId)
        .Must(MembershipCustomFieldInput.ValidId).WithMessage("lockey_validation_failed");
}
