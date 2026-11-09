using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Customization.Application.Contracts.CustomFields;

public sealed record CustomFieldDefinitionInput(
    string TargetEntity, string Key, int SchemaVersion,
    [property: PiiSensitive] string JsonSchema,
    [property: PiiSensitive] IReadOnlyDictionary<string, string> Labels,
    PersonalDataCategory PiiCategory, bool IsRequired, short Sort,
    [property: PiiSensitive] string? PurposeKey,
    [property: PiiSensitive] IReadOnlyDictionary<string, string>? Notices,
    [property: PiiSensitive] IReadOnlyList<CustomFieldControlLabels>? FormLabels = null)
{
    public override string ToString() => nameof(CustomFieldDefinitionInput);
}

public sealed record RegisterCustomFieldDefinitionCommand(Guid DefinitionId, [property: PiiSensitive] CustomFieldDefinitionInput Input)
    : IRequest<Result<CustomFieldDefinitionDto>>
{ public override string ToString() => nameof(RegisterCustomFieldDefinitionCommand); }
public sealed record ReviseCustomFieldDefinitionDraftCommand(Guid DefinitionId, long ExpectedVersion, [property: PiiSensitive] CustomFieldDefinitionInput Input)
    : IRequest<Result<CustomFieldDefinitionDto>>
{ public override string ToString() => nameof(ReviseCustomFieldDefinitionDraftCommand); }
public sealed record PublishCustomFieldDefinitionCommand(Guid DefinitionId, long ExpectedVersion,
    Guid? ExpectedActiveDefinitionId, long? ExpectedActiveVersion) : IRequest<Result<CustomFieldDefinitionDto>>;
public sealed record RetireCustomFieldDefinitionCommand(Guid DefinitionId, long ExpectedVersion) : IRequest<Result<CustomFieldDefinitionDto>>;
public sealed record DiscardCustomFieldDefinitionDraftCommand(Guid DefinitionId, long ExpectedVersion) : IRequest<Result<CustomFieldDefinitionDto>>;
public sealed record GetCustomFieldDefinitionQuery(Guid DefinitionId) : IRequest<Result<CustomFieldDefinitionDto>>;
public sealed record ListCustomFieldDefinitionsQuery(string? Cursor = null, string? Status = null) : IRequest<Result<CustomFieldConceptPage>>;
public sealed record ListCustomFieldDefinitionRevisionsQuery(Guid DefinitionId, string? Cursor = null) : IRequest<Result<CustomFieldDefinitionPage>>;

public sealed record CustomFieldDefinitionDto(Guid DefinitionId, string TargetEntity, string Key, int SchemaVersion,
    string Status, long Version, bool IsDiscarded, [property: PiiSensitive] string JsonSchema,
    [property: PiiSensitive] IReadOnlyDictionary<string, string> Labels, PersonalDataCategory PiiCategory,
    bool IsRequired, short Sort, [property: PiiSensitive] string? PurposeKey,
    [property: PiiSensitive] IReadOnlyDictionary<string, string>? Notices, int MaximumValueBytes,
    [property: PiiSensitive] IReadOnlyList<CustomFieldControlLabels>? FormLabels = null)
{ public override string ToString() => nameof(CustomFieldDefinitionDto); }
public sealed record CustomFieldConceptSummary(string Key, int LatestSchemaVersion, Guid? ActiveDefinitionId, int? ActiveSchemaVersion);
public sealed record CustomFieldConceptPage(IReadOnlyList<CustomFieldConceptSummary> Items, string? NextCursor);
public sealed record CustomFieldDefinitionPage(IReadOnlyList<CustomFieldDefinitionDto> Items, string? NextCursor);

public enum CustomFieldResolution { NewBinding = 1, ExistingPin = 2 }
public sealed record CustomFieldDefinitionPin(Guid DefinitionId, int SchemaVersion);

/// <summary>Uncached, exact, tenant-filtered reads on the caller's existing ambient owner.</summary>
public interface IExactCustomFieldDefinitionReader
{
    Task<CustomFieldDefinitionDto?> ReadAsync(CustomFieldDefinitionPin pin, CustomFieldResolution resolution, IdentityMutationLockPlan? fence, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CustomFieldDefinitionDto>> ReadBatchAsync(IReadOnlyCollection<CustomFieldDefinitionPin> pins,
        CustomFieldResolution resolution, IdentityMutationLockPlan? fence, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CustomFieldDefinitionDto>> ReadActiveForDisplayAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CustomFieldDefinitionDto>> ReadActiveAsync(IdentityMutationLockPlan fence, CancellationToken cancellationToken = default);
}

/// <summary>Finite numeric normalization and the authoritative instance gate, with payload-free refusals.</summary>
public interface ICustomFieldValueValidator
{
    Result<string> ValidateAndNormalize(CustomFieldDefinitionDto definition, string envelopeJson);
}

/// <summary>Numeric structural location, using ordinal ASCII property order; enum choices retain array order.</summary>
public sealed record CustomFieldControlLabels(string Location,
    [property: PiiSensitive] IReadOnlyDictionary<string, string>? Labels,
    [property: PiiSensitive] IReadOnlyList<IReadOnlyDictionary<string, string>>? Choices = null)
{
    public override string ToString() => nameof(CustomFieldControlLabels);
}
public sealed record CustomFieldControlShape(string Location, string Type, int EnumChoices);
