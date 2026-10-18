using LearnStack.Application.Jobs;

namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobControlStore
{
    // One statement snapshot: complete status populations, even with empty local
    // registries. Payload/causal columns remain outside the dispatcher's grants.
    internal const string DurableObservationSql = """
        WITH
        clock AS MATERIALIZED (SELECT statement_timestamp() AS at),
        defs AS MATERIALIZED (
          SELECT * FROM unnest(
            @d_keys::text[], @d_profiles::text[], @d_queues::text[],
            @d_groups::text[], @d_tenantwide::boolean[], @d_active::boolean[],
            @d_occurrence::boolean[], @d_owners::text[])
            AS d(key, profile, queue, capacity_group, tenant_wide, active, occurrence_input, owner)
        ),
        local_recurrences AS MATERIALIZED (
          SELECT * FROM unnest(@r_keys::text[], @r_revisions::bigint[], @r_hashes::text[])
            AS r(key, revision, hash)
        ),
        job_base AS MATERIALIZED (
          SELECT j.id, j.tenant_id, j.organization_id, j.definition_key, j.profile,
            j.queue, j.capacity_group, j.original_due_at, j.phase, j.normal_attempts,
            j.finalize_attempts, j.final_outcome_attempt_id, j.finalization_status,
            j.finalization_reason, j.plan_fingerprint, j.prepared_at, j.operation_id,
            j.available_after, j.ownership_token, j.ownership_kind, j.ownership_until,
            j.terminal_reason
          FROM background_jobs j
          WHERE j.processed_at IS NULL AND j.dead_lettered_at IS NULL
        ),
        job_class AS MATERIALIZED (
          SELECT j.*,
            CASE WHEN j.profile IN ('transactional','external') THEN j.profile ELSE 'invalid' END AS label,
            CASE
              WHEN NOT (length(j.definition_key)<=150 AND j.definition_key COLLATE "C"
                  ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9_]*\.v[1-9][0-9]*$'
                  AND j.profile IN ('transactional','external')) THEN 5
              WHEN j.definition_key=ANY(@retired::text[]) THEN 2
              WHEN d.key IS NULL THEN 1
              WHEN NOT d.active THEN 3
              WHEN j.profile<>d.profile OR j.queue<>d.queue
                OR j.capacity_group IS DISTINCT FROM d.capacity_group
                OR (j.organization_id IS NULL)<>d.tenant_wide THEN 4
              ELSE 0 END AS disposition
          FROM job_base j LEFT JOIN defs d ON d.key=j.definition_key
        ),
        job_eligibility AS MATERIALIZED (
          SELECT j.*,
            (j.disposition=0 AND j.terminal_reason IS NULL
              AND j.available_after<=c.at AND j.original_due_at<=c.at
              AND NOT (j.id=ANY(@retained_jobs::uuid[]))
              AND (j.ownership_token IS NULL
                OR (j.ownership_kind='routing' AND j.ownership_until<=c.at))
              AND NOT EXISTS (SELECT 1 FROM background_job_reservations r
                WHERE r.tenant_id=j.tenant_id AND r.job_id=j.id AND r.released_at IS NULL)
              AND ((j.profile='transactional' AND j.phase='pending' AND j.normal_attempts<6)
                OR (j.profile='external' AND (
                  (j.phase='finalize_pending' AND j.finalize_attempts<6
                    AND j.normal_attempts BETWEEN 1 AND 6
                    AND j.final_outcome_attempt_id IS NOT NULL
                    AND j.finalization_status IS NOT NULL AND j.finalization_reason IS NOT NULL
                    AND j.plan_fingerprint IS NOT NULL AND j.prepared_at IS NOT NULL
                    AND j.operation_id IS NOT NULL)
                  OR (j.phase='pending' AND j.normal_attempts<6 AND j.finalization_status IS NULL
                    AND NOT EXISTS (SELECT 1 FROM background_job_attempts a
                      WHERE a.tenant_id=j.tenant_id AND a.job_id=j.id AND a.stage='normal'
                        AND ((a.no_start_at IS NULL AND a.outcome_kind IS NULL)
                          OR (a.outcome_kind IS NOT NULL AND (a.quiescent_at IS NULL
                            OR a.outcome_kind NOT IN ('failed','cancelled')
                            OR a.failure_policy IS DISTINCT FROM 'transient_repeatable')))))))))
              IS TRUE AS eligible
          FROM job_class j CROSS JOIN clock c
        ),
        -- Completed history is omitted except durable quarantine, matching today's
        -- quarantine-presence check. Future/leased obligations stay in the population.
        recurrence_base AS MATERIALIZED (
          SELECT recurrence_key, revision, next_scheduled_at, superseded_at,
            quarantined_at, available_after
          FROM background_job_recurrences
          WHERE quarantined_at IS NULL AND (superseded_at IS NULL OR next_scheduled_at<superseded_at)
        ),
        occurrence_base AS MATERIALIZED (
          SELECT id, recurrence_key, revision, manifest_hash, target_definition_key,
            recipient_policy, scheduled_at, jitter_microseconds, completed_at, quarantined_at
          FROM background_job_occurrences
          WHERE completed_at IS NULL AND quarantined_at IS NULL
        ),
        recipient_base AS MATERIALIZED (
          SELECT recurrence_key, revision, manifest_hash, target_definition_key,
            recipient_policy, scheduled_at, due_at, accepted_job_id, skipped_at, quarantined_at
          FROM background_job_recipients
          WHERE accepted_job_id IS NULL AND skipped_at IS NULL AND quarantined_at IS NULL
        ),
        needed AS MATERIALIZED (
          SELECT recurrence_key,revision FROM recurrence_base
          UNION SELECT recurrence_key,revision FROM occurrence_base
          UNION SELECT recurrence_key,revision FROM recipient_base
        ),
        -- This join hashes relevant historical manifests once, not every child row.
        manifest_atoms AS MATERIALIZED (
          SELECT m.recurrence_key,m.revision,m.manifest_hash,m.target_definition_key,
            m.schedule,m.activates_at,m.anchor_at,m.period_microseconds,m.jitter_microseconds,
            m.recipient_policy,m.misfire_policy,
            CASE m.schedule WHEN 'interval' THEN 0 WHEN 'daily' THEN 1 END AS schedule_code,
            CASE m.recipient_policy WHEN 'provisioned_non_archived_tenants' THEN 0
              WHEN 'serving_tenants' THEN 1 WHEN 'serving_organizations' THEN 2 END AS policy_code,
            CASE m.recipient_policy WHEN 'serving_organizations' THEN 1
              WHEN 'provisioned_non_archived_tenants' THEN 0 WHEN 'serving_tenants' THEN 0 END AS scope_code,
            COALESCE((length(m.recurrence_key)<=150 AND m.recurrence_key COLLATE "C"
                ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9_]*$'
              AND length(m.target_definition_key)<=150 AND m.target_definition_key COLLATE "C"
                ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9_]*\.v[1-9][0-9]*$'
              AND m.revision>0 AND m.manifest_hash COLLATE "C" ~ '^[0-9a-f]{64}$'
              AND m.schedule IN ('interval','daily') AND m.misfire_policy='coalesce_latest'
              AND m.period_microseconds BETWEEN 60000000 AND 31622400000000
              AND (m.schedule<>'daily' OR m.period_microseconds=86400000000)
              AND m.jitter_microseconds BETWEEN 0 AND LEAST(m.period_microseconds,7200000000)
              AND m.recipient_policy IN ('provisioned_non_archived_tenants','serving_tenants','serving_organizations')
              AND m.activates_at>=timestamptz '0001-01-01 00:00:00+00'
              AND m.activates_at<timestamptz '10000-01-01 00:00:00+00'
              AND m.anchor_at>=timestamptz '0001-01-01 00:00:00+00'
              AND m.anchor_at<timestamptz '10000-01-01 00:00:00+00'),false) AS atoms_ok
          FROM background_job_recurrences m JOIN needed n USING(recurrence_key,revision)
        ),
        manifest_hash AS MATERIALIZED (
          SELECT m.*,
            CASE WHEN atoms_ok THEN encode(sha256(convert_to(
              m.recurrence_key || E'\n' || m.revision::text || E'\n' ||
              ((extract(epoch FROM m.activates_at)::numeric*10000000
                  +621355968000000000)::bigint)::text || E'\n' ||
              m.target_definition_key || E'\n' || m.schedule_code::text || E'\n' ||
              ((extract(epoch FROM m.anchor_at)::numeric*10000000
                  +621355968000000000)::bigint)::text || E'\n' ||
              (m.period_microseconds*10)::text || E'\n' || (m.jitter_microseconds*10)::text || E'\n' ||
              m.policy_code::text || E'\n' || '0' || E'\n' || m.scope_code::text,
              'UTF8')),'hex') END AS calculated_hash
          FROM manifest_atoms m
        ),
        manifest_class AS MATERIALIZED (
          SELECT m.*,
            CASE WHEN lr.key IS NULL THEN 'unknown' ELSE lr.key END AS label,
            CASE
              WHEN NOT m.atoms_ok OR m.calculated_hash IS DISTINCT FROM m.manifest_hash THEN 5
              WHEN lr.key IS NULL THEN 1
              WHEN lr.revision<m.revision OR (lr.revision=m.revision AND lr.hash<>m.manifest_hash) THEN 4
              WHEN m.target_definition_key=ANY(@retired::text[]) THEN 2
              WHEN d.key IS NULL THEN 1
              WHEN NOT d.active THEN 3
              WHEN NOT d.occurrence_input OR d.tenant_wide<>(m.scope_code=0)
                OR d.owner<>split_part(m.recurrence_key,'.',1) THEN 4
              ELSE 0 END AS disposition
          FROM manifest_hash m
          LEFT JOIN local_recurrences lr ON lr.key=m.recurrence_key
          LEFT JOIN defs d ON d.key=m.target_definition_key
        ),
        recurrence_checked AS MATERIALIZED (
          SELECT r.*,COALESCE(m.label,'unknown') AS label, m.disposition AS manifest_disposition,
            CASE WHEN r.next_scheduled_at>=timestamptz '0001-01-01 00:00:00+00'
              AND r.next_scheduled_at<timestamptz '10000-01-01 00:00:00+00'
              AND r.next_scheduled_at>=m.activates_at
              AND (r.superseded_at IS NULL OR (r.superseded_at>=m.activates_at
                AND isfinite(r.superseded_at)))
              AND m.atoms_ok AND m.calculated_hash=m.manifest_hash THEN
              CASE WHEN r.next_scheduled_at<=c.at THEN
                date_bin(m.period_microseconds*interval '1 microsecond',
                  LEAST(c.at,COALESCE(r.superseded_at-interval '1 microsecond',c.at)),m.anchor_at)
              ELSE r.next_scheduled_at END
            END AS slot
          FROM recurrence_base r LEFT JOIN manifest_class m USING(recurrence_key,revision)
          CROSS JOIN clock c
        ),
        -- Slot<next is invalid only for a due slot. An ordinary future next slot must
        -- not be falsely quarantined merely because date_bin(now) precedes it.
        recurrence_class AS MATERIALIZED (
          SELECT r.*,
            CASE WHEN r.slot IS NULL OR r.slot<r.next_scheduled_at
              OR r.slot<timestamptz '0001-01-01 00:00:00+00'
              OR r.slot>=timestamptz '10000-01-01 00:00:00+00'
              THEN 5 ELSE COALESCE(r.manifest_disposition,5) END AS disposition
          FROM recurrence_checked r
        ),
        occurrence_class AS MATERIALIZED (
          SELECT o.*,COALESCE(m.label,'unknown') AS label,
            CASE WHEN m.recurrence_key IS NULL OR m.disposition=5
              OR o.manifest_hash IS DISTINCT FROM m.manifest_hash
              OR o.target_definition_key IS DISTINCT FROM m.target_definition_key
              OR o.recipient_policy IS DISTINCT FROM m.recipient_policy
              OR o.jitter_microseconds IS DISTINCT FROM m.jitter_microseconds
              OR NOT COALESCE((o.scheduled_at>=timestamptz '0001-01-01 00:00:00+00'
                AND o.scheduled_at<timestamptz '10000-01-01 00:00:00+00'),false)
              THEN 5 ELSE m.disposition END AS disposition
          FROM occurrence_base o LEFT JOIN manifest_class m USING(recurrence_key,revision)
        ),
        recipient_class AS MATERIALIZED (
          SELECT r.*,COALESCE(m.label,'unknown') AS label,
            CASE WHEN m.recurrence_key IS NULL OR m.disposition=5
              OR r.manifest_hash IS DISTINCT FROM m.manifest_hash
              OR r.target_definition_key IS DISTINCT FROM m.target_definition_key
              OR r.recipient_policy IS DISTINCT FROM m.recipient_policy
              OR NOT COALESCE((r.scheduled_at>=timestamptz '0001-01-01 00:00:00+00'
                AND r.scheduled_at<timestamptz '10000-01-01 00:00:00+00'
                AND r.due_at>=timestamptz '0001-01-01 00:00:00+00'
                AND r.due_at<timestamptz '10000-01-01 00:00:00+00'),false)
              THEN 5 ELSE m.disposition END AS disposition
          FROM recipient_base r LEFT JOIN manifest_class m USING(recurrence_key,revision)
        ),
        obligations AS MATERIALIZED (
          SELECT 'job'::text AS artifact,disposition FROM job_class WHERE phase<>'quarantined'
          UNION ALL SELECT 'recurrence',disposition FROM recurrence_class WHERE quarantined_at IS NULL
          UNION ALL SELECT 'occurrence',disposition FROM occurrence_class
            WHERE completed_at IS NULL AND quarantined_at IS NULL
          UNION ALL SELECT 'recipient',disposition FROM recipient_class
            WHERE accepted_job_id IS NULL AND skipped_at IS NULL AND quarantined_at IS NULL
        ),
        output AS (
          SELECT 1 AS family,j.label,0 AS code,count(*)::bigint AS n,
            count(*) FILTER (WHERE j.eligible)::bigint AS eligible,
            COALESCE(GREATEST(0,extract(epoch FROM c.at-min(j.original_due_at)
              FILTER (WHERE j.eligible))),0)::double precision AS age
          FROM job_eligibility j CROSS JOIN clock c GROUP BY j.label,c.at
          UNION ALL
          SELECT 2,artifact,disposition,count(*)::bigint,0::bigint,0::double precision
            FROM obligations WHERE disposition<>0 GROUP BY artifact,disposition
          UNION ALL
          SELECT 3,'job',0,count(*)::bigint,0::bigint,0::double precision
            FROM background_jobs WHERE phase='quarantined'
          UNION ALL
          SELECT 3,'recurrence',0,count(*)::bigint,0::bigint,0::double precision
            FROM background_job_recurrences WHERE quarantined_at IS NOT NULL
          UNION ALL
          SELECT 3,'occurrence',0,count(*)::bigint,0::bigint,0::double precision
            FROM background_job_occurrences WHERE quarantined_at IS NOT NULL
          UNION ALL
          SELECT 3,'recipient',0,count(*)::bigint,0::bigint,0::double precision
            FROM background_job_recipients WHERE quarantined_at IS NOT NULL
          UNION ALL
          SELECT 4,CASE WHEN capacity_group=ANY(@capacity_groups::text[]) THEN capacity_group
              WHEN length(capacity_group)<=150 AND capacity_group COLLATE "C"
                ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9_]*$' THEN 'unknown' ELSE 'invalid' END,
            0,count(*)::bigint,0::bigint,0::double precision
            FROM background_job_reservations WHERE released_at IS NULL GROUP BY 2
          UNION ALL
          SELECT 5,label,1,count(*)::bigint,0::bigint,0::double precision
            FROM recurrence_class WHERE quarantined_at IS NULL GROUP BY label
          UNION ALL
          SELECT 5,label,2,count(*)::bigint,0::bigint,0::double precision
            FROM occurrence_class WHERE completed_at IS NULL AND quarantined_at IS NULL GROUP BY label
          UNION ALL
          SELECT 5,label,3,count(*)::bigint,0::bigint,0::double precision
            FROM recipient_class WHERE accepted_job_id IS NULL AND skipped_at IS NULL
              AND quarantined_at IS NULL GROUP BY label
        )
        SELECT family AS observation_family,label,code,n,eligible,age FROM output ORDER BY family,label,code;
        """;

    internal async Task<OwnedBackgroundJobControl<BackgroundJobDurableObservation>> ReadDurableObservationAsync(
        BackgroundJobObservationManifest manifest, IReadOnlyCollection<Guid> retainedJobs,
        CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(retainedJobs);
        var resources = new BackgroundJobControlResources { IsProbe = true };
        BackgroundJobDurableObservation? value = null;
        using var deadline = Deadline(cancellationToken);
        try
        {
            if (await OpenAsync(resources, deadline.Token).ConfigureAwait(false))
            {
                var command = Command(resources, DurableObservationSql);
                manifest.Bind(command, retainedJobs);
                resources.SetReader(await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false));
                var points = new List<BackgroundJobObservationPoint>();
                while (await resources.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false))
                {
                    if (points.Count >= manifest.MaximumRows)
                        throw new InvalidOperationException("The durable job observation exceeds its finite inventory.");
                    points.Add(new((BackgroundJobObservationFamily)resources.Reader.GetInt32(0),
                        resources.Reader.GetString(1), resources.Reader.GetInt32(2), resources.Reader.GetInt64(3),
                        resources.Reader.GetInt64(4), resources.Reader.GetDouble(5)));
                }
                var observed = BackgroundJobDurableObservation.Create(manifest, points);
                await FinishAsync(resources).ConfigureAwait(false);
                await resources.Transaction!.RollbackAsync(deadline.Token).ConfigureAwait(false);
                resources.ProbeRollbackAcknowledged = true;
                value = observed;
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { resources.RecordFailure(failure); }
        finally { await resources.CleanupAsync().ConfigureAwait(false); }
        if (!resources.ProbeRollbackAcknowledged || !resources.CleanupConfirmed || resources.Failure is not null) value = null;
        return new(value, resources, Array.Empty<BackgroundJobCommitProbe>());
    }
}
