using System.Data.Common;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Raw populated storage subjects, never production job or recurrence definitions.</summary>
internal static class BackgroundJobSchemaSeed
{
    public const string CapacityGroup = "fixture.resource";
    public const string Definition = "fixture.run.v1";
    public const string Traceparent = "00-11111111111111111111111111111111-2222222222222222-01";
    public static readonly DateTimeOffset Slot = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly Scope[] Scopes = EducationSchemaSeed.Scopes.Select((scope, index) => new Scope(
        scope.TenantId, scope.OrganizationId,
        Guid.Parse($"e000000{index + 1}-0000-7000-8000-00000000000{index + 1}"),
        Guid.Parse($"f000000{index + 1}-0000-7000-8000-00000000000{index + 1}"),
        Guid.Parse($"9000000{index + 1}-0000-7000-8000-00000000000{index + 1}"))).ToArray();
    public sealed record Scope(Guid TenantId, Guid? OrganizationId, Guid JobId, Guid AttemptId, Guid RecipientId);
    public static Scope Find(Guid tenant, Guid? organization) =>
        Scopes.Single(scope => scope.TenantId == tenant && scope.OrganizationId == organization);

    public static async Task SeedAsync(string appConnectionString, string dispatcherConnectionString)
    {
        await SeedTenantsAsync(appConnectionString);
        await SeedRecipientsAsync(dispatcherConnectionString);
        await using var app = await PostgresFixture.OpenAsync(appConnectionString);
        foreach (var scope in Scopes)
        {
            await using var transaction = await app.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(app, transaction, scope.TenantId, scope.OrganizationId);
            await InsertJobAsync(app, transaction, scope.JobId, scope.TenantId, scope.OrganizationId,
                recipientId: scope.RecipientId, dueAt: Slot);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                UPDATE background_job_recipients SET accepted_job_id = @job, accepted_at = clock_timestamp()
                WHERE tenant_id = @tenant AND id = @recipient;
                INSERT INTO background_job_capacity (tenant_id, capacity_group)
                VALUES (@tenant, 'fixture.resource') ON CONFLICT DO NOTHING;
                """, ("job", scope.JobId), ("tenant", scope.TenantId), ("recipient", scope.RecipientId));
            await transaction.CommitAsync();
        }
        foreach (var scope in Scopes)
        {
            var token = Guid.NewGuid();
            await ClaimAsync(dispatcherConnectionString, scope.JobId, token);
            await using var transaction = await app.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(app, transaction, scope.TenantId, scope.OrganizationId);
            await AdmitAsync(app, transaction, scope.JobId, scope.AttemptId, token, scope.TenantId, scope.OrganizationId);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO background_job_reservations (tenant_id, capacity_group, job_id, attempt_id)
                VALUES (@tenant, 'fixture.resource', @job, @attempt);
                """, ("tenant", scope.TenantId), ("job", scope.JobId), ("attempt", scope.AttemptId));
            await transaction.CommitAsync();
        }
    }

    public static async Task SeedTenantsAsync(string appConnectionString)
    {
        await using var app = await PostgresFixture.OpenAsync(appConnectionString);
        foreach (var tenant in new[] { SchemaFixture.TenantA, SchemaFixture.TenantB })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(app, transaction, tenant, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
                VALUES (@tenant, @slug, 'Job fixture', 'Trial', now(), @actor, 0) ON CONFLICT (id) DO NOTHING;
                """, ("tenant", tenant), ("slug", tenant == SchemaFixture.TenantA ? "alpha" : "beta"),
                ("actor", SchemaFixture.Actor));
            foreach (var organization in EducationSchemaSeed.Scopes.Where(scope => scope.TenantId == tenant && scope.OrganizationId.HasValue))
                await SchemaQueries.ExecuteAsync(app, transaction, """
                    INSERT INTO organizations (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
                    VALUES (@organization, @tenant, @slug, 'Job fixture', 'Active', now(), @actor, 0)
                    ON CONFLICT (id) DO NOTHING;
                    """, ("organization", organization.OrganizationId!.Value), ("tenant", tenant),
                    ("slug", organization.Slug), ("actor", SchemaFixture.Actor));
            await transaction.CommitAsync();
        }
    }

    public static async Task SeedRecipientsAsync(string dispatcherConnectionString)
    {
        await using var dispatcher = await PostgresFixture.OpenAsync(dispatcherConnectionString);
        foreach (var organization in new[] { false, true })
        {
            var key = organization ? "fixture.organizations" : "fixture.tenants";
            var policy = organization ? "serving_organizations" : "serving_tenants";
            var occurrence = organization
                ? Guid.Parse("70000002-0000-7000-8000-000000000002")
                : Guid.Parse("70000001-0000-7000-8000-000000000001");
            await using var transaction = await dispatcher.BeginTransactionAsync();
            await SchemaQueries.ExecuteAsync(dispatcher, transaction, """
                INSERT INTO background_job_recurrences (recurrence_key, revision, manifest_hash,
                    target_definition_key, schedule, activates_at, anchor_at, period_microseconds,
                    jitter_microseconds, recipient_policy, next_scheduled_at)
                VALUES (@key, 1, repeat('a',64), 'fixture.run.v1', 'interval', @slot, @slot,
                    60000000, 0, @policy, @slot);
                INSERT INTO background_job_occurrences (id, recurrence_key, revision, manifest_hash,
                    target_definition_key, recipient_policy, scheduled_at, jitter_microseconds, skipped_slots)
                VALUES (@occurrence, @key, 1, repeat('a',64), 'fixture.run.v1', @policy, @slot, 0, 0);
                UPDATE background_job_occurrences SET ownership_token = @token,
                    ownership_until = clock_timestamp() + interval '9 minutes' WHERE id = @occurrence;
                UPDATE background_job_occurrences SET bounds_frozen_at = clock_timestamp(),
                    upper_tenant_id = @upper, upper_organization_id = @upper_org WHERE id = @occurrence;
                """, ("key", key), ("policy", policy), ("slot", Slot), ("occurrence", occurrence),
                ("token", Guid.NewGuid()), ("upper", SchemaFixture.TenantB),
                ("upper_org", organization ? SchemaFixture.OrgB1 : DBNull.Value));
            foreach (var scope in Scopes.Where(scope => scope.OrganizationId.HasValue == organization))
                await SchemaQueries.ExecuteAsync(dispatcher, transaction, """
                    INSERT INTO background_job_recipients (id, occurrence_id, tenant_id, organization_id,
                        recurrence_key, revision, manifest_hash, target_definition_key, recipient_policy, scheduled_at, due_at)
                    VALUES (@recipient, @occurrence, @tenant, @organization, @key, 1, repeat('a',64),
                        'fixture.run.v1', @policy, @slot, @slot);
                    UPDATE background_job_recipients SET ownership_token = @token,
                        ownership_until = clock_timestamp() + interval '9 minutes' WHERE id = @recipient;
                    """, ("recipient", scope.RecipientId), ("occurrence", occurrence), ("tenant", scope.TenantId),
                    ("organization", (object?)scope.OrganizationId ?? DBNull.Value), ("key", key), ("policy", policy),
                    ("slot", Slot), ("token", Guid.NewGuid()));
            await transaction.CommitAsync();
        }
    }

    public static Task InsertJobAsync(DbConnection connection, DbTransaction transaction, Guid jobId, Guid tenantId,
        Guid? organizationId, string profile = "transactional", Guid? recipientId = null, DateTimeOffset? dueAt = null,
        Guid? parentId = null, string parameters = "{}", string? capacityGroup = CapacityGroup) => SchemaQueries.ExecuteAsync(connection, transaction, """
            INSERT INTO background_jobs (id, tenant_id, organization_id, definition_key, profile, queue,
                capacity_group, parameters, original_due_at, traceparent, recipient_id, parent_job_id)
            VALUES (@id, @tenant, @organization, 'fixture.run.v1', @profile, 'fixture', @capacity,
                @parameters, @due, @trace, @recipient, @parent)
            """, ("id", jobId), ("tenant", tenantId), ("organization", (object?)organizationId ?? DBNull.Value),
            ("profile", profile), ("capacity", (object?)capacityGroup ?? DBNull.Value), ("parameters", parameters), ("due", dueAt ?? Slot), ("trace", Traceparent),
            ("recipient", (object?)recipientId ?? DBNull.Value), ("parent", (object?)parentId ?? DBNull.Value));

    public static async Task ClaimAsync(string dispatcherConnectionString, Guid jobId, Guid token)
    {
        await using var dispatcher = await PostgresFixture.OpenAsync(dispatcherConnectionString);
        await SchemaQueries.ExecuteAsync(dispatcher, null, """
            UPDATE background_jobs SET ownership_token = @token, ownership_kind = 'routing',
                ownership_until = clock_timestamp() + interval '4 minutes' WHERE id = @job
            """, ("token", token), ("job", jobId));
    }

    public static async Task AdmitAsync(DbConnection connection, DbTransaction transaction, Guid jobId, Guid attemptId,
        Guid token, Guid tenant, Guid? organization, string profile = "transactional")
    {
        var deadline = Microseconds(DateTimeOffset.UtcNow.AddMinutes(4));
        await SchemaQueries.ExecuteAsync(connection, transaction, """
            INSERT INTO background_job_attempts (id, tenant_id, organization_id, job_id, stage, ordinal,
                admission_token, deadline) VALUES (@attempt, @tenant, @organization, @job, 'normal', 1, @token, @deadline);
            UPDATE background_jobs SET normal_attempts = 1, current_attempt_id = @attempt, attempt_deadline = @deadline,
                phase = @phase WHERE id = @job AND tenant_id = @tenant;
            """, ("attempt", attemptId), ("tenant", tenant), ("organization", (object?)organization ?? DBNull.Value),
            ("job", jobId), ("token", token), ("deadline", deadline), ("phase", profile == "external" ? "preparing" : "executing"));
    }

    public static DateTimeOffset Microseconds(DateTimeOffset instant) =>
        new(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);
}
