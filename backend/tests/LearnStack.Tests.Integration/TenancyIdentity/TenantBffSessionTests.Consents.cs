using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;
using LearnStack.Infrastructure.Validation;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Customization.Application.CustomFields;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Npgsql;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantBffSessionTests
{
    [Fact]
    public async Task Reconciled_warm_realm_retained_consent_entry_reaches_nonempty_history_before_selection_on_both_hosts()
    {
        var root = RepositoryRoot();
        var historical = JsonNode.Parse(await TenantRealmStockBaselineTests.HistoricalImportAsync(root))!.AsObject();
        var passwords = historical["users"]!.AsArray().ToDictionary(user => user!["username"]!.GetValue<string>(),
            user => user!["credentials"]![0]!["value"]!.GetValue<string>(), StringComparer.Ordinal);
        await using var provider = await KeycloakFixture.StartAsync(historical.ToJsonString());
        var resources = await provider.StockProviderResourcesAsync();
        try
        {
            var reconciled = await KeycloakReconciliationDriver.RunAsync(provider);
            reconciled.Success.Should().BeTrue("closed warm consent qualification: {0}/{1}", reconciled.Stage, reconciled.Reason);
            reconciled.PreparationDispatched.Should().BeTrue();
            await using var database = await KeycloakIdentityDatabase.CreateAsync(publicDatabase, provider);
            await SeedAdministrationOriginsAsync(database);
            (await SeedRetainedConsentProofAsync(database)).Should().Be(SeedData.IdentityMemberships.Length);
            (await SeedRetainedConsentProofAsync(database)).Should().Be(0,
                "repeated fixture setup reuses immutable metadata/evidence instead of appending another grant");
            var auditBefore = await AuditCountsAsync(database);
            foreach (var tenant in SeedData.All)
            {
                var identity = SeedData.IdentityMemberships.Single(member => member.Identity.Tenant == tenant && member.IsFirstAdmin).Identity;
                var password = passwords.GetValueOrDefault(identity.Username) ?? KeycloakFixture.DemoPassword;
                var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                var nativeSourceSecret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
                await using var api = StartApi(database, secret, nativeSourceSecret, provider);
                await using var native = await BffNativeDriver.StartAsync(root,
                    NativeConfiguration(provider, database, api, secret, nativeSourceSecret));
                using var browser = provider.Browser();
                var cookies = new BffWireCookies();
                var session = await LoginAsync(native, browser, identity, password, cookies, api.Observed,
                    verifyConsentEntry: true);
                session.GetProperty("state").GetString().Should().Be("authenticated");
                await native.CompleteAsync();
            }
            (await AuditCountsAsync(database)).Should().Equal(auditBefore.Select(count => count + 1),
                "retained discovery/history are Off; only the subsequent explicit session admission adds its MUST row on each host");
            output.WriteLine("Real warm retained-consent proof: management hosts=2; credentialed scopes/nonempty own history before session selection=true; unchanged cookies/no public-site or AccountSite admission=true.");
        }
        finally
        {
            var cleanup = Stopwatch.StartNew();
            await provider.DisposeAsync();
            await KeycloakFixture.AssertStockResourcesAbsentAsync(resources);
            output.WriteLine("Consent provider joined cleanup={0:F3}s; two owned containers and network independently absent=true.", cleanup.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Fixture arrangement only, before the journey's audit baseline. The later browser-owned
    /// entry credential still traverses the production consent producer and retained reader.
    /// These app-role/domain-valid inserts do not claim an HTTP grant or its runtime audit proof.
    /// </summary>
    private static async Task<int> SeedRetainedConsentProofAsync(IdentityPublicReadFixture database)
    {
        const string key = "native-retained-consent-proof";
        const string purpose = "native-retained-proof-purpose";
        const string notice = "Native retained consent fixture notice";
        const string rawSchema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false}""";
        var profile = new CustomFieldFormProfile(new JsonSchemaNetValidator());
        var admitted = profile.Admit(rawSchema);
        admitted.IsSuccess.Should().BeTrue("fixture metadata uses the actual finite schema admission");
        var shape = admitted.Value!;
        var clock = new SystemClock();
        var appended = 0;
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        foreach (var tenant in SeedData.All)
        {
            await using var transaction = await app.BeginTransactionAsync();
            await ScopeAsync(app, transaction, tenant.TenantId.Value);
            await LockAsync(app, transaction, tenant.TenantId.Value, null, true);
            var locale = await ScalarAsync<string>(app, transaction,
                "SELECT locale FROM public.tenant_locales WHERE tenant_id=@tenant AND is_default AND is_enabled",
                ("tenant", tenant.TenantId.Value));
            locale.Should().Be(tenant.Curriculum!.Locales.Single(value => value.IsDefault).Locale,
                "the fixture pins the actual enabled default locale, not a hard-coded English notice");
            var labels = new Dictionary<string, string> { [locale] = "Native retained consent fixture" };
            var notices = new Dictionary<string, string> { [locale] = notice };
            var input = new CustomFieldDefinitionInput("Membership", key, 1, shape.Schema, labels,
                PersonalDataCategory.Sensitive, false, 0, purpose, notices);
            CustomFieldFormProfile.FitsAuthoringInput(input).Should().BeTrue();
            CustomFieldFormProfile.FitsSubmission([(key, shape.MaximumValueBytes)]).Should().BeTrue();
            var actor = SeedData.IdentityMemberships.Single(value => value.Identity.Tenant == tenant && value.IsFirstAdmin).Identity.UserId;
            var existing = await ScalarAsync<Guid?>(app, transaction, """
                SELECT id FROM public.tenant_custom_field_defs
                WHERE tenant_id=@tenant AND target_entity='Membership' AND key=@key AND schema_version=1
                """, ("tenant", tenant.TenantId.Value), ("key", key));
            var definition = TenantCustomFieldDef.Create(TenantCustomFieldDefId.From(existing ?? Guid.CreateVersion7()),
                tenant.TenantId, key, 1, shape.Schema, JsonSerializer.Serialize(labels),
                PersonalDataCategory.Sensitive, false, 0, purpose, JsonSerializer.Serialize(notices),
                shape.MaximumValueBytes, clock, actor);
            definition.Publish(clock, actor);
            if (existing is null)
            {
                (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.tenant_custom_field_defs WHERE status='Active' AND deleted_at IS NULL"))
                    .Should().BeLessThan(20);
                (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.tenant_custom_field_defs WHERE status='Draft' AND deleted_at IS NULL"))
                    .Should().BeLessThan(20);
                (await ScalarAsync<long>(app, transaction, "SELECT count(DISTINCT key) FROM public.tenant_custom_field_defs"))
                    .Should().BeLessThan(100);
                (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.tenant_custom_field_defs"))
                    .Should().BeLessThan(1000);
                await ExecuteAsync(app, transaction, """
                    INSERT INTO public.tenant_custom_field_defs
                      (id,tenant_id,target_entity,key,schema_version,status,json_schema,labels_json,pii_category,is_required,
                       sort,maximum_value_bytes,purpose_key,notices_json,created_at,created_by,row_version)
                    VALUES(@id,@tenant,'Membership',@key,@version,'Draft',CAST(@schema AS jsonb),CAST(@labels AS jsonb),
                       'Sensitive',false,@sort,@maximum,@purpose,CAST(@notices AS jsonb),@at,@actor,0);
                    UPDATE public.tenant_custom_field_defs SET status='Active',row_version=1,updated_at=@updated,updated_by=@actor
                      WHERE id=@id;
                    """, ("id", definition.Id.Value), ("tenant", tenant.TenantId.Value), ("key", definition.Key),
                    ("version", definition.SchemaVersion), ("schema", definition.JsonSchema), ("labels", definition.LabelsJson),
                    ("sort", definition.Sort), ("maximum", definition.MaximumValueBytes), ("purpose", definition.PurposeKey),
                    ("notices", definition.NoticesJson), ("at", definition.CreatedAt), ("updated", definition.UpdatedAt), ("actor", actor.Value));
            }
            (await ScalarAsync<long>(app, transaction, """
                SELECT count(*) FROM public.tenant_custom_field_defs
                WHERE id=@id AND tenant_id=@tenant AND target_entity='Membership' AND key=@key AND schema_version=1
                  AND status='Active' AND deleted_at IS NULL AND pii_category='Sensitive' AND NOT is_required
                  AND json_schema=CAST(@schema AS jsonb) AND labels_json=CAST(@labels AS jsonb)
                  AND purpose_key=@purpose AND notices_json=CAST(@notices AS jsonb) AND maximum_value_bytes=@maximum
                """, ("id", definition.Id.Value), ("tenant", tenant.TenantId.Value), ("key", key),
                ("schema", definition.JsonSchema), ("labels", definition.LabelsJson), ("purpose", purpose),
                ("notices", definition.NoticesJson), ("maximum", definition.MaximumValueBytes))).Should().Be(1,
                    "repeated setup must reuse the exact immutable fixture definition");
            foreach (var member in SeedData.IdentityMemberships.Where(value => value.Identity.Tenant == tenant))
            {
                await ScopeAsync(app, transaction, tenant.TenantId.Value, member.OrganizationId?.Value);
                (await ScalarAsync<long>(app, transaction, """
                    SELECT count(*) FROM public.memberships WHERE id=@member AND tenant_id=@tenant AND user_id=@actor
                      AND organization_id IS NOT DISTINCT FROM @organization AND status='Active' AND deleted_at IS NULL
                    """, ("member", member.MembershipId), ("tenant", tenant.TenantId.Value),
                    ("actor", member.Identity.UserId.Value), ("organization", member.OrganizationId?.Value))).Should().Be(1);
                var exists = await ScalarAsync<long>(app, transaction, """
                    SELECT count(*) FROM public.membership_field_consents
                    WHERE tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization
                      AND membership_id=@member AND subject_user_id=@actor AND definition_id=@definition
                    """, ("tenant", tenant.TenantId.Value), ("organization", member.OrganizationId?.Value),
                    ("member", member.MembershipId), ("actor", member.Identity.UserId.Value), ("definition", definition.Id.Value));
                if (exists == 0)
                {
                    var consent = MembershipFieldConsent.Create(MembershipFieldConsentId.From(Guid.CreateVersion7()),
                        tenant.TenantId, member.OrganizationId, member.MembershipId, member.Identity.UserId,
                        member.Identity.UserId, definition.Id.Value, definition.SchemaVersion, purpose, locale,
                        MembershipFieldConsentNotice.ComputeHash(notice), 1, MembershipFieldConsentState.Granted,
                        null, 0, clock.UtcNow);
                    await ExecuteAsync(app, transaction, """
                        INSERT INTO public.membership_field_consents
                          (id,tenant_id,organization_id,membership_id,subject_user_id,actor_user_id,definition_id,definition_version,
                           purpose_key,notice_locale,notice_hash,sequence,state,predecessor_id,predecessor_sequence,created_by,row_version)
                        VALUES(@id,@tenant,@organization,@member,@actor,@actor,@definition,@version,@purpose,@locale,@hash,
                           @sequence,'Granted',NULL,@previous,@actor,0)
                        """, ("id", consent.Id.Value), ("tenant", consent.TenantId.Value), ("organization", consent.OrganizationId?.Value),
                        ("member", consent.MembershipId), ("actor", consent.ActorUserId.Value), ("definition", consent.DefinitionId),
                        ("version", consent.DefinitionVersion), ("purpose", consent.PurposeKey), ("locale", consent.NoticeLocale),
                        ("hash", consent.NoticeHash), ("sequence", consent.Sequence), ("previous", consent.PredecessorSequence));
                    ++appended;
                }
                (await ScalarAsync<long>(app, transaction, """
                    SELECT count(*) FROM public.membership_field_consents
                    WHERE tenant_id=@tenant AND organization_id IS NOT DISTINCT FROM @organization
                      AND membership_id=@member AND subject_user_id=@actor AND actor_user_id=@actor AND created_by=@actor
                      AND definition_id=@definition AND definition_version=1 AND purpose_key=@purpose AND notice_locale=@locale
                      AND notice_hash=@hash AND sequence=1 AND state='Granted' AND predecessor_id IS NULL AND predecessor_sequence=0
                    """, ("tenant", tenant.TenantId.Value), ("organization", member.OrganizationId?.Value),
                    ("member", member.MembershipId), ("actor", member.Identity.UserId.Value), ("definition", definition.Id.Value),
                    ("purpose", purpose), ("locale", locale), ("hash", MembershipFieldConsentNotice.ComputeHash(notice))))
                    .Should().Be(1, "each real seeded subject must have one exact retained grant before native discovery");
            }
            await transaction.CommitAsync();
        }
        return appended;
    }
}
