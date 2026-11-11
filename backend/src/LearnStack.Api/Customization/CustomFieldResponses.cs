using System.Text.Json;
using LearnStack.Api.Common;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Pagination;
namespace LearnStack.Api.Customization;

public sealed record CustomFieldActiveRevisionResponse(string Id, string Version);
public sealed record CustomFieldReceiptResponse(string Id, string Version);
public sealed record CustomFieldDefinitionResponse(string DefinitionId, string TargetEntity, string Key, int SchemaVersion, string Status,
    string Version, bool IsDiscarded, [property: PiiSensitive] JsonElement Schema, [property: PiiSensitive] IReadOnlyDictionary<string, string> Labels,
    string PiiCategory, bool IsRequired, int Sort, [property: PiiSensitive] string? PurposeKey,
    [property: PiiSensitive] IReadOnlyDictionary<string, string>? Notices, int MaximumValueBytes,
    [property: PiiSensitive] IReadOnlyList<CustomFieldControlLabels>? FormLabels, CustomFieldActiveRevisionResponse? ActiveRevision)
{ public override string ToString() => nameof(CustomFieldDefinitionResponse); }
public sealed record CustomFieldDefinitionPageResponse(IReadOnlyList<CustomFieldDefinitionResponse> Items, PageInfo PageInfo);
public sealed record MembershipCustomFieldsResponse(string MembershipId, string Version, [property: PiiSensitive] IReadOnlyList<MembershipCustomFieldResponse> Fields)
{ public override string ToString() => nameof(MembershipCustomFieldsResponse); }
public sealed record MembershipCustomFieldResponse(string ConceptKey, string DefinitionId, int SchemaVersion, string State, bool IsRequired,
    [property: PiiSensitive] JsonElement? Envelope, [property: PiiSensitive] MembershipCustomFieldMetadataResponse Definition, FieldConsentReceiptResponse? Consent)
{ public override string ToString() => nameof(MembershipCustomFieldResponse); }
public sealed record MembershipCustomFieldMetadataResponse([property: PiiSensitive] JsonElement Schema, [property: PiiSensitive] IReadOnlyDictionary<string, string> Labels,
    string PiiCategory, int Sort, [property: PiiSensitive] string? PurposeKey, [property: PiiSensitive] IReadOnlyDictionary<string, string>? Notices,
    [property: PiiSensitive] IReadOnlyList<MembershipCustomFieldControlLabels>? FormLabels,
    [property: PiiSensitive] IReadOnlyDictionary<string, string>? NoticeHashes, string Status)
{ public override string ToString() => nameof(MembershipCustomFieldMetadataResponse); }
public sealed record FieldConsentReceiptResponse(string EvidenceId, string Sequence, bool IsGranted);
internal static class CustomFieldResponseProjection
{
    internal static CustomFieldDefinitionResponse Definition(CustomFieldDefinitionDto d) => new(d.DefinitionId.ToString("D"), d.TargetEntity, d.Key, d.SchemaVersion, d.Status,
        AdministrationHttpInput.Version(d.Version), d.IsDiscarded, Schema(d.JsonSchema), d.Labels, d.PiiCategory.ToString(), d.IsRequired, d.Sort, d.PurposeKey, d.Notices,
        d.MaximumValueBytes, d.FormLabels, d.ActiveRevision is { } a ? new(a.Id.ToString("D"), AdministrationHttpInput.Version(a.Version)) : null);
    internal static MembershipCustomFieldsResponse Values(MembershipCustomFieldsDto d) => new(d.MembershipId.ToString("D"), AdministrationHttpInput.Version(d.Version),
        d.Fields.Select(f => new MembershipCustomFieldResponse(f.ConceptKey, f.DefinitionId.ToString("D"), f.SchemaVersion, f.State, f.IsRequired, f.Envelope,
            new(Schema(f.Definition.JsonSchema), f.Definition.Labels, f.Definition.PiiCategory.ToString(), f.Definition.DisplayOrder, f.Definition.PurposeKey, f.Definition.Notices,
                f.Definition.FormLabels, f.Definition.Notices?.ToDictionary(p => p.Key, p => MembershipFieldConsentNotice.ComputeHash(p.Value), StringComparer.Ordinal), f.Definition.Status),
            f.Consent is { } c ? new(c.EvidenceId.ToString("D"), AdministrationHttpInput.Version(c.Sequence), c.IsGranted) : null)).ToArray());
    private static JsonElement Schema(string value)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(value) > 2048) throw new InvalidOperationException("Custom field metadata is unavailable.");
        using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone();
    }
    internal static PageInfo Page(string? cursor) => new(cursor, null, cursor is not null, false);
}
