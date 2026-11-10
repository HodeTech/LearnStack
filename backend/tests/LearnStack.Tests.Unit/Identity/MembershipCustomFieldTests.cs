using System.Text.Json;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.CustomFields;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class MembershipCustomFieldTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly DateTimeOffset At = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly IdentityMutationLockPlan Fence = new([], Tenant, IdentityTenantLockMode.Shared);

    [Fact]
    public async Task Required_current_set_counts_unchanged_current_pins_but_not_retired_predecessors()
    {
        var old = Definition("answer", required: true) with { Status = "Deprecated" };
        var current = Definition("answer", required: true) with { SchemaVersion = 2 };
        var other = Definition("other");
        var member = Member();
        Store(member, Value(old, "old"));
        var service = Service([old, current, other]);

        var refused = await service.ApplyAsync(member, Patch(Set(other, "new")), Fence, default);
        Assert.True(refused.IsFailure);
        Assert.Equal("business_rule_violation", refused.Error!.Code);
        var replaced = await service.ApplyAsync(member, new([Set(other, "new"), Set(current, "replacement")], []), Fence, default);
        Assert.True(replaced.IsSuccess);
        Assert.Equal(current.DefinitionId, Assert.Single(replaced.Value, slot => slot.ConceptKey == "answer").DefinitionId);
        Assert.True(member.SetCustomFields(replaced.Value, At.AddSeconds(1), Actor).IsSuccess);
        Assert.True((await service.ApplyAsync(member, Patch(Set(other, "changed")), Fence, default)).IsSuccess);
    }

    [Fact]
    public async Task Removal_only_can_leave_required_form_incomplete_and_stale_removal_refuses_replacement()
    {
        var required = Definition("required", required: true);
        var retained = Definition("retained") with { Status = "Deprecated" };
        var member = Member();
        Store(member, Value(retained, "history"));
        var service = Service([required, retained]);
        var result = await service.ApplyAsync(member, new([], [new(retained.Key, retained.DefinitionId, retained.SchemaVersion)]), Fence, default);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
        var stale = await service.ApplyAsync(member, new([], [new(retained.Key, Guid.CreateVersion7(), retained.SchemaVersion)]), Fence, default);
        Assert.True(stale.IsFailure);
        Assert.Equal("concurrency_conflict", stale.Error!.Code);
    }

    [Fact]
    public async Task Withdrawal_withholds_retained_value_and_reconsent_restores_without_rewriting_original_evidence()
    {
        var definition = Definition("sensitive", sensitive: true, required: true);
        var original = Guid.CreateVersion7();
        var latest = Guid.CreateVersion7();
        var member = Member();
        Store(member, Value(definition, "private", original));
        var consents = new ConsentReader { Current = new(latest, 2, false, "en", "hash") };
        var service = Service([definition], consents);
        var hidden = await service.DiscloseAsync(member, default);
        var field = Assert.Single(hidden.Value!.Fields);
        Assert.Equal("Withheld", field.State);
        Assert.Null(field.Envelope);
        Assert.Equal(original, Assert.Single(MembershipCustomFieldValues.Read(member.Profile.CustomFieldsJson)).ConsentEvidenceId);
        Assert.True((await service.ApplyAsync(member, Patch(Set(definition, "changed", original)), Fence, default)).IsFailure);
        consents.Current = new(latest, 3, true, "en", "hash");
        var shown = await service.DiscloseAsync(member, default);
        Assert.Equal("private", Assert.Single(shown.Value!.Fields).Envelope!.Value.GetProperty("value").GetString());
        Assert.True((await service.ApplyAsync(member, Patch(Set(definition, "changed", original)), Fence, default)).IsFailure);
        var update = await service.ApplyAsync(member, Patch(Set(definition, "changed", latest)), Fence, default);
        Assert.True(update.IsSuccess);
        Assert.Equal(original, Assert.Single(update.Value).ConsentEvidenceId);
        var recapture = await service.ApplyAsync(member,
            new([Set(definition, "new capture", latest)], [new(definition.Key, definition.DefinitionId, definition.SchemaVersion)]), Fence, default);
        Assert.True(recapture.IsSuccess);
        Assert.Equal(latest, Assert.Single(recapture.Value).ConsentEvidenceId);
        Assert.All(consents.Observed, tuple =>
        {
            Assert.Equal(member.Id.Value, tuple.MembershipId);
            Assert.Equal(member.UserId, tuple.SubjectUserId);
            Assert.Equal(definition.DefinitionId, tuple.DefinitionId);
            Assert.Equal(definition.SchemaVersion, tuple.DefinitionVersion);
            Assert.Equal(definition.PurposeKey, tuple.PurposeKey);
        });
    }

    [Fact]
    public async Task Retired_sensitive_pin_keeps_old_classification_after_public_successor()
    {
        var old = Definition("answer", sensitive: true) with { Status = "Deprecated" };
        var successor = Definition("answer") with { SchemaVersion = 2 };
        var member = Member();
        Store(member, Value(old, "private", Guid.CreateVersion7()));
        var service = Service([old, successor]);
        var result = await service.DiscloseAsync(member, default);
        Assert.Collection(result.Value!.Fields,
            field => { Assert.Equal("Withheld", field.State); Assert.Null(field.Envelope); },
            field => { Assert.Equal("Missing", field.State); Assert.Equal(successor.DefinitionId, field.DefinitionId); });
        Assert.True((await service.ApplyAsync(member, Patch(Set(old, "modified", Guid.CreateVersion7())), Fence, default)).IsFailure);
        var replacement = await service.ApplyAsync(member, Patch(Set(successor, "public")), Fence, default);
        Assert.True(replacement.IsSuccess);
        Assert.Null(Assert.Single(replacement.Value).ConsentEvidenceId);
    }

    [Fact]
    public async Task Unchanged_sensitive_required_slot_counts_only_with_current_grant_and_revision_replacement_gets_fresh_evidence()
    {
        var definition = Definition("required", sensitive: true, required: true);
        var optional = Definition("optional");
        var original = Guid.CreateVersion7();
        var latest = Guid.CreateVersion7();
        var member = Member();
        Store(member, Value(definition, "private", original));
        var consents = new ConsentReader { Current = new(latest, 2, false, "en", "hash") };
        var service = Service([definition, optional], consents);
        Assert.True((await service.ApplyAsync(member, Patch(Set(optional, "answer")), Fence, default)).IsFailure);
        consents.Current = new(latest, 3, true, "en", "hash");
        var unchanged = await service.ApplyAsync(member, Patch(Set(optional, "answer")), Fence, default);
        Assert.True(unchanged.IsSuccess);
        Assert.Equal(original, Assert.Single(unchanged.Value, value => value.ConceptKey == definition.Key).ConsentEvidenceId);
        var successor = Definition("required", sensitive: true, required: true) with { SchemaVersion = 2 };
        var replaced = await Service([definition with { Status = "Deprecated" }, successor, optional], consents)
            .ApplyAsync(member, Patch(Set(successor, "replacement", latest)), Fence, default);
        Assert.True(replaced.IsSuccess);
        Assert.Equal(latest, Assert.Single(replaced.Value).ConsentEvidenceId);
        Assert.Equal(successor.DefinitionId, Assert.Single(replaced.Value).DefinitionId);
    }

    [Fact]
    public async Task Full_retained_slot_set_requires_explicit_removal_before_another_concept()
    {
        var retained = Enumerable.Range(0, MembershipCustomFieldValues.MaximumSlots)
            .Select(index => Definition($"field-{index}") with { Status = "Deprecated" }).ToArray();
        var added = Definition("added");
        var member = Member();
        Store(member, retained.Select(definition => Value(definition, "history")).ToArray());
        var service = Service([.. retained, added]);
        Assert.True((await service.ApplyAsync(member, Patch(Set(added, "new")), Fence, default)).IsFailure);
        var result = await service.ApplyAsync(member, new([Set(added, "new")],
            [new(retained[0].Key, retained[0].DefinitionId, retained[0].SchemaVersion)]), Fence, default);
        Assert.True(result.IsSuccess);
        Assert.Equal(MembershipCustomFieldValues.MaximumSlots, result.Value.Count);
        Assert.Contains(result.Value, value => value.DefinitionId == added.DefinitionId);
    }

    [Fact]
    public void Input_and_retained_collection_budgets_are_independent_and_do_not_echo_payload()
    {
        var definition = Definition("answer");
        var large = new string('x', 5000);
        var command = new SetOwnMembershipCustomFieldsCommand(0, Patch(Set(definition, large)));
        Assert.False(MembershipCustomFieldInput.Valid(command));
        Assert.DoesNotContain(large, command.ToString(), StringComparison.Ordinal);
        var member = Member();
        Store(member, Value(definition, large));
        Assert.True(member.Profile.CustomFieldsJson.Length > MembershipCustomFieldInput.MaximumSubmissionBytes);
        var oversized = Value(definition, new string('x', 33 * 1024));
        Assert.True(member.SetCustomFields([oversized], At, Actor).IsFailure);
        Assert.Single(MembershipCustomFieldValues.Read(member.Profile.CustomFieldsJson));
    }

    [Fact]
    public void Domain_enforces_slots_nulls_duplicates_and_revoked_successor_starts_empty()
    {
        var member = Member();
        var definition = Definition("answer");
        Assert.True(member.SetCustomFields([Value(definition, "one"), Value(definition, "two")], At, Actor).IsFailure);
        using var nullJson = JsonDocument.Parse("{\"value\":null}");
        Assert.False(MembershipCustomFieldInput.Valid(new SetOwnMembershipCustomFieldsCommand(0,
            Patch(new(definition.Key, definition.DefinitionId, 1, nullJson.RootElement.Clone(), null)))));
        Store(member, Value(definition, "retained"));
        Assert.True(member.Revoke(At.AddMinutes(1), Actor).IsSuccess);
        Assert.True(member.SetCustomFields([], At.AddMinutes(2), Actor).IsFailure);
        var successor = Member();
        Assert.Equal("[]", successor.Profile.CustomFieldsJson);
        Assert.NotEqual(member.Id, successor.Id);
    }

    [Fact]
    public async Task Missing_immutable_definition_fails_disclosure_closed()
    {
        var member = Member();
        Store(member, Value(Definition("unknown"), "private"));
        var response = await Service([]).DiscloseAsync(member, default);
        Assert.True(response.IsFailure);
        Assert.Equal("dependency_unavailable", response.Error!.Code);
    }

    [Fact]
    public void Stored_jsonb_expanded_numbers_reconstruct_before_logical_budgets_without_changing_pins_or_value()
    {
        var definition = Definition("tiny");
        var evidence = Guid.CreateVersion7();
        var expandedNumber = "0." + new string('0', 319) + "1";
        // Two admitted container levels, ten homogeneous items and ten numeric
        // children each: the stored decimal expansion exceeds the whole budget.
        var item = "{" + string.Join(',', Enumerable.Range(0, 10).Select(index => $"\"n{index}\":{expandedNumber}")) + "}";
        var stored = "[{\"conceptKey\":\"tiny\",\"definitionId\":\"" + definition.DefinitionId
            + "\",\"schemaVersion\":1,\"consentEvidenceId\":\"" + evidence
            + "\",\"envelope\":{\"value\":[" + string.Join(',', Enumerable.Repeat(item, 10)) + "]}}]";
        // Add whitespace as jsonb does so no raw-text boundary is accidentally used.
        stored = stored.Replace(":", ": ", StringComparison.Ordinal).Replace(",", ", ", StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(stored) > MembershipCustomFieldValues.MaximumCollectionBytes);
        var values = MembershipCustomFieldValues.Read(stored);
        var value = Assert.Single(values);
        Assert.Equal(evidence, value.ConsentEvidenceId);
        Assert.Equal(definition.DefinitionId, value.DefinitionId);
        Assert.Equal(1, value.SchemaVersion);
        Assert.Equal(1e-320, value.Envelope.GetProperty("value")[0].GetProperty("n0").GetDouble());
        var canonical = MembershipCustomFieldValues.Serialize(values.ToArray());
        Assert.NotNull(canonical);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(canonical) < MembershipCustomFieldInput.MaximumSubmissionBytes);
    }

    private static Membership Member()
    {
        var role = Role.Create(RoleId.From(Guid.CreateVersion7()), Tenant, BuiltInRoleKey.Learner, PermissionScope.Tenant, At, Actor).Value!;
        return Membership.Create(MembershipId.From(Guid.CreateVersion7()), Tenant, null, Actor, role, At, Actor).Value!;
    }
    private static void Store(Membership member, params MembershipCustomFieldValue[] values) =>
        Assert.True(member.SetCustomFields(values, At, Actor).IsSuccess);
    private static CustomFieldDefinitionDto Definition(string key, bool sensitive = false, bool required = false) =>
        new(Guid.CreateVersion7(), "Membership", key, 1, "Active", 1, false, "{}", new Dictionary<string, string> { ["en"] = "Label" },
            sensitive ? PersonalDataCategory.Sensitive : PersonalDataCategory.None, required, 0,
            sensitive ? "purpose" : null, sensitive ? new Dictionary<string, string> { ["en"] = "Notice" } : null, 1024);
    private static JsonElement Envelope(string text) => JsonSerializer.SerializeToElement(new { value = text });
    private static MembershipCustomFieldValue Value(CustomFieldDefinitionDto definition, string text, Guid? evidence = null) =>
        new(definition.Key, definition.DefinitionId, definition.SchemaVersion, Envelope(text), evidence);
    private static MembershipCustomFieldSet Set(CustomFieldDefinitionDto definition, string text, Guid? evidence = null) =>
        new(definition.Key, definition.DefinitionId, definition.SchemaVersion, Envelope(text), evidence);
    private static MembershipCustomFieldPatch Patch(MembershipCustomFieldSet value) => new([value], []);
    private static MembershipCustomFieldService Service(CustomFieldDefinitionDto[] definitions, ConsentReader? consents = null) =>
        new(new DefinitionReader(definitions), new AcceptingValidator(), consents ?? new ConsentReader());

    private sealed class AcceptingValidator : ICustomFieldValueValidator
    {
        public Result<string> ValidateAndNormalize(CustomFieldDefinitionDto definition, string envelopeJson) => Result<string>.Ok(envelopeJson);
    }
    private sealed class ConsentReader : IMembershipFieldConsentReader
    {
        public MembershipFieldConsentSnapshot? Current { get; set; }
        public List<MembershipFieldConsentTuple> Observed { get; } = [];
        public Task<MembershipFieldConsentSnapshot?> ReadCurrentAsync(MembershipFieldConsentTuple tuple, CancellationToken cancellationToken = default)
        { Observed.Add(tuple); return Task.FromResult(Current); }
        public Task<IReadOnlyList<MembershipFieldConsentSnapshot?>> ReadCurrentBatchAsync(IReadOnlyList<MembershipFieldConsentTuple> tuples,
            CancellationToken cancellationToken = default)
        { Observed.AddRange(tuples); return Task.FromResult<IReadOnlyList<MembershipFieldConsentSnapshot?>>(tuples.Select(_ => Current).ToArray()); }
    }
    private sealed class DefinitionReader(CustomFieldDefinitionDto[] definitions) : IExactCustomFieldDefinitionReader
    {
        public Task<CustomFieldDefinitionDto?> ReadAsync(CustomFieldDefinitionPin pin, CustomFieldResolution resolution,
            IdentityMutationLockPlan? fence, CancellationToken cancellationToken = default) =>
            Task.FromResult(definitions.SingleOrDefault(definition => definition.DefinitionId == pin.DefinitionId && definition.SchemaVersion == pin.SchemaVersion
                && (resolution == CustomFieldResolution.ExistingPin || definition.Status == "Active")));
        public Task<IReadOnlyList<CustomFieldDefinitionDto>> ReadBatchAsync(IReadOnlyCollection<CustomFieldDefinitionPin> pins,
            CustomFieldResolution resolution, IdentityMutationLockPlan? fence, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CustomFieldDefinitionDto>>(definitions.Where(definition => pins.Any(pin => pin.DefinitionId == definition.DefinitionId
                && pin.SchemaVersion == definition.SchemaVersion) && (resolution == CustomFieldResolution.ExistingPin || definition.Status == "Active")).ToArray());
        public Task<IReadOnlyList<CustomFieldDefinitionDto>> ReadActiveForDisplayAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CustomFieldDefinitionDto>>(definitions.Where(definition => definition.Status == "Active").ToArray());
        public Task<IReadOnlyList<CustomFieldDefinitionDto>> ReadActiveAsync(IdentityMutationLockPlan fence, CancellationToken cancellationToken = default) =>
            ReadActiveForDisplayAsync(cancellationToken);
    }
}
