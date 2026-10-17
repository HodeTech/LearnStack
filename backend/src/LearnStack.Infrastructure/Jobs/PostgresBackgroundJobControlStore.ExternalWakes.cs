namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class PostgresBackgroundJobControlStore
{
    // Separate seeks keep expired ownership and one-time legacy normalization
    // from repeatedly scanning already scheduled or future recovery work.
    private static string ExternalWakeSelectionSql(bool unarmed)
    {
        const string Eligible = """
            profile='external' AND capacity_group IS NOT NULL AND current_attempt_id IS NOT NULL
            AND normal_attempts BETWEEN 1 AND 6 AND finalize_attempts=0
            AND finalization_status IS NULL AND final_outcome_attempt_id IS NULL
            AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
            AND EXISTS (SELECT 1 FROM unnest(@externalDefinitions,@externalQueues,@externalGroups,@externalTenantWide)
                AS definition(key,queue,capacity,tenant_wide)
                WHERE definition.key=j.definition_key AND definition.queue=j.queue
                    AND definition.capacity=j.capacity_group AND definition.tenant_wide=(j.organization_id IS NULL))
            AND NOT (id=ANY(@blocked))
            """;
        var ownership = unarmed
            ? "phase='reconciling' AND ownership_token IS NULL AND probe_after IS NULL ORDER BY created_at,id"
            : """
                ownership_token IS NOT NULL AND phase<>'quarantined' AND ownership_until<=statement_timestamp()
                AND ((phase IN ('preparing','prepared','executing') AND ownership_kind='execution')
                    OR (phase='reconciling' AND ownership_kind='recovery')) ORDER BY ownership_until,created_at,id
                """;
        return $"SELECT id FROM background_jobs j WHERE {Eligible} AND {ownership} LIMIT @batch FOR UPDATE SKIP LOCKED";
    }

    private async Task<BackgroundJobControlChange?> ParkExternalRecoveryAsync(BackgroundJobControlResources resources,
        ControlRow row, bool unarmed, CancellationToken token)
    {
        if (row.Profile != "external" || row.CapacityGroup is null || !row.MatchingNormalAttempt
            || row.NormalAttempts is < 1 or > 6 || row.FinalizeAttempts != 0 || row.SelectedNormalAttemptId is not null
            || (unarmed ? row.Phase != "reconciling" || row.OwnershipToken is not null || row.ProbeAfter is not null
                : !(row.Phase is "preparing" or "prepared" or "executing" && row.OwnershipKind == "execution"
                        && row.OwnershipToken == row.LedgerToken
                    || row.Phase == "reconciling" && row.OwnershipKind == "recovery"
                        && row.OwnershipToken is not null && row.OwnershipToken != row.LedgerToken)))
            return null;
        var command = Command(resources, """
            UPDATE background_jobs SET phase='reconciling',probe_after=clock_timestamp(),
                ownership_token=NULL,ownership_kind=NULL,ownership_until=NULL
            WHERE id=@id AND current_attempt_id=@attempt AND wake_generation=@generation
                AND ownership_token IS NOT DISTINCT FROM @token AND profile='external'
                AND normal_attempts BETWEEN 1 AND 6 AND finalize_attempts=0
                AND finalization_status IS NULL AND final_outcome_attempt_id IS NULL
                AND ((@unarmed AND phase='reconciling' AND ownership_token IS NULL AND probe_after IS NULL)
                    OR (NOT @unarmed AND ownership_token IS NOT NULL AND ownership_until<=clock_timestamp()
                        AND ((phase IN ('preparing','prepared','executing') AND ownership_kind='execution')
                            OR (phase='reconciling' AND ownership_kind='recovery'))))
                AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL
            RETURNING id,wake_generation,phase,terminal_reason
            """);
        AddRowFence(command, row);
        command.Parameters.AddWithValue("unarmed", unarmed);
        return await ReadChangeAsync(resources, token).ConfigureAwait(false);
    }
}
