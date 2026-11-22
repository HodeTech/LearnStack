using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>The pinned revision of one stored custom-field value: its labels and classification (ADR-0107 § 3).</summary>
public sealed record MembershipFieldRevision(Guid DefinitionId, int SchemaVersion, IReadOnlyDictionary<string, string> Labels,
    PersonalDataCategory Classification)
{
    public override string ToString() => nameof(MembershipFieldRevision);
}

/// <summary>
/// Reads the exact revisions a Membership's stored custom-field values are pinned to, through Customization's
/// application contract, so the export names each value's definition revision, labels and classification.
/// </summary>
/// <remarks>
/// The contract is the sanctioned cross-module read (ADR-0010); Identity's infrastructure reaches it only through
/// this application service. A revision that is no longer readable is simply absent, and the value is still
/// exported, because a missing label is not a reason to withhold the person's own data.
/// </remarks>
public sealed class MembershipFieldRevisionReader(IExactCustomFieldDefinitionReader definitions)
{
    public async Task<IReadOnlyList<MembershipFieldRevision>> ReadAsync(IReadOnlyCollection<MembershipCustomFieldValue> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return [];
        }

        var revisions = await definitions.ReadBatchAsync(
            values.Select(value => new CustomFieldDefinitionPin(value.DefinitionId, value.SchemaVersion)).ToArray(),
            CustomFieldResolution.ExistingPin, null, cancellationToken).ConfigureAwait(false);
        return [.. revisions.Select(revision => new MembershipFieldRevision(revision.DefinitionId, revision.SchemaVersion,
            revision.Labels, revision.PiiCategory))];
    }
}
