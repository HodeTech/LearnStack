using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Applies <b>every</b> migration chain and seeds every table they create, for
/// two tenants.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every chain, one fixture, and that is the point.</b> The structural sweeps
/// — row security, the permissive-policy rule, snake_case, the grant matrix,
/// foreign-key indexing — enumerate the catalogue rather than a list of names, so
/// a fixture carrying only the tenancy chain silently narrows every one of them
/// to eight of the ten tables. That is the inclusion-list failure with a
/// different shape: measured, a second permissive SELECT policy on
/// <c>outbox_messages</c> passed the entire suite while letting any session with
/// any tenant context read every tenant's pending events.
/// </para>
/// <para>
/// It happened again, in the same shape, when the Customization chain shipped:
/// this fixture applied two of three, so the four new tables were outside every
/// sweep above and each one stayed green by reading a schema that did not contain
/// them. <b>A chain that deploys is a chain this fixture applies</b> — the count
/// below is the assertion that says so.
/// </para>
/// <para>
/// <b>Every table carries rows for both tenants.</b> A count assertion against an
/// empty table passes whether or not the policy that should have emptied it
/// exists. Tenant A carries a second organization as well, so the organization
/// half of the template has a sibling row to hide.
/// </para>
/// <para>
/// Most of the seed runs as <c>learnstack_migration</c> inside a transaction that
/// sets <c>app.tenant_id</c> — the only way to insert, since every table's
/// <c>WITH CHECK</c> is live from the moment the migration finishes.
/// <c>platform_host_to_tenant</c> is the exception: its policies are qualified
/// <c>TO learnstack_app</c>, so the owner is denied on it and its rows go in
/// through the application role.
/// </para>
/// </remarks>
public sealed class SchemaFixture : IAsyncLifetime
{
    public static readonly Guid TenantA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    public static readonly Guid TenantB = Guid.Parse("22222222-2222-7222-8222-222222222222");
    public static readonly Guid OrgA1 = Guid.Parse("aaaaaaaa-1111-7111-8111-111111111111");
    public static readonly Guid OrgA2 = Guid.Parse("aaaaaaaa-2222-7222-8222-222222222222");
    public static readonly Guid OrgB1 = Guid.Parse("bbbbbbbb-1111-7111-8111-111111111111");
    public static readonly Guid Actor = Guid.Parse("00000000-0000-7000-8000-000000000001");

    public const string HostA = "alpha.example.com";
    public const string HostB = "beta.example.com";

    /// <summary>
    /// The sixteen tables the four chains create, used only to prove that a
    /// catalogue sweep read something.
    /// </summary>
    /// <remarks>
    /// Not an inclusion list: no query filters on it. It exists so a sweep that
    /// silently matched nothing fails instead of passing, which is the other way
    /// a structural assertion can prove nothing.
    /// </remarks>
    public static readonly string[] KnownTables =
    [
        "tenants", "organizations", "tenant_domains", "tenant_locales",
        "tenant_settings", "tenant_feature_flags",
        "platform_entitlement_cache", "platform_host_to_tenant",
        "outbox_messages", "idempotency_keys",
        "tenant_content_types", "tenant_level_taxonomies", "tenant_level_taxonomy_items",
        "customization_generations",
        "audit_log", "audit_config",
        "platform_killswitches",
    ];

    /// <summary>What tenant A sees with its tenant context set and no organization scope.</summary>
    public static readonly Dictionary<string, long> RowsVisibleToTenantA = new(StringComparer.Ordinal)
    {
        ["tenants"] = 1,
        ["organizations"] = 2,
        ["tenant_domains"] = 1,
        ["tenant_locales"] = 1,
        // Three rows exist; two are organization-scoped and invisible without
        // app.organization_id. Org_X_cannot_read_Org_Y_within_TenantA reads those.
        ["tenant_settings"] = 1,
        ["tenant_feature_flags"] = 1,
        ["platform_entitlement_cache"] = 1,
        ["platform_host_to_tenant"] = 1,
        ["outbox_messages"] = 1,
        ["idempotency_keys"] = 1,
        ["tenant_content_types"] = 1,
        ["tenant_level_taxonomies"] = 1,
        ["tenant_level_taxonomy_items"] = 1,
        ["customization_generations"] = 1,
        // Two rows exist. One is organization-scoped and invisible without
        // app.organization_id, exactly as tenant_settings' pair is — audit_log is the
        // second org-scoped table in the schema and the only one whose rows a tenant
        // never writes directly.
        ["audit_log"] = 1,
        ["audit_config"] = 1,
        // Platform-scoped and deliberately tenant-blind: one switch, visible to both
        // tenants and to a request with no tenant at all. The only row in the schema for
        // which "no tenant context ⇒ zero rows" is false on purpose.
        ["platform_killswitches"] = 1,
    };

