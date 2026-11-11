using System.Collections.Immutable;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Audit;
using System.Text.Json;
using LearnStack.Application.Pipeline;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Results;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;
using static LearnStack.Tests.Integration.Database.MembershipCustomFieldFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class MembershipCustomFieldCommandTests(MembershipCustomFieldFixture fixture)
    : IClassFixture<MembershipCustomFieldFixture>
{
    [Fact]
    public async Task Custom_field_store_refuses_creation_and_stale_or_other_membership_instances()
    {
        var member = await fixture.Membership.AddMemberAsync();
        var other = await fixture.Membership.AddMemberAsync();
        var original = await fixture.Membership.ReadAsync(member.Membership);
        await using (var harness = await fixture.CreateAsync("custom-field-store-owner"))
        {
            await using var frame = await harness.Unit.BeginTransactionAsync();
            await harness.Unit.SetTenantContextAsync(harness.Services.GetRequiredService<ITenantContext>());
            await harness.Services.GetRequiredService<IIdentityMutationLocks>().AcquireAsync(
                new([], TenantId.From(SchemaFixture.TenantA), IdentityTenantLockMode.Shared));
            var reader = harness.Services.GetRequiredService<IMembershipAdministrationReader>();
            var store = harness.Services.GetRequiredService<IMembershipCustomFieldStore>();
            var id = MembershipId.From(member.Membership);
            var sameIdDetached = Assert.IsType<Membership>(await reader.ReadDetailAsync(id));
            var different = Assert.IsType<Membership>(await reader.ReadDetailAsync(MembershipId.From(other.Membership)));
            Assert.True(sameIdDetached.SetProfile("refused-store-canary", "en", DateTimeOffset.UtcNow,
                UserId.From(member.Actor)).IsSuccess);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(sameIdDetached));
            await store.LockAsync(id);
            var captured = Assert.IsType<Membership>(await store.ReadForWriteAsync(id));
            Assert.NotSame(captured, sameIdDetached);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(sameIdDetached));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(different));
            await store.LockAsync(id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(captured));
            var refreshed = Assert.IsType<Membership>(await store.ReadForWriteAsync(id));
            Assert.NotSame(captured, refreshed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(captured));
            await frame.FailAsync();
        }
        var persisted = await fixture.Membership.ReadAsync(member.Membership);
        Assert.Equal(original.DisplayName, persisted.DisplayName);
        Assert.Equal(original.PreferredLocale, persisted.PreferredLocale);
        Assert.Equal(original.Version, persisted.Version);
    }

    [Fact]
    public async Task Compact_numeric_input_cannot_expand_past_the_complete_submission_limit_after_normalization()
    {
        var member = await fixture.Membership.AddMemberAsync();
        const string shape = """
            {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"array","minItems":0,"maxItems":10,"items":{"type":"array","minItems":0,"maxItems":10,"items":{"type":"integer","minimum":0,"maximum":10000000000}}}},"required":["value"],"additionalProperties":false}
            """;
        var definitions = new List<LearnStack.Modules.Customization.Application.Contracts.CustomFields.CustomFieldDefinitionDto>();
        try
        {
            for (var n = 0; n < 3; n++)
                definitions.Add(await fixture.PublishAsync(Input("numeric-budget-" + Guid.NewGuid().ToString("N")) with
                {
                    JsonSchema = shape,
                    FormLabels = [new("/0", new Dictionary<string,string>{{"en","Row"}}),
                        new("/0/0",new Dictionary<string,string>{{"en","Number"}})],
                }));
            var row = "[" + string.Join(',', Enumerable.Repeat("1e10", 10)) + "]";
            using var envelope = JsonDocument.Parse("{\"value\":[" + string.Join(',', Enumerable.Repeat(row, 10)) + "]}");
            var set = definitions.Select(d => new MembershipCustomFieldSet(d.Key, d.DefinitionId, d.SchemaVersion, envelope.RootElement.Clone(), null)).ToImmutableArray();
            var patch = new MembershipCustomFieldPatch(set, []);
            Assert.True(LearnStack.Modules.Identity.Application.CustomFields.MembershipCustomFieldInput.Valid(new SetMembershipCustomFieldsCommand(member.Membership, 0, patch)));
            var refused = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 0, patch), "value-expanded-submission");
            Assert.Equal("limit_exceeded", refused.Error!.Code);
            Assert.Equal(0, (await fixture.Membership.ReadAsync(member.Membership)).Version);
            Assert.Empty(MembershipCustomFieldValues.Read(await fixture.StoredAsync(member.Membership)));
            var accepted = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 0, new([set[0]], [])), "value-expanded-submission-positive");
            Assert.True(accepted.IsSuccess, accepted.Error?.Code);
            Assert.Single(MembershipCustomFieldValues.Read(await fixture.StoredAsync(member.Membership)));
        }
        finally { foreach (var definition in definitions) await fixture.RetireAsync(definition); }
    }

    [Fact]
    public async Task Current_non_sensitive_value_round_trips_through_commands_and_explicit_removal()
    {
        var member = await fixture.Membership.AddMemberAsync();
        var definition = await fixture.PublishAsync(Input("roundtrip-" + Guid.NewGuid().ToString("N")));
        var saved = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 0,
            new([Set(definition)], [])), "value-roundtrip-save");
        Assert.True(saved.IsSuccess, saved.Error?.Code);
        Assert.Equal(1, saved.Value.Version);
        var read = await fixture.SendAsync(new GetOwnMembershipCustomFieldsQuery(), "value-roundtrip-own", member.Actor);
        Assert.True(read.IsSuccess, read.Error?.Code);
        var field = Assert.Single(read.Value.Fields, value => value.DefinitionId == definition.DefinitionId);
        Assert.Equal("Current", field.State);
        Assert.True(field.Envelope!.Value.GetProperty("value").GetBoolean());
        Assert.Equal(definition.SchemaVersion, field.SchemaVersion);
        var stored = await fixture.StoredAsync(member.Membership);
        var unchanged = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 1,
            new([Set(definition)], [])), "value-roundtrip-unchanged");
        Assert.True(unchanged.IsSuccess, unchanged.Error?.Code);
        Assert.Equal(1, unchanged.Value.Version);
        Assert.Equal(1, (await fixture.Membership.ReadAsync(member.Membership)).Version);
        Assert.Equal(stored, await fixture.StoredAsync(member.Membership));
        using var unchangedAudit = JsonDocument.Parse(Assert.Single(await fixture.Membership.AuditAsync(
            "value-roundtrip-unchanged", "identity.membership.custom_fields_write")));
        Assert.Equal("success", unchangedAudit.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("Must", unchangedAudit.RootElement.GetProperty("class").GetString());
        Assert.Equal(member.Membership.ToString("D"), unchangedAudit.RootElement.GetProperty("id").GetString());
        var removed = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 1,
            new([], [Remove(definition)])), "value-roundtrip-remove");
        Assert.True(removed.IsSuccess, removed.Error?.Code);
        Assert.Equal(2, removed.Value.Version);
        Assert.Empty(MembershipCustomFieldValues.Read(await fixture.StoredAsync(member.Membership)));
        var audit = Assert.Single(await fixture.Membership.AuditAsync("value-roundtrip-save", "identity.membership.custom_fields_write"));
        Assert.DoesNotContain("\"value\": true", audit, StringComparison.Ordinal);
        using var row = JsonDocument.Parse(audit);
        Assert.Equal("Must", row.RootElement.GetProperty("class").GetString());
        Assert.Equal(member.Membership.ToString("D"), row.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Own_read_authority_does_not_grant_custom_value_write_authority()
    {
        var member = await fixture.Membership.AddMemberAsync();
        var definition = await fixture.PublishAsync(Input("authority-" + Guid.NewGuid().ToString("N"), category: PersonalDataCategory.Sensitive));
        await fixture.Membership.SetRoleGrantsAsync(BuiltInRoleKey.Learner, PermissionScope.Tenant);
        var consent = await fixture.SendAsync(new GrantOwnMembershipFieldConsentCommand(definition.DefinitionId,
            definition.SchemaVersion, "en", MembershipFieldConsentNotice.ComputeHash(definition.Notices!["en"]), 0, null, true),
            "value-learner-consent-without-write", member.Actor);
        Assert.True(consent.IsSuccess, consent.Error?.Code);
        Assert.True((await fixture.SendAsync(new GetOwnMembershipCustomFieldsQuery(), "value-learner-read", member.Actor)).IsSuccess);
        var denied = await fixture.SendAsync(new SetOwnMembershipCustomFieldsCommand(0, new([Set(definition, evidence: consent.Value!.EvidenceId)], [])),
            "value-learner-own-write", member.Actor);
        Assert.Equal("forbidden", denied.Error!.Code);
        Assert.Equal(0, (await fixture.Membership.ReadAsync(member.Membership)).Version);
        Assert.Empty(MembershipCustomFieldValues.Read(await fixture.StoredAsync(member.Membership)));
        await fixture.Membership.SetRoleGrantsAsync(BuiltInRoleKey.Learner, PermissionScope.Tenant,
            IdentityFoundationManifest.Version1.Templates.Single(template => template.BuiltInKey == BuiltInRoleKey.Learner
                && template.Scope == PermissionScope.Tenant).Grants.ToArray());
    }

    [Fact]
    public async Task Foreign_tenant_and_foreign_organization_targets_refuse_before_value_mutation()
    {
        var definition = await fixture.PublishAsync(Input("isolation-" + Guid.NewGuid().ToString("N")));
        var foreignTenant = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(AdminMembershipB, 0,
            new([Set(definition)], [])), "value-foreign-tenant");
        Assert.Equal("not_found", foreignTenant.Error!.Code);
        var branch = await fixture.Membership.AddMemberAsync(SchemaFixture.OrgA2);
        var foreignOrg = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(branch.Membership, 0,
            new([Set(definition)], [])), "value-foreign-org", organization: SchemaFixture.OrgA1);
        Assert.Equal("not_found", foreignOrg.Error!.Code);
        Assert.Equal(0, (await fixture.Membership.ReadAsync(branch.Membership, SchemaFixture.OrgA2)).Version);
        var foreignDefinition = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(AdminMembershipB, 0,
            new([Set(definition)], [])), "value-foreign-definition", AdminB, tenant: SchemaFixture.TenantB);
        Assert.Equal("business_rule_violation", foreignDefinition.Error!.Code);
        Assert.Equal(0, (await fixture.Membership.ReadAsync(AdminMembershipB, tenant: SchemaFixture.TenantB)).Version);
    }

    [Fact]
    public async Task Sensitive_write_requires_latest_personal_grant_and_withdrawal_withholds_but_allows_removal()
    {
        var member = await fixture.Membership.AddMemberAsync();
        var definition = await fixture.PublishAsync(Input("sensitive-" + Guid.NewGuid().ToString("N"), category: PersonalDataCategory.Sensitive));
        var missing = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 0,
            new([Set(definition)], [])), "value-sensitive-no-grant");
        Assert.Equal("business_rule_violation", missing.Error!.Code);
        var granted = await fixture.SendAsync(new GrantOwnMembershipFieldConsentCommand(definition.DefinitionId,
            definition.SchemaVersion, "en", MembershipFieldConsentNotice.ComputeHash(definition.Notices!["en"]), 0, null, true),
            "value-sensitive-personal-grant", member.Actor);
        Assert.True(granted.IsSuccess, granted.Error?.Code);
        var wrongEvidence = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 0,
            new([Set(definition, evidence: Guid.CreateVersion7())], [])), "value-sensitive-wrong-grant");
        Assert.Equal("business_rule_violation", wrongEvidence.Error!.Code);
        var saved = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 0,
            new([Set(definition, evidence: granted.Value.EvidenceId)], [])), "value-sensitive-granted");
        Assert.True(saved.IsSuccess, saved.Error?.Code);
        await fixture.AppendWithdrawalAsync(member.Membership, member.Actor, definition, granted.Value.EvidenceId);
        var read = await fixture.SendAsync(new GetMembershipCustomFieldsQuery(member.Membership), "value-sensitive-withheld");
        Assert.True(read.IsSuccess, read.Error?.Code);
        var withheld = Assert.Single(read.Value.Fields, field => field.DefinitionId == definition.DefinitionId);
        Assert.Equal("Withheld", withheld.State);
        Assert.Null(withheld.Envelope);
        var retained = Assert.Single(MembershipCustomFieldValues.Read(await fixture.StoredAsync(member.Membership)));
        Assert.Equal(granted.Value.EvidenceId, retained.ConsentEvidenceId);
        Assert.True(retained.Envelope.GetProperty("value").GetBoolean());
        var staleGrant = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 1,
            new([Set(definition, false, granted.Value.EvidenceId)], [])), "value-sensitive-withdrawn-save");
        Assert.Equal("business_rule_violation", staleGrant.Error!.Code);
        var removed = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 1,
            new([], [Remove(definition)])), "value-sensitive-withdrawn-remove");
        Assert.True(removed.IsSuccess, removed.Error?.Code);
        Assert.Empty(MembershipCustomFieldValues.Read(await fixture.StoredAsync(member.Membership)));
    }

    [Fact]
    public async Task Set_requires_the_complete_current_required_set_but_removal_only_may_leave_it_incomplete()
    {
        var member = await fixture.Membership.AddMemberAsync();
        var optional = await fixture.PublishAsync(Input("optional-" + Guid.NewGuid().ToString("N")));
        var first = await fixture.PublishAsync(Input("required-a-" + Guid.NewGuid().ToString("N"), true));
        var second = await fixture.PublishAsync(Input("required-b-" + Guid.NewGuid().ToString("N"), true));
        try
        {
            var incomplete = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 0,
                new([Set(first)], [])), "value-incomplete-required");
            Assert.Equal("business_rule_violation", incomplete.Error!.Code);
            Assert.Empty(MembershipCustomFieldValues.Read(await fixture.StoredAsync(member.Membership)));
            var complete = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 0,
                new([Set(first), Set(second), Set(optional)], [])), "value-complete-required");
            Assert.True(complete.IsSuccess, complete.Error?.Code);
            var remove = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 1,
                new([], [Remove(first)])), "value-remove-required");
            Assert.True(remove.IsSuccess, remove.Error?.Code);
            var stillIncomplete = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 2,
                new([Set(optional, false)], [])), "value-missing-required-blocks-optional");
            Assert.Equal("business_rule_violation", stillIncomplete.Error!.Code);
            Assert.Equal(2, (await fixture.Membership.ReadAsync(member.Membership)).Version);
        }
        finally
        {
            await fixture.RetireAsync(first);
            await fixture.RetireAsync(second);
        }
    }

    [Fact]
    public async Task Retired_pins_keep_exact_interpretation_until_explicit_replacement_and_stale_removal_refuses()
    {
        var member = await fixture.Membership.AddMemberAsync();
        var first = await fixture.PublishAsync(Input("history-" + Guid.NewGuid().ToString("N")));
        Assert.True((await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 0,
            new([Set(first)], [])), "value-history-first")).IsSuccess);
        var successor = await fixture.PublishAsync(Input(first.Key, version: 2), first);
        var read = await fixture.SendAsync(new GetMembershipCustomFieldsQuery(member.Membership), "value-history-read");
        Assert.True(read.IsSuccess, read.Error?.Code);
        Assert.Equal("Retired", Assert.Single(read.Value.Fields, value => value.DefinitionId == first.DefinitionId).State);
        Assert.Equal("Missing", Assert.Single(read.Value.Fields, value => value.DefinitionId == successor.DefinitionId).State);
        Assert.Equal("business_rule_violation", (await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 1,
            new([Set(first, false)], [])), "value-history-readonly")).Error!.Code);
        Assert.True((await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 1,
            new([Set(successor, false)], [])), "value-history-replace")).IsSuccess);
        Assert.Equal("concurrency_conflict", (await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 2,
            new([], [Remove(first)])), "value-history-stale-remove")).Error!.Code);
        Assert.Equal(successor.DefinitionId, Assert.Single(MembershipCustomFieldValues.Read(await fixture.StoredAsync(member.Membership))).DefinitionId);
    }

    [Fact]
    public async Task Classification_downgrade_cannot_disclose_a_withdrawn_sensitive_historical_pin()
    {
        var member = await fixture.Membership.AddMemberAsync();
        var sensitive = await fixture.PublishAsync(Input("downgrade-" + Guid.NewGuid().ToString("N"), category: PersonalDataCategory.Sensitive));
        var granted = await fixture.SendAsync(new GrantOwnMembershipFieldConsentCommand(sensitive.DefinitionId,
            sensitive.SchemaVersion, "en", MembershipFieldConsentNotice.ComputeHash(sensitive.Notices!["en"]), 0, null, true),
            "value-downgrade-grant", member.Actor);
        Assert.True(granted.IsSuccess, granted.Error?.Code);
        Assert.True((await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 0,
            new([Set(sensitive, evidence: granted.Value.EvidenceId)], [])), "value-downgrade-save")).IsSuccess);
        var successor = await fixture.PublishAsync(Input(sensitive.Key, version: 2), sensitive);
        await fixture.AppendWithdrawalAsync(member.Membership, member.Actor, sensitive, granted.Value.EvidenceId);
        var read = await fixture.SendAsync(new GetOwnMembershipCustomFieldsQuery(), "value-downgrade-read", member.Actor);
        Assert.True(read.IsSuccess, read.Error?.Code);
        var historical = Assert.Single(read.Value.Fields, field => field.DefinitionId == sensitive.DefinitionId);
        Assert.Equal(PersonalDataCategory.Sensitive, historical.Definition.PiiCategory);
        Assert.Equal("Withheld", historical.State);
        Assert.Null(historical.Envelope);
        var current = Assert.Single(read.Value.Fields, field => field.DefinitionId == successor.DefinitionId);
        Assert.Equal(PersonalDataCategory.None, current.Definition.PiiCategory);
        Assert.Equal("Missing", current.State);
    }

    [Fact]
    public async Task Withdrawn_unchanged_required_sensitive_pin_makes_the_next_set_incomplete_but_removal_remains_available()
    {
        var member = await fixture.Membership.AddMemberAsync();
        var required = await fixture.PublishAsync(Input("required-sensitive-" + Guid.NewGuid().ToString("N"), true, PersonalDataCategory.Sensitive));
        var optional = await fixture.PublishAsync(Input("required-sensitive-other-" + Guid.NewGuid().ToString("N")));
        try
        {
            var granted = await fixture.SendAsync(new GrantOwnMembershipFieldConsentCommand(required.DefinitionId,
                required.SchemaVersion, "en", MembershipFieldConsentNotice.ComputeHash(required.Notices!["en"]), 0, null, true),
                "value-required-sensitive-grant", member.Actor);
            Assert.True(granted.IsSuccess, granted.Error?.Code);
            Assert.True((await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 0,
                new([Set(required, evidence: granted.Value.EvidenceId), Set(optional)], [])), "value-required-sensitive-save")).IsSuccess);
            await fixture.AppendWithdrawalAsync(member.Membership, member.Actor, required, granted.Value.EvidenceId);
            var refused = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 1,
                new([Set(optional, false)], [])), "value-required-sensitive-incomplete");
            Assert.Equal("business_rule_violation", refused.Error!.Code);
            Assert.True((await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 1,
                new([], [Remove(optional)])), "value-required-sensitive-remove-other")).IsSuccess);
            var retained = Assert.Single(MembershipCustomFieldValues.Read(await fixture.StoredAsync(member.Membership)));
            Assert.Equal(granted.Value.EvidenceId, retained.ConsentEvidenceId);
            Assert.Equal(required.DefinitionId, retained.DefinitionId);
        }
        finally { await fixture.RetireAsync(required); }
    }

    [Fact]
    public async Task MUST_audit_failure_rolls_back_the_custom_values_and_membership_version()
    {
        var member = await fixture.Membership.AddMemberAsync();
        var definition = await fixture.PublishAsync(Input("audit-value-" + Guid.NewGuid().ToString("N")));
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.OwnerConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE FUNCTION fn_custom_value_audit_proof() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.operation='identity.membership.custom_fields_write' AND NEW.correlation_id='custom-value-audit-refused' THEN
                    RAISE EXCEPTION 'fixed custom value audit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER tg_custom_value_audit_proof BEFORE INSERT ON audit_log
                FOR EACH ROW EXECUTE FUNCTION fn_custom_value_audit_proof();
            """);
        try
        {
            Result<MembershipMutationResult>? published = null;
            var failure = await Assert.ThrowsAsync<AuditWriteFailedException>(async () => published = await fixture.SendAsync(
                new SetMembershipCustomFieldsCommand(member.Membership, 0, new([Set(definition)], [])), "custom-value-audit-refused"));
            Assert.Equal("audit_unavailable", failure.Error!.Code);
            Assert.Null(published);
            Assert.Equal(0, (await fixture.Membership.ReadAsync(member.Membership)).Version);
            Assert.Empty(MembershipCustomFieldValues.Read(await fixture.StoredAsync(member.Membership)));
        }
        finally
        {
            await ExecuteAsync(owner, null, "DROP TRIGGER tg_custom_value_audit_proof ON audit_log; DROP FUNCTION fn_custom_value_audit_proof()");
        }
    }
}
