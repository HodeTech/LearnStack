using System.Text.Json;
using System.Collections.Immutable;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Identity.Application.CustomFields;

/// <summary>Revision and consent interpretation; all reads use the caller's existing owner.</summary>
public sealed class MembershipCustomFieldService(IExactCustomFieldDefinitionReader definitions,
    ICustomFieldValueValidator validator, IMembershipFieldConsentReader consents)
{
    public async Task<Result<IReadOnlyCollection<MembershipCustomFieldValue>>> ApplyAsync(Membership member,
        MembershipCustomFieldPatch patch, IdentityMutationLockPlan fence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        var values = MembershipCustomFieldValues.Read(member.Profile.CustomFieldsJson).ToDictionary(value => value.ConceptKey, StringComparer.Ordinal);
        foreach (var remove in patch.Remove)
        {
            if (!values.TryGetValue(remove.ConceptKey, out var stored) || stored.DefinitionId != remove.DefinitionId
                || stored.SchemaVersion != remove.SchemaVersion) return Fail("lockey_concurrency_conflict");
            values.Remove(remove.ConceptKey);
        }
        // The final new-pin observation follows the shared Tenant fence and Member lock.
        var active = await definitions.ReadActiveAsync(fence, cancellationToken);
        if (active.Count > MembershipCustomFieldValues.MaximumSlots) return Fail("lockey_dependency_unavailable");
        var newDefinitions = await definitions.ReadBatchAsync(patch.Set.Select(value =>
            new CustomFieldDefinitionPin(value.DefinitionId, value.SchemaVersion)).ToArray(),
            CustomFieldResolution.NewBinding, fence, cancellationToken);
        if (newDefinitions.Count != patch.Set.Length) return Fail("lockey_business_rule_violation");
        var submitted = newDefinitions.ToDictionary(definition => definition.DefinitionId);
        var sensitiveSets = patch.Set.Where(value => submitted[value.DefinitionId].PiiCategory == PersonalDataCategory.Sensitive).ToArray();
        var current = await CurrentAsync(member, sensitiveSets.Select(value => submitted[value.DefinitionId]).ToArray(), cancellationToken);
        foreach (var set in patch.Set)
        {
            var definition = submitted[set.DefinitionId];
            if (definition.TargetEntity != "Membership" || definition.Key != set.ConceptKey || definition.SchemaVersion != set.SchemaVersion
                || definition.Status != "Active" || definition.IsDiscarded) return Fail("lockey_business_rule_violation");
            var normalized = validator.ValidateAndNormalize(definition, set.Envelope.GetRawText());
            if (normalized.IsFailure) return Fail("lockey_validation_failed");
            Guid? evidence = null;
            if (definition.PiiCategory == PersonalDataCategory.Sensitive)
            {
                if (!current.TryGetValue(definition.DefinitionId, out var consent) || consent is not { IsGranted: true }
                    || consent.EvidenceId != set.ConsentEvidenceId) return Fail("lockey_business_rule_violation");
                evidence = consent.EvidenceId;
            }
            else if (set.ConsentEvidenceId is not null) return Fail("lockey_validation_failed");
            // Reconsent changes current eligibility, never the evidence attached to
            // the original retained slot. An explicit new revision gets new evidence.
            if (values.TryGetValue(set.ConceptKey, out var prior) && prior.DefinitionId == set.DefinitionId
                && prior.SchemaVersion == set.SchemaVersion) evidence = prior.ConsentEvidenceId;
            using var document = JsonDocument.Parse(normalized.Value);
            values[set.ConceptKey] = new(set.ConceptKey, set.DefinitionId, set.SchemaVersion, document.RootElement.Clone(), evidence);
        }
        var normalizedPatch = patch with
        {
            Set = patch.Set.Select(set => set with
            { Envelope = values[set.ConceptKey].Envelope.Clone() }).ToImmutableArray()
        };
        if (!MembershipCustomFieldInput.FitsSubmission(normalizedPatch)) return Fail("lockey_limit_exceeded");
        if (MembershipCustomFieldValues.Serialize(values.Values.ToArray()) is null) return Fail("lockey_validation_failed");
        // Removal-only never makes consent or another missing required answer compulsory.
        if (patch.Set.Length != 0)
        {
            var required = active.Where(definition => definition.IsRequired).ToArray();
            var sensitiveRequired = required.Where(definition => definition.PiiCategory == PersonalDataCategory.Sensitive).ToArray();
            var grants = await CurrentAsync(member, sensitiveRequired, cancellationToken);
            foreach (var definition in required)
            {
                if (!values.TryGetValue(definition.Key, out var value) || value.DefinitionId != definition.DefinitionId
                    || value.SchemaVersion != definition.SchemaVersion || definition.PiiCategory == PersonalDataCategory.Sensitive
                        && (!grants.TryGetValue(definition.DefinitionId, out var consent) || consent is not { IsGranted: true }))
                    return Fail("lockey_business_rule_violation");
            }
        }
        return Result<IReadOnlyCollection<MembershipCustomFieldValue>>.Ok(values.Values.ToArray());
    }

    public async Task<Result<MembershipCustomFieldsDto>> DiscloseAsync(Membership member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        var stored = MembershipCustomFieldValues.Read(member.Profile.CustomFieldsJson);
        var retained = await definitions.ReadBatchAsync(stored.Select(value => new CustomFieldDefinitionPin(value.DefinitionId, value.SchemaVersion)).ToArray(),
            CustomFieldResolution.ExistingPin, null, cancellationToken);
        // A missing immutable revision cannot safely be reinterpreted as public data.
        if (retained.Count != stored.Count || retained.Any(definition => !stored.Any(value => value.DefinitionId == definition.DefinitionId
                && value.SchemaVersion == definition.SchemaVersion && value.ConceptKey == definition.Key)))
            return Result<MembershipCustomFieldsDto>.Fail(new Error(LocalizedMessage.Of("lockey_dependency_unavailable")));
        var active = await definitions.ReadActiveForDisplayAsync(cancellationToken);
        var grants = await CurrentAsync(member, retained.Where(definition => definition.PiiCategory == PersonalDataCategory.Sensitive).ToArray(), cancellationToken);
        // Each batch remains within the authoritative twenty-tuple bound. A
        // missing active Sensitive answer still needs its current consent state.
        var activeGrants = await CurrentAsync(member, active.Where(definition => definition.PiiCategory == PersonalDataCategory.Sensitive
            && !grants.ContainsKey(definition.DefinitionId)).ToArray(), cancellationToken);
        foreach (var item in activeGrants) grants.Add(item.Key, item.Value);
        var fields = new List<MembershipCustomFieldDto>();
        foreach (var value in stored)
        {
            var definition = retained.Single(definition => definition.DefinitionId == value.DefinitionId);
            var withheld = definition.PiiCategory == PersonalDataCategory.Sensitive
                && (!grants.TryGetValue(definition.DefinitionId, out var consent) || consent is not { IsGranted: true });
            fields.Add(new(value.ConceptKey, value.DefinitionId, value.SchemaVersion,
                withheld ? "Withheld" : definition.Status == "Active" ? "Current" : "Retired", definition.IsRequired,
                withheld ? null : value.Envelope.Clone(), Metadata(definition), Evidence(definition.DefinitionId)));
        }
        foreach (var definition in active)
            if (!stored.Any(value => value.DefinitionId == definition.DefinitionId && value.SchemaVersion == definition.SchemaVersion))
                fields.Add(new(definition.Key, definition.DefinitionId, definition.SchemaVersion, "Missing", definition.IsRequired, null, Metadata(definition), Evidence(definition.DefinitionId)));
        return Result<MembershipCustomFieldsDto>.Ok(new(member.Id.Value, member.Version,
            fields.OrderBy(field => field.ConceptKey, StringComparer.Ordinal).ThenBy(field => field.SchemaVersion).ToArray()));
        MembershipCustomFieldConsent? Evidence(Guid definitionId) => grants.TryGetValue(definitionId, out var item) && item is not null
            ? new(item.EvidenceId, item.Sequence, item.IsGranted) : null;
    }

    private static MembershipCustomFieldMetadata Metadata(CustomFieldDefinitionDto definition) => new(
        definition.JsonSchema, definition.Labels, definition.PiiCategory, definition.Sort,
        definition.PurposeKey, definition.Notices, definition.FormLabels?.Select(control => new MembershipCustomFieldControlLabels(
            control.Location, control.Labels, control.Choices)).ToArray(), definition.Status);

    private async Task<Dictionary<Guid, MembershipFieldConsentSnapshot?>> CurrentAsync(Membership member,
        CustomFieldDefinitionDto[] sensitive, CancellationToken cancellationToken)
    {
        if (sensitive.Length == 0) return [];
        if (sensitive.Any(definition => definition.PurposeKey is null))
            throw new InvalidOperationException("A sensitive revision has no purpose.");
        var tuples = sensitive.Select(definition => new MembershipFieldConsentTuple(member.Id.Value, member.UserId,
            definition.DefinitionId, definition.SchemaVersion, definition.PurposeKey!)).ToArray();
        var current = await consents.ReadCurrentBatchAsync(tuples, cancellationToken);
        if (current.Count != tuples.Length) throw new InvalidOperationException("Consent batch shape is invalid.");
        return sensitive.Select((definition, index) => (definition.DefinitionId, Consent: current[index]))
            .ToDictionary(item => item.DefinitionId, item => item.Consent);
    }

    private static Result<IReadOnlyCollection<MembershipCustomFieldValue>> Fail(string key) =>
        Result<IReadOnlyCollection<MembershipCustomFieldValue>>.Fail(new Error(LocalizedMessage.Of(key)));
}