    /// <summary>What tenant B sees with its tenant context set.</summary>
    public static readonly Dictionary<string, long> RowsVisibleToTenantB = new(StringComparer.Ordinal)
    {
        ["tenants"] = 1,
        ["organizations"] = 1,
        ["tenant_domains"] = 1,
        ["tenant_locales"] = 1,
        ["tenant_settings"] = 1,
        ["tenant_feature_flags"] = 1,
        ["platform_entitlement_cache"] = 1,
        ["platform_host_to_tenant"] = 1,
        ["outbox_messages"] = 1,
        ["idempotency_keys"] = 1,
        ["tenant_content_types"] = 1,
        ["tenant_level_taxonomies"] = 1,
        ["tenant_level_taxonomy_items"] = 1,
        ["customization_generations"] = 1,
        ["audit_log"] = 1,
        // Two: the second is tenant B's alone, and AuditConfigServiceTests asks tenant A
        // about that slug to prove a scoped read from a leaking one.
        ["audit_config"] = 2,
        // Platform-scoped and deliberately tenant-blind: one switch, visible to both
        // tenants and to a request with no tenant at all. The only row in the schema for
        // which "no tenant context implies zero rows" is false on purpose.
        ["platform_killswitches"] = 1,
    };

    public PostgresFixture Postgres { get; } = new();

    public async Task InitializeAsync()
    {
        await Postgres.InitializeAsync();

        await MigrationChains.ApplyAllAsync(Postgres.MigrationConnectionString);

        await SeedAsync();
    }

    public async Task DisposeAsync() => await Postgres.DisposeAsync();

