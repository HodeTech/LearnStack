using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

internal sealed record BackgroundJobOccurrencePage(Guid OccurrenceId, int ExaminedRoutes, int InsertedRecipients, bool Completed);
internal sealed record BackgroundJobLifecycleRoute(Guid Tenant, Guid? Organization, string Status, bool Deleted,
    Guid? DefaultOrganization = null);

internal sealed partial class PostgresBackgroundJobControlStore
{
    private const string TenantLifecyclePageSql = """
        SELECT id,status,deleted_at,default_organization_id FROM tenants
        WHERE id>@afterTenant AND id<=@upperTenant ORDER BY id LIMIT @limit
        """;
    private const string OrganizationLifecyclePageSql = """
        SELECT tenant_id,id,status,deleted_at FROM organizations
        WHERE ROW(tenant_id,id)>ROW(@afterTenant,@afterOrganization) AND ROW(tenant_id,id)<=ROW(@upperTenant,@upperOrganization)
        ORDER BY tenant_id,id LIMIT @limit
        """;

    /// <summary>One bounded raw-key page; recipients and the last examined key commit together.</summary>
    internal async Task<OwnedBackgroundJobControl<BackgroundJobOccurrencePage>> MaterializeRecipientPageAsync(
        BackgroundJobRecurrenceRegistry recurrences, BackgroundJobLifecycleProjection projection,
        CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(recurrences);
        ArgumentNullException.ThrowIfNull(projection);
        var resources = new BackgroundJobControlResources();
        BackgroundJobOccurrencePage? value = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var command = Command(resources, """
                    SELECT r.recurrence_key,r.revision,r.manifest_hash,r.target_definition_key,r.schedule,r.activates_at,
                        r.anchor_at,r.period_microseconds,r.jitter_microseconds,r.recipient_policy,
                        o.id,o.recurrence_key,o.revision,o.manifest_hash,o.target_definition_key,o.recipient_policy,
                        o.scheduled_at,o.jitter_microseconds,o.bounds_frozen_at,o.upper_tenant_id,o.upper_organization_id,
                        o.after_tenant_id,o.after_organization_id
                    FROM background_job_occurrences o JOIN background_job_recurrences r
                        ON r.recurrence_key=o.recurrence_key AND r.revision=o.revision
                    WHERE o.completed_at IS NULL AND o.quarantined_at IS NULL AND o.available_after<=statement_timestamp()
                        AND (o.ownership_token IS NULL OR o.ownership_until<=statement_timestamp())
                    ORDER BY o.available_after,o.scheduled_at,o.id LIMIT 1 FOR UPDATE OF o SKIP LOCKED
                    """);
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                OccurrenceCursor? cursor = null;
                if (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false)) cursor = OccurrenceCursor.Read(resources.Reader);
                await FinishAsync(resources).ConfigureAwait(false);
                if (cursor is not null)
                {
                    var canRoute = recurrences.TryGet(cursor.Key, out var local) && local.Registration.Revision >= cursor.Revision
                        && (local.Registration.Revision != cursor.Revision || local.ManifestHash == cursor.Hash)
                        && definitions.TryGet(cursor.Target, out var target)
                        && target.Readiness == BackgroundJobDefinitionReadiness.Active
                        && target.ParameterType == typeof(BackgroundJobOccurrenceParameters)
                        && target.Scope == (cursor.Organizations ? BackgroundJobScope.Organization : BackgroundJobScope.TenantWide)
                        && target.OwnerModule == cursor.Key.Split('.')[0];
                    if (cursor.Invalid || !canRoute)
                    {
                        command = Command(resources, """
                            UPDATE background_job_occurrences SET available_after=clock_timestamp()+@backoff,
                                ownership_token=NULL,ownership_until=NULL,last_error=@error,
                                quarantined_at=CASE WHEN @invalid THEN clock_timestamp() ELSE quarantined_at END WHERE id=@id
                            """);
                        command.Parameters.AddWithValue("id", cursor.Id);
                        command.Parameters.AddWithValue("backoff", options.HandoffRepairGrace);
                        command.Parameters.AddWithValue("error", cursor.Invalid ? "invalid_control" : "deployment_mismatch");
                        command.Parameters.AddWithValue("invalid", cursor.Invalid);
                        await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
                        await FinishAsync(resources).ConfigureAwait(false);
                    }
                    else
                    {
                        cursor = cursor with { Token = ids.NewUuidV4() };
                        command = Command(resources, """
                            UPDATE background_job_occurrences SET ownership_token=@token,ownership_until=clock_timestamp()+@lease
                            WHERE id=@id
                            """);
                        command.Parameters.AddWithValue("id", cursor.Id);
                        command.Parameters.AddWithValue("token", cursor.Token);
                        command.Parameters.AddWithValue("lease", options.LeaseDuration);
                        await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
                        await FinishAsync(resources).ConfigureAwait(false);
                        if (!cursor.BoundsFrozen) cursor = await FreezeOccurrenceBoundsAsync(resources, cursor, deadline.Token).ConfigureAwait(false);
                        var routes = await ReadLifecyclePageAsync(resources, cursor, deadline.Token).ConfigureAwait(false);
                        var eligible = await EligibleLifecycleRoutesAsync(resources, cursor, routes, projection, deadline.Token).ConfigureAwait(false);
                        var inserted = await InsertOccurrenceRecipientsAsync(resources, cursor, eligible, deadline.Token).ConfigureAwait(false);
                        var last = routes.Count == 0 ? null : routes[^1];
                        var afterTenant = last?.Tenant ?? cursor.AfterTenant;
                        var afterOrganization = last?.Organization ?? cursor.AfterOrganization;
                        var completed = routes.Count < options.FanoutPageSize
                            || afterTenant == cursor.UpperTenant && afterOrganization == cursor.UpperOrganization;
                        command = Command(resources, """
                            UPDATE background_job_occurrences SET after_tenant_id=@after,after_organization_id=@organization,
                                completed_at=CASE WHEN @completed THEN clock_timestamp() ELSE NULL END,
                                ownership_token=NULL,ownership_until=NULL,available_after=clock_timestamp(),last_error=NULL
                            WHERE id=@id AND ownership_token=@token AND ownership_until>clock_timestamp()
                            """);
                        command.Parameters.AddWithValue("id", cursor.Id);
                        command.Parameters.AddWithValue("token", cursor.Token);
                        command.Parameters.AddWithValue("after", NpgsqlDbType.Uuid, (object?)afterTenant ?? DBNull.Value);
                        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)afterOrganization ?? DBNull.Value);
                        command.Parameters.AddWithValue("completed", completed);
                        if (await command.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false) != 1)
                            throw new InvalidOperationException("Occurrence page ownership expired before its atomic checkpoint.");
                        await FinishAsync(resources).ConfigureAwait(false);
                        value = new(cursor.Id, routes.Count, inserted, completed);
                    }
                    await CommitAsync(resources, deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        return new(value, resources, []);
    }

    private async Task<OccurrenceCursor> FreezeOccurrenceBoundsAsync(BackgroundJobControlResources resources,
        OccurrenceCursor cursor, CancellationToken token)
    {
        // Freeze raw keys, including deleted/ineligible routes. Filtering before
        // LIMIT could scan an unbounded ineligible backlog and skip live cohorts.
        var command = Command(resources, cursor.Organizations
            ? "SELECT tenant_id,id FROM organizations ORDER BY tenant_id DESC,id DESC LIMIT 1"
            : "SELECT id FROM tenants ORDER BY id DESC LIMIT 1");
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        Guid? upperTenant = null;
        Guid? upperOrganization = null;
        if (await resources.Reader!.ReadAsync(token).ConfigureAwait(false))
        {
            upperTenant = resources.Reader.GetGuid(0);
            if (cursor.Organizations) upperOrganization = resources.Reader.GetGuid(1);
        }
        await FinishAsync(resources).ConfigureAwait(false);
        command = Command(resources, """
            UPDATE background_job_occurrences SET bounds_frozen_at=clock_timestamp(),upper_tenant_id=@tenant,upper_organization_id=@organization
            WHERE id=@id AND ownership_token=@token AND ownership_until>clock_timestamp()
            """);
        command.Parameters.AddWithValue("id", cursor.Id);
        command.Parameters.AddWithValue("token", cursor.Token);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, (object?)upperTenant ?? DBNull.Value);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)upperOrganization ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Occurrence ownership expired before cohort bounds were frozen.");
        await FinishAsync(resources).ConfigureAwait(false);
        return cursor with { BoundsFrozen = true, UpperTenant = upperTenant, UpperOrganization = upperOrganization };
    }

    private async Task<IReadOnlyList<BackgroundJobLifecycleRoute>> ReadLifecyclePageAsync(BackgroundJobControlResources resources,
        OccurrenceCursor cursor, CancellationToken token)
    {
        if (cursor.UpperTenant is null) return [];
        var command = Command(resources, cursor.Organizations ? OrganizationLifecyclePageSql : TenantLifecyclePageSql);
        command.Parameters.AddWithValue("afterTenant", cursor.AfterTenant ?? Guid.Empty);
        command.Parameters.AddWithValue("upperTenant", cursor.UpperTenant.Value);
        command.Parameters.AddWithValue("limit", options.FanoutPageSize);
        if (cursor.Organizations)
        {
            command.Parameters.AddWithValue("afterOrganization", cursor.AfterOrganization ?? Guid.Empty);
            command.Parameters.AddWithValue("upperOrganization", cursor.UpperOrganization!.Value);
        }
        var routes = new List<BackgroundJobLifecycleRoute>();
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        while (await resources.Reader!.ReadAsync(token).ConfigureAwait(false))
        {
            var row = resources.Reader;
            routes.Add(cursor.Organizations ? new(row.GetGuid(0), row.GetGuid(1), row.GetString(2), !row.IsDBNull(3))
                : new(row.GetGuid(0), null, row.GetString(1), !row.IsDBNull(2), row.IsDBNull(3) ? null : row.GetGuid(3)));
        }
        await FinishAsync(resources).ConfigureAwait(false);
        return routes.AsReadOnly();
    }

    private async Task<IReadOnlyList<BackgroundJobLifecycleRoute>> EligibleLifecycleRoutesAsync(BackgroundJobControlResources resources,
        OccurrenceCursor cursor, IReadOnlyList<BackgroundJobLifecycleRoute> routes, BackgroundJobLifecycleProjection projection,
        CancellationToken token)
    {
        if (!cursor.Organizations)
            return routes.Where(route => projection.AllowsTenant(route.Status, route.Deleted, route.DefaultOrganization, cursor.Policy)).ToArray();
        if (routes.Count == 0) return [];
        var command = Command(resources, "SELECT id,status,deleted_at,default_organization_id FROM tenants WHERE id=ANY(@tenants)");
        command.Parameters.AddWithValue("tenants", routes.Select(route => route.Tenant).Distinct().ToArray());
        var serving = new HashSet<Guid>();
        resources.SetReader(await command.ExecuteReaderAsync(token).ConfigureAwait(false));
        while (await resources.Reader!.ReadAsync(token).ConfigureAwait(false))
        {
            var row = resources.Reader;
            if (projection.AllowsTenant(row.GetString(1), !row.IsDBNull(2), row.IsDBNull(3) ? null : row.GetGuid(3), cursor.Policy))
                serving.Add(row.GetGuid(0));
        }
        await FinishAsync(resources).ConfigureAwait(false);
        return routes.Where(route => serving.Contains(route.Tenant) && projection.AllowsOrganization(route.Status, route.Deleted)).ToArray();
    }

    private async Task<int> InsertOccurrenceRecipientsAsync(BackgroundJobControlResources resources,
        OccurrenceCursor cursor, IReadOnlyList<BackgroundJobLifecycleRoute> routes, CancellationToken token)
    {
        if (routes.Count == 0) return 0;
        var command = Command(resources, """
            INSERT INTO background_job_recipients (id,occurrence_id,tenant_id,organization_id,recurrence_key,revision,
                manifest_hash,target_definition_key,recipient_policy,scheduled_at,due_at)
            SELECT x.id,@occurrence,x.tenant,x.organization,@key,@revision,@hash,@target,@policy,@slot,x.due
            FROM unnest(@ids,@tenants,@organizations,@dues) AS x(id,tenant,organization,due)
            ON CONFLICT (occurrence_id,tenant_id,organization_id) DO NOTHING
            """);
        command.Parameters.AddWithValue("ids", routes.Select(_ => ids.NewUuidV7()).ToArray());
        command.Parameters.AddWithValue("tenants", routes.Select(route => route.Tenant).ToArray());
        command.Parameters.AddWithValue("organizations", NpgsqlDbType.Array | NpgsqlDbType.Uuid, routes.Select(route => route.Organization).ToArray());
        command.Parameters.AddWithValue("dues", routes.Select(route => BackgroundJobRecipientJitter.DueAt(cursor.Id, route.Tenant,
            route.Organization, cursor.Slot, cursor.JitterMicroseconds)).ToArray());
        command.Parameters.AddWithValue("occurrence", cursor.Id);
        command.Parameters.AddWithValue("key", cursor.Key);
        command.Parameters.AddWithValue("revision", cursor.Revision);
        command.Parameters.AddWithValue("hash", cursor.Hash);
        command.Parameters.AddWithValue("target", cursor.Target);
        command.Parameters.AddWithValue("policy", cursor.StoredPolicy);
        command.Parameters.AddWithValue("slot", cursor.Slot);
        var inserted = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await FinishAsync(resources).ConfigureAwait(false);
        return inserted;
    }

    private sealed record OccurrenceCursor(Guid Id, string Key, long Revision, string Hash, string Target,
        string StoredPolicy, DateTimeOffset Slot, long JitterMicroseconds, bool BoundsFrozen, Guid? UpperTenant,
        Guid? UpperOrganization, Guid? AfterTenant, Guid? AfterOrganization, bool Invalid = false, Guid Token = default)
    {
        internal bool Organizations => StoredPolicy == "serving_organizations";
        internal BackgroundJobRecipientPolicy Policy => StoredPolicy switch
        {
            "provisioned_non_archived_tenants" => BackgroundJobRecipientPolicy.ProvisionedNonArchivedTenants,
            "serving_tenants" => BackgroundJobRecipientPolicy.ServingTenants,
            "serving_organizations" => BackgroundJobRecipientPolicy.ServingOrganizations,
            _ => throw new InvalidOperationException("An occurrence requires a closed recipient policy."),
        };
        internal static OccurrenceCursor Read(Npgsql.NpgsqlDataReader row)
        {
            var manifest = BackgroundJobRecurrenceFacts.Read(row);
            var cursor = new OccurrenceCursor(row.GetGuid(10), row.GetString(11), row.GetInt64(12), row.GetString(13),
                row.GetString(14), row.GetString(15), row.GetFieldValue<DateTimeOffset>(16), row.GetInt64(17),
                !row.IsDBNull(18), row.IsDBNull(19) ? null : row.GetGuid(19), row.IsDBNull(20) ? null : row.GetGuid(20),
                row.IsDBNull(21) ? null : row.GetGuid(21), row.IsDBNull(22) ? null : row.GetGuid(22));
            return cursor with
            {
                Invalid = manifest.Fingerprint() != row.GetString(2) || cursor.Hash != row.GetString(2)
                || cursor.Key != manifest.Key || cursor.Revision != manifest.Revision || cursor.Target != manifest.Target
                || cursor.StoredPolicy != manifest.StoredRecipientPolicy
                || cursor.JitterMicroseconds != manifest.Jitter.Ticks / TimeSpan.TicksPerMicrosecond
            };
        }
    }
}
