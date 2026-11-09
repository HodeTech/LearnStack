using LearnStack.SharedKernel;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Time;
using Vogen;

namespace LearnStack.Modules.Customization.Domain;

[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct TenantCustomFieldDefId : IStronglyTypedId<Guid>;

public enum CustomFieldTarget { Membership = 1 }

/// <summary>One reserved revision. Every authored property freezes at publication.</summary>
[TenantOwned]
public sealed class TenantCustomFieldDef : AuditableEntity<TenantCustomFieldDefId>,
    ITenantOwned, IAggregateRoot<TenantCustomFieldDefId>
{
    private TenantCustomFieldDef() { }
    public TenantId TenantId { get; private set; }
    public CustomFieldTarget TargetEntity { get; private set; }
    public string Key { get; private set; } = null!;
    public int SchemaVersion { get; private set; }
    public CustomizationStatus Status { get; private set; }
    [PiiSensitive] public string JsonSchema { get; private set; } = null!;
    [PiiSensitive] public string LabelsJson { get; private set; } = null!;
    public PersonalDataCategory PiiCategory { get; private set; }
    public bool IsRequired { get; private set; }
    public short Sort { get; private set; }
    [PiiSensitive] public string? PurposeKey { get; private set; }
    [PiiSensitive] public string? NoticesJson { get; private set; }
    [PiiSensitive] public string? FormLabelsJson { get; private set; }
    public int MaximumValueBytes { get; private set; }

    public static bool IsSupportedCategory(PersonalDataCategory category) => category is
        PersonalDataCategory.None or PersonalDataCategory.Identity or PersonalDataCategory.Behaviour or PersonalDataCategory.Sensitive;

    public static TenantCustomFieldDef Create(TenantCustomFieldDefId id, TenantId tenant, string key,
        int schemaVersion, string schema, string labels, PersonalDataCategory category, bool required,
        short sort, string? purpose, string? notices, int maximumValueBytes, IClock clock, UserId actor, string? formLabelsJson = null)
    {
        if (!id.IsInitialized() || id.Value == Guid.Empty || id.Value.Version != 7) throw new ArgumentException("Definition identifier is invalid.", nameof(id));
        TenantOwnership.EnsureRealTenant(tenant, "A field belongs to a tenant.", nameof(tenant));
        CustomFieldKey.EnsureValid(key, nameof(key));
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        var result = new TenantCustomFieldDef
        {
            Id = id,
            TenantId = tenant,
            Key = key,
            TargetEntity = CustomFieldTarget.Membership,
            SchemaVersion = schemaVersion,
            Status = CustomizationStatus.Draft
        };
        result.Assign(schema, labels, category, required, sort, purpose, notices, maximumValueBytes, formLabelsJson);
        result.MarkCreated(clock.UtcNow, actor);
        return result;
    }

    public void ReviseDraft(string schema, string labels, PersonalDataCategory category, bool required,
        short sort, string? purpose, string? notices, int maximumValueBytes, IClock clock, UserId actor, string? formLabelsJson = null)
    {
        RequireDraft();
        Assign(schema, labels, category, required, sort, purpose, notices, maximumValueBytes, formLabelsJson);
        MarkUpdated(clock.UtcNow, actor);
    }

    public void Publish(IClock clock, UserId actor)
    {
        RequireDraft();
        MarkUpdated(clock.UtcNow, actor);
        Status = CustomizationStatus.Active;
    }

    public void Retire(IClock clock, UserId actor)
    {
        if (Status != CustomizationStatus.Active || DeletedAt is not null) throw new InvalidOperationException("Definition is not active.");
        MarkUpdated(clock.UtcNow, actor);
        Status = CustomizationStatus.Deprecated;
    }

    public void Discard(IClock clock, UserId actor)
    {
        RequireDraft();
        SoftDelete(clock.UtcNow, actor);
    }

    private void RequireDraft()
    {
        if (Status != CustomizationStatus.Draft || DeletedAt is not null) throw new InvalidOperationException("Definition is not an editable draft.");
    }

    private void Assign(string schema, string labels, PersonalDataCategory category, bool required,
        short sort, string? purpose, string? notices, int maximumValueBytes, string? formLabelsJson)
    {
        if (!IsSupportedCategory(category)) throw new ArgumentOutOfRangeException(nameof(category));
        if (sort < 0 || maximumValueBytes is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(sort));
        JsonValue.EnsureWellFormed(schema, nameof(schema));
        JsonValue.EnsureWellFormed(labels, nameof(labels));
        if (category == PersonalDataCategory.Sensitive)
        {
            CustomFieldKey.EnsureValid(purpose!, nameof(purpose));
            if (purpose!.Length > CustomFieldKey.PurposeMaxLength)
                throw new ArgumentException("Custom field purpose is invalid.", nameof(purpose));
            JsonValue.EnsureWellFormed(notices!, nameof(notices));
        }
        else if (purpose is not null || notices is not null) throw new ArgumentException("Only sensitive fields declare a purpose.");
        if (formLabelsJson is not null) JsonValue.EnsureWellFormed(formLabelsJson, nameof(formLabelsJson));
        JsonSchema = schema; LabelsJson = labels; PiiCategory = category; IsRequired = required;
        Sort = sort; PurposeKey = purpose; NoticesJson = notices; MaximumValueBytes = maximumValueBytes; FormLabelsJson = formLabelsJson;
    }
}

/// <summary>The finite authoring profile's key grammar, including prototype-name refusal.</summary>
public static class CustomFieldKey
{
    public const int PurposeMaxLength = 64;
    public static bool IsValid(string? value) => value is { Length: > 0 and <= CustomizationKey.MaxLength }
        && value is not ("constructor" or "prototype" or "__proto__") && UrlSlug.IsUrlSafe(value);
    public static void EnsureValid(string value, string parameterName)
    {
        if (!IsValid(value)) throw new ArgumentException("Custom field key is invalid.", parameterName);
    }
}