    private async Task SeedAsync()
    {
        await using (var owner = await PostgresFixture.OpenAsync(Postgres.MigrationConnectionString))
        {
            await using var command = new NpgsqlCommand(TenantRowsSql, (NpgsqlConnection)owner);
            await command.ExecuteNonQueryAsync();
        }

        // platform_host_to_tenant only: its four policies are role-qualified TO
        // learnstack_app, so under FORCE the owner is denied on it.
        await using (var app = await PostgresFixture.OpenAsync(Postgres.AppConnectionString))
        {
            await using var mappings = new NpgsqlCommand(HostMappingsSql, (NpgsqlConnection)app);
            await mappings.ExecuteNonQueryAsync();
        }

        // platform_killswitches as learnstack_platform, and it cannot be either of the
        // two roles above: learnstack_app holds SELECT and nothing else, and the owner is
        // denied for the same reason it is on platform_host_to_tenant — the only policy
        // names learnstack_app, so under FORCE none applies to learnstack_migration.
        // That is also the role every real toggle will use, through
        // EnterPlatformAdminScope.
        await using var platform = await PostgresFixture.OpenAsync(Postgres.PlatformConnectionString);
        await using var killswitches = new NpgsqlCommand(KillswitchesSql, (NpgsqlConnection)platform);
        await killswitches.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// One switch, enabled — the state a killswitch is in when nobody has flipped it.
    /// </summary>
    /// <remarks>
    /// Seeded rather than left empty because the sweep's positive half needs a row to
    /// find: <c>USING (true)</c> exists to guarantee that <c>learnstack_app</c> reads ALL
    /// of this table with no tenant context, and a sweep against an empty table passes
    /// whether or not the policy does anything.
    /// </remarks>
    private const string KillswitchesSql =
        """
        INSERT INTO platform_killswitches (key, is_enabled, reason, toggled_at, toggled_by)
        VALUES ('killswitch.classroom.recording', true, NULL, now(), NULL);
        """;

    private const string TenantRowsSql =
        """
        BEGIN;
        SET LOCAL app.tenant_id = '11111111-1111-7111-8111-111111111111';

        INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
        VALUES ('11111111-1111-7111-8111-111111111111','alpha','Alpha','Trial', now(),
                '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO organizations (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
        VALUES ('aaaaaaaa-1111-7111-8111-111111111111','11111111-1111-7111-8111-111111111111',
                'main','Main','Active', now(), '00000000-0000-7000-8000-000000000001', 0),
               ('aaaaaaaa-2222-7222-8222-222222222222','11111111-1111-7111-8111-111111111111',
                'branch','Branch','Active', now(), '00000000-0000-7000-8000-000000000001', 0);

        UPDATE tenants SET default_organization_id = 'aaaaaaaa-1111-7111-8111-111111111111'
        WHERE id = '11111111-1111-7111-8111-111111111111';

        INSERT INTO tenant_domains
            (id, tenant_id, host, kind, status, verification_attempts, created_at, created_by, row_version)
        VALUES (uuidv7(),'11111111-1111-7111-8111-111111111111','alpha.example.com','Subdomain','Verified',
                0, now(), '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO tenant_locales (tenant_id, locale, is_default, is_enabled, sort)
        VALUES ('11111111-1111-7111-8111-111111111111','tr-TR', true, true, 0);

        INSERT INTO tenant_settings (id, tenant_id, organization_id, key, value, created_at, created_by, row_version)
        VALUES (uuidv7(),'11111111-1111-7111-8111-111111111111', NULL,
                'tz', '"Europe/Istanbul"', now(), '00000000-0000-7000-8000-000000000001', 0);

        -- One organization at a time, because the org-scoped WITH CHECK admits a
        -- row only under its own organization's context. Writing both in one
        -- statement is exactly what the guard refuses, and the seed is the first
        -- place that shows it.
        SET LOCAL app.organization_id = 'aaaaaaaa-1111-7111-8111-111111111111';
        INSERT INTO tenant_settings (id, tenant_id, organization_id, key, value, created_at, created_by, row_version)
        VALUES (uuidv7(),'11111111-1111-7111-8111-111111111111','aaaaaaaa-1111-7111-8111-111111111111',
                'theme', '"main"', now(), '00000000-0000-7000-8000-000000000001', 0);

        SET LOCAL app.organization_id = 'aaaaaaaa-2222-7222-8222-222222222222';
        INSERT INTO tenant_settings (id, tenant_id, organization_id, key, value, created_at, created_by, row_version)
        VALUES (uuidv7(),'11111111-1111-7111-8111-111111111111','aaaaaaaa-2222-7222-8222-222222222222',
                'theme', '"branch"', now(), '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO tenant_feature_flags (tenant_id, key, value, updated_by)
        VALUES ('11111111-1111-7111-8111-111111111111','live-classroom','true',
                '00000000-0000-7000-8000-000000000001');

        INSERT INTO platform_entitlement_cache
            (tenant_id, plan_code, features, limits, compliance, valid_until, source)
        VALUES ('11111111-1111-7111-8111-111111111111','pro','{}','{}','{}',
                now() + interval '30 days','null-provider');

        INSERT INTO outbox_messages
            (tenant_id, correlation_id, type, topic, partition_key, payload)
        VALUES ('11111111-1111-7111-8111-111111111111','00-alpha-span-01','TenantCreated',
                'learnstack.tenancy.tenant','11111111-1111-7111-8111-111111111111','{}');

        INSERT INTO idempotency_keys (tenant_id, key, fingerprint, claim_token, state, expires_at)
        VALUES ('11111111-1111-7111-8111-111111111111','alpha-seed-key','fp-alpha', uuidv7(),
                'in_flight', now() + interval '5 minutes');

        -- The Customization chain. Every table carries a row for both tenants for
        -- the reason the tenancy tables do: a count assertion against an empty
        -- table passes whether or not the policy that should have emptied it
        -- exists.
        INSERT INTO tenant_content_types
            (id, tenant_id, key, schema_version, schema_revision, status, display_name,
             json_schema, renderer_key, created_at, created_by, row_version)
        VALUES (uuidv7(),'11111111-1111-7111-8111-111111111111','announcement', 1, 0, 'Active', '{"en":"Announcement"}',
                '{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object"}',
                'default-card', now(), '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO tenant_level_taxonomies
            (id, tenant_id, key, schema_version, schema_revision, status, display_name,
             created_at, created_by, row_version)
        VALUES (uuidv7(),'11111111-1111-7111-8111-111111111111','proficiency', 1, 0, 'Active', '{"en":"Proficiency"}',
                now(), '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO tenant_level_taxonomy_items
            (tenant_id, taxonomy_key, schema_version, key, display_name, sort)
        VALUES ('11111111-1111-7111-8111-111111111111','proficiency', 1, 'beginner', '{"en":"Beginner"}', 0);

        INSERT INTO customization_generations (tenant_id, generation)
        VALUES ('11111111-1111-7111-8111-111111111111', 1);

        -- The Audit chain. audit_log is the schema's second organization-scoped table,
        -- so tenant A gets the same pair tenant_settings has: one tenant-wide row and
        -- one under an organization, which is what makes the org half of the predicate
        -- a number rather than an assumption. Neither is written by any command — the
        -- runtime writer is PostgresAuditStore — so the seed is the owner's, and it
        -- announces app.organization_id for the org-scoped row exactly as that writer
        -- does. audit_config carries no organization_id and takes one row.
        SET LOCAL app.organization_id = '';
        INSERT INTO audit_log
            (id, tenant_id, organization_id, module, operation, operation_type,
             operation_class, outcome, timestamp)
        VALUES (uuidv7(),'11111111-1111-7111-8111-111111111111', NULL,
                'tenancy','tenancy.tenant.provision','Create','Must','success', now());

        SET LOCAL app.organization_id = 'aaaaaaaa-1111-7111-8111-111111111111';
        INSERT INTO audit_log
            (id, tenant_id, organization_id, module, operation, operation_type,
             operation_class, outcome, timestamp)
        VALUES (uuidv7(),'11111111-1111-7111-8111-111111111111',
                'aaaaaaaa-1111-7111-8111-111111111111',
                'tenancy','tenancy.organization.create','Create','Must','success', now());

        INSERT INTO audit_config
            (id, tenant_id, module, operation, is_enabled, created_at, created_by, row_version)
        VALUES (uuidv7(),'11111111-1111-7111-8111-111111111111',
                'tenancy','tenancy.organization.create', false, now(),
                '00000000-0000-7000-8000-000000000001', 0);
        COMMIT;

        BEGIN;
        SET LOCAL app.tenant_id = '22222222-2222-7222-8222-222222222222';

        INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
        VALUES ('22222222-2222-7222-8222-222222222222','beta','Beta','Trial', now(),
                '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO organizations (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
        VALUES ('bbbbbbbb-1111-7111-8111-111111111111','22222222-2222-7222-8222-222222222222',
                'main','Main','Active', now(), '00000000-0000-7000-8000-000000000001', 0);

        UPDATE tenants SET default_organization_id = 'bbbbbbbb-1111-7111-8111-111111111111'
        WHERE id = '22222222-2222-7222-8222-222222222222';

        INSERT INTO tenant_domains
            (id, tenant_id, host, kind, status, verification_attempts, created_at, created_by, row_version)
        VALUES (uuidv7(),'22222222-2222-7222-8222-222222222222','beta.example.com','Subdomain','Verified',
                0, now(), '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO tenant_locales (tenant_id, locale, is_default, is_enabled, sort)
        VALUES ('22222222-2222-7222-8222-222222222222','en-US', true, true, 0);

        INSERT INTO tenant_settings (id, tenant_id, organization_id, key, value, created_at, created_by, row_version)
        VALUES (uuidv7(),'22222222-2222-7222-8222-222222222222', NULL,
                'beta-only', '"visible to beta alone"', now(),
                '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO tenant_feature_flags (tenant_id, key, value, updated_by)
        VALUES ('22222222-2222-7222-8222-222222222222','live-classroom','false',
                '00000000-0000-7000-8000-000000000001');

        INSERT INTO platform_entitlement_cache
            (tenant_id, plan_code, features, limits, compliance, valid_until, source)
        VALUES ('22222222-2222-7222-8222-222222222222','free','{}','{}','{}',
                now() + interval '30 days','null-provider');

        INSERT INTO outbox_messages
            (tenant_id, correlation_id, type, topic, partition_key, payload)
        VALUES ('22222222-2222-7222-8222-222222222222','00-beta-span-01','TenantCreated',
                'learnstack.tenancy.tenant','22222222-2222-7222-8222-222222222222','{}');

        INSERT INTO idempotency_keys (tenant_id, key, fingerprint, claim_token, state, expires_at)
        VALUES ('22222222-2222-7222-8222-222222222222','beta-seed-key','fp-beta', uuidv7(),
                'in_flight', now() + interval '5 minutes');

        -- The Customization chain. Every table carries a row for both tenants for
        -- the reason the tenancy tables do: a count assertion against an empty
        -- table passes whether or not the policy that should have emptied it
        -- exists.
        INSERT INTO tenant_content_types
            (id, tenant_id, key, schema_version, schema_revision, status, display_name,
             json_schema, renderer_key, created_at, created_by, row_version)
        VALUES (uuidv7(),'22222222-2222-7222-8222-222222222222','announcement', 1, 0, 'Active', '{"en":"Announcement"}',
                '{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object"}',
                'default-card', now(), '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO tenant_level_taxonomies
            (id, tenant_id, key, schema_version, schema_revision, status, display_name,
             created_at, created_by, row_version)
        VALUES (uuidv7(),'22222222-2222-7222-8222-222222222222','proficiency', 1, 0, 'Active', '{"en":"Proficiency"}',
                now(), '00000000-0000-7000-8000-000000000001', 0);

        INSERT INTO tenant_level_taxonomy_items
            (tenant_id, taxonomy_key, schema_version, key, display_name, sort)
        VALUES ('22222222-2222-7222-8222-222222222222','proficiency', 1, 'starter', '{"en":"Beginner"}', 0);

        INSERT INTO customization_generations (tenant_id, generation)
        VALUES ('22222222-2222-7222-8222-222222222222', 2);

        -- The Audit chain, tenant-wide on both tables. A count assertion against an
        -- empty table passes whether or not the policy that should have emptied it
        -- exists, so tenant B holds rows here too.
        INSERT INTO audit_log
            (id, tenant_id, organization_id, module, operation, operation_type,
             operation_class, outcome, timestamp)
        VALUES (uuidv7(),'22222222-2222-7222-8222-222222222222', NULL,
                'tenancy','tenancy.tenant.provision','Create','Must','success', now());

        -- Two override rows, and the second one is what makes cross-tenant isolation
        -- falsifiable here. Both tenants carrying the SAME slug was the earlier seed,
        -- and under it a loader that announced the wrong tenant — or a cache key that
        -- omitted it — produced the identical answer for both, so the isolation case
        -- could not fail. tenancy.hostmapping.write is tenant B's alone: asking tenant A
        -- about it separates a correctly scoped read from a leaking one.
        INSERT INTO audit_config
            (id, tenant_id, module, operation, is_enabled, created_at, created_by, row_version)
        VALUES (uuidv7(),'22222222-2222-7222-8222-222222222222',
                'tenancy','tenancy.organization.create', false, now(),
                '00000000-0000-7000-8000-000000000001', 0),
               (uuidv7(),'22222222-2222-7222-8222-222222222222',
                'tenancy','tenancy.hostmapping.write', false, now(),
                '00000000-0000-7000-8000-000000000001', 0);
        COMMIT;
        """;

    private const string HostMappingsSql =
        """
        BEGIN;
        SET LOCAL app.tenant_id = '11111111-1111-7111-8111-111111111111';
        INSERT INTO platform_host_to_tenant (host, tenant_id, organization_id, is_active, is_publicly_live)
        VALUES ('alpha.example.com','11111111-1111-7111-8111-111111111111',
                'aaaaaaaa-1111-7111-8111-111111111111', true, true);
        COMMIT;

        BEGIN;
        SET LOCAL app.tenant_id = '22222222-2222-7222-8222-222222222222';
        INSERT INTO platform_host_to_tenant (host, tenant_id, organization_id, is_active, is_publicly_live)
        VALUES ('beta.example.com','22222222-2222-7222-8222-222222222222',
                'bbbbbbbb-1111-7111-8111-111111111111', true, true);
        COMMIT;
        """;
}

/// <summary>
/// The collection that shares one <see cref="SchemaFixture"/> — and therefore one
/// container and one applied schema — between the tenancy and platform cases.
/// </summary>
/// <remarks>
/// A shared collection rather than two <c>IClassFixture</c>s, because the point
/// of merging them is that the structural sweeps must see <b>every</b> table.
/// Two class fixtures would be two containers with two half-schemas, which is the
/// arrangement that let a second permissive policy on <c>outbox_messages</c> pass
/// the whole suite.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SharedSchema : ICollectionFixture<SchemaFixture>
{
    public const string Name = "schema";
}
