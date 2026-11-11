using Npgsql;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Nonempty app-role subjects for the catalogue's tenant and organization isolation sweeps.</summary>
internal static class CustomFieldFoundationSchemaSeed
{
    internal static async Task SeedAsync(string connectionString)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        foreach (var tenant in new[] { SchemaFixture.TenantA, SchemaFixture.TenantB })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(app, transaction, tenant);
            await SchemaQueries.SetSettingAsync(app, transaction, "app.organization_id", "");
            await SchemaQueries.ExecuteAsync(app, transaction,
                "SELECT pg_advisory_xact_lock(public.fn_identity_contextual_lock_key('tenant', @tenant))", ("tenant", tenant));
            var definition = Guid.CreateVersion7();
            var locale = tenant == SchemaFixture.TenantA ? "tr-TR" : "en-US";
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO public.tenant_custom_field_defs
                    (id,tenant_id,target_entity,key,schema_version,status,json_schema,labels_json,pii_category,
                     is_required,sort,maximum_value_bytes,purpose_key,notices_json,created_at,created_by,row_version)
                VALUES(@definition,@tenant,'Membership','fixture-consent',1,'Draft',
                    '{"type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false}',
                    jsonb_build_object(@locale::text,'Fixture field'),'Sensitive',false,0,64,'fixture-purpose',
                    jsonb_build_object(@locale::text,'Fixture notice'),clock_timestamp(),@actor,0);
                UPDATE public.tenant_custom_field_defs SET status='Active',row_version=1
                    WHERE id=@definition;
                """, ("definition", definition), ("tenant", tenant), ("locale", locale), ("actor", SchemaFixture.Actor));
            (Guid? Organization, Guid Membership, Guid Actor)[] scopes = tenant == SchemaFixture.TenantA
                ? [(null, LearnerMembershipA, PersonA), (SchemaFixture.OrgA1, BranchMembershipA1, PersonA),
                    (SchemaFixture.OrgA2, BranchMembershipA2, PersonA)]
                : [(null, AdminMembershipB, AdminB), (SchemaFixture.OrgB1, BranchMembershipB, PersonB)];
            foreach (var (organization, membership, actor) in scopes)
            {
                await SchemaQueries.SetSettingAsync(app, transaction, "app.organization_id", organization?.ToString("D") ?? "");
                await SchemaQueries.ExecuteAsync(app, transaction, """
                    INSERT INTO public.membership_field_consents
                        (id,tenant_id,organization_id,membership_id,subject_user_id,actor_user_id,definition_id,
                         definition_version,purpose_key,notice_locale,notice_hash,sequence,state,predecessor_sequence,
                         created_by,row_version)
                    VALUES(uuidv7(),@tenant,@organization,@membership,@actor,@actor,@definition,1,'fixture-purpose',
                        @locale,encode(sha256(convert_to('Fixture notice','UTF8')),'hex'),1,'Granted',0,@actor,0)
                    """, ("tenant", tenant), ("organization", (object?)organization ?? DBNull.Value),
                    ("membership", membership), ("actor", actor), ("definition", definition), ("locale", locale));
            }
            await transaction.CommitAsync();
        }
    }
}
