using FluentAssertions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The data-request storage controls that must commit, or that need one other layer removed to be observed alone:
/// the concurrent-creation race, the guard's snapshot immutability behind a deliberately widened grant, and the
/// 24-hour window and 7-day expiry against backdated rows. Each runs in its own disposable database (Database
/// Standards § Parent organization mirrors: a control removal commits before a separate application connection tests
/// the remaining layer), so the shared schema stays untouched.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DataRequestStorageControlTests(SchemaFixture schema)
{
    [Fact]
    public async Task Concurrent_creations_for_one_subject_and_group_cannot_both_commit()
    {
        await using var database = await SeededAsync();
        await using var first = await OpenAppAsync(database);
        await using var second = await OpenAppAsync(database);

        // With the guard live: the subject's User fence serializes the two creations and the index refuses the loser.
        await using (var winner = await first.BeginTransactionAsync())
        {
            await FenceAsync(first, winner, SchemaFixture.TenantA, null, AdminA);
            await InsertRequestAsync(first, winner, Guid.CreateVersion7(), SchemaFixture.TenantA, null, AdminA, [AdminMembershipA]);
            await using var loser = await second.BeginTransactionAsync();
            var contender = Task.Run(async () =>
            {
                await FenceAsync(second, loser, SchemaFixture.TenantA, null, AdminA);
                await InsertRequestAsync(second, loser, Guid.CreateVersion7(), SchemaFixture.TenantA, null, AdminA, [AdminMembershipA]);
            });
            await WaitUntilBlockedAsync(database, second.ProcessID, contender, "the contender waits on the subject's fence while the winner is open");
            await winner.CommitAsync();
            var refusal = (await FluentActions.Awaiting(() => contender).Should().ThrowAsync<PostgresException>()).Which;
            refusal.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
            refusal.ConstraintName.Should().Be("ux_data_subject_requests_active");
        }

        // With the guard removed, the partial unique index alone still decides an in-flight race.
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(owner, null,
                "ALTER TABLE public.data_subject_requests DISABLE TRIGGER tg_data_subject_requests_mutation_guard");
        await using (var winner = await first.BeginTransactionAsync())
        {
            await ScopeAsync(first, winner, SchemaFixture.TenantA);
            await InsertRequestAsync(first, winner, Guid.CreateVersion7(), SchemaFixture.TenantA, null, AdminA, [AdminMembershipA], "erasure");
            await using var loser = await second.BeginTransactionAsync();
            await ScopeAsync(second, loser, SchemaFixture.TenantA);
            var contender = InsertRequestAsync(second, loser, Guid.CreateVersion7(), SchemaFixture.TenantA, null, AdminA,
                [AdminMembershipA], "erasure");
            await WaitUntilBlockedAsync(database, second.ProcessID, contender, "the second insert waits on the first's uncommitted index entry");
            await winner.CommitAsync();
            (await FluentActions.Awaiting(() => contender).Should().ThrowAsync<PostgresException>()).Which.ConstraintName
                .Should().Be("ux_data_subject_requests_active");
        }

        await using var reader = await OpenAppAsync(database);
        await using var read = await reader.BeginTransactionAsync();
        await ScopeAsync(reader, read, SchemaFixture.TenantA);
        (await ScalarAsync<string>(reader, read, """
            SELECT string_agg(kind || ':' || count, ',' ORDER BY kind) FROM (SELECT kind, count(*) FROM public.data_subject_requests
                WHERE subject_user_id=@subject GROUP BY kind) rows
            """, ("subject", AdminA))).Should().Be("erasure:1,export:1", "exactly one creation of each race committed");
    }

    [Fact]
    public async Task The_guard_refuses_every_snapshot_change_even_where_a_grant_would_allow_it()
    {
        await using var database = await SeededAsync();
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await SchemaQueries.ExecuteAsync(owner, null, """
                GRANT UPDATE (organization_id, kind, origin, subject_user_id, requester_user_id, membership_snapshot, execute_at,
                    created_at, created_by) ON public.data_subject_requests TO learnstack_app
                """);
        await using var app = await OpenAppAsync(database);
        foreach (var set in new[]
        {
            "kind='erasure'",
            "origin='administrator'",
            $"subject_user_id='{AdminA}'",
            $"requester_user_id='{AdminA}'",
            $"membership_snapshot='{Snapshot(LearnerMembershipA, BranchMembershipA1)}'",
            "execute_at=now()",
            "created_at=created_at - interval '1 second'",
            $"created_by='{AdminA}'",
        })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await FenceAsync(app, transaction, SchemaFixture.TenantA, null, null);
            var act = () => StepAsync(app, transaction, TenantWideA.Request, set);
            var refusal = (await act.Should().ThrowAsync<PostgresException>()).Which;
            refusal.SqlState.Should().Be("23514", set);
            refusal.MessageText.Should().Be("Data request immutable state refused.", set);
        }

        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1, null);
            var act = () => StepAsync(app, transaction, BranchA1.Request, $"organization_id='{SchemaFixture.OrgA2}'");
            (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Be("Data request immutable state refused.",
                "the guard fires before the shared organization immutability trigger, and both refuse the move");
        }
    }

    [Fact]
    public async Task The_window_and_the_expiry_are_database_clock_checks()
    {
        await using var database = await SeededAsync();
        var erasure = Guid.CreateVersion7();
        var lateErasure = Guid.CreateVersion7();
        await using (var app = await OpenAppAsync(database))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, SchemaFixture.TenantB, null, AdminB);
            await InsertRequestAsync(app, transaction, erasure, SchemaFixture.TenantB, null, AdminB, [AdminMembershipB], "erasure");
            await LockAsync(app, transaction, SchemaFixture.TenantB, PersonB, false);
            await ScopeAsync(app, transaction, SchemaFixture.TenantB, SchemaFixture.OrgB1);
            await InsertRequestAsync(app, transaction, lateErasure, SchemaFixture.TenantB, SchemaFixture.OrgB1, PersonB, [BranchMembershipB], "erasure");
            await transaction.CommitAsync();
        }

        // Backdate as the owner with every application guard paused, then restore them: the rows are what a request
        // filed 25 hours ago and an artifact completed 8 days ago look like.
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            await using var transaction = await owner.BeginTransactionAsync();
            foreach (var table in SchemaTableClasses.IdentityDataRequest.Where(table => table != "data_export_chunks"))
                await ExecuteAsync(owner, transaction, $"ALTER TABLE public.{table} DISABLE TRIGGER tg_{table}_mutation_guard");
            foreach (var (tenant, organization, request) in new (Guid, Guid?, Guid)[]
            {
                (SchemaFixture.TenantB, null, erasure), (SchemaFixture.TenantB, SchemaFixture.OrgB1, lateErasure),
            })
            {
                await ScopeAsync(owner, transaction, tenant, organization);
                await ExecuteAsync(owner, transaction, """
                    UPDATE public.data_subject_requests SET created_at=created_at - interval '25 hours',
                        execute_at=execute_at - interval '25 hours' WHERE id=@id
                    """, ("id", request));
            }
            await ScopeAsync(owner, transaction, SchemaFixture.TenantB);
            await ExecuteAsync(owner, transaction, """
                UPDATE public.data_export_artifacts SET created_at=created_at - interval '8 days',
                    expires_at=expires_at - interval '8 days' WHERE id=@artifact;
                UPDATE public.data_export_tickets SET created_at=created_at - interval '8 days',
                    expires_at=expires_at - interval '8 days' WHERE id=@ticket;
                UPDATE public.data_subject_requests SET created_at=created_at - interval '8 days',
                    completed_at=completed_at - interval '8 days', updated_at=updated_at - interval '8 days' WHERE id=@request
                """, ("artifact", TenantWideB.Artifact!.Value), ("ticket", TenantWideB.Ticket!.Value), ("request", TenantWideB.Request));
            // The artifact's deferred completeness check must run before its table can be altered again.
            await ExecuteAsync(owner, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            foreach (var table in SchemaTableClasses.IdentityDataRequest.Where(table => table != "data_export_chunks"))
                await ExecuteAsync(owner, transaction, $"ALTER TABLE public.{table} ENABLE TRIGGER tg_{table}_mutation_guard");
            await transaction.CommitAsync();
        }

        await using var connection = await OpenAppAsync(database);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await FenceAsync(connection, transaction, SchemaFixture.TenantB, null, null);
            var cancel = () => StepAsync(connection, transaction, erasure, "status='cancelled'");
            (await cancel.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Be("Data request transition refused.",
                "the cancellable window has closed");
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await FenceAsync(connection, transaction, SchemaFixture.TenantB, null, null);
            await StepAsync(connection, transaction, erasure, "status='fulfilling'");
            await StepAsync(connection, transaction, erasure, "status='awaiting_privileged_step'");
            var complete = () => StepAsync(connection, transaction, erasure, "status='completed',completed_at=now()");
            (await complete.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Be("Data request transition refused.",
                "only the operator's privileged step completes an erasure");
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await FenceAsync(connection, transaction, SchemaFixture.TenantB, SchemaFixture.OrgB1, null);
            await StepAsync(connection, transaction, lateErasure, "status='fulfilling'");
            await StepAsync(connection, transaction, lateErasure, "status='blocked',last_refusal='blocked_last_administrator'");
            await transaction.RollbackAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await FenceAsync(connection, transaction, SchemaFixture.TenantB, null, null);
            var artifact = TenantWideB.Artifact!.Value;
            var revoke = () => ExecuteAsync(connection, transaction, """
                UPDATE public.data_export_artifacts SET status='revoked',purged_at=now(),row_version=row_version+1,
                    updated_at=now(),updated_by=@system WHERE id=@id
                """, ("id", artifact), ("system", SystemActor));
            (await revoke.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Be("Data export artifact transition refused.",
                "an expired artifact is purged, not revoked");
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await FenceAsync(connection, transaction, SchemaFixture.TenantB, null, null);
            var artifact = TenantWideB.Artifact!.Value;
            var mint = () => InsertTicketAsync(connection, transaction, Guid.CreateVersion7(), SchemaFixture.TenantB, null, artifact, AdminB);
            (await mint.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Be("Data export ticket artifact refused.",
                "expiry is a time check whether or not the purge ran");
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await FenceAsync(connection, transaction, SchemaFixture.TenantB, null, null);
            var consume = () => ExecuteAsync(connection, transaction,
                "UPDATE public.data_export_tickets SET consumed_at=created_at WHERE id=@id", ("id", TenantWideB.Ticket!.Value));
            (await consume.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Be("Data export ticket consumption refused.");
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await FenceAsync(connection, transaction, SchemaFixture.TenantB, null, null);
            var artifact = TenantWideB.Artifact!.Value;
            await ExecuteAsync(connection, transaction, """
                UPDATE public.data_export_artifacts SET status='expired',row_version=row_version+1,updated_at=now(),updated_by=@system
                WHERE id=@id
                """, ("id", artifact), ("system", SystemActor));
            await ExecuteAsync(connection, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            await ExecuteAsync(connection, transaction, "DELETE FROM public.data_export_chunks WHERE artifact_id=@id", ("id", artifact));
            await ExecuteAsync(connection, transaction, """
                UPDATE public.data_export_artifacts SET status='purged',purged_at=now(),row_version=row_version+1,updated_at=now(),
                    updated_by=@system WHERE id=@id
                """, ("id", artifact), ("system", SystemActor));
            await transaction.CommitAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ScopeAsync(connection, transaction, SchemaFixture.TenantB);
            (await ScalarAsync<string>(connection, transaction, """
                SELECT a.status || ':' || (a.purged_at IS NOT NULL)::text || ':'
                    || (SELECT count(*) FROM public.data_export_chunks c WHERE c.artifact_id = a.id)::text
                FROM public.data_export_artifacts a WHERE a.id = @id
                """, ("id", TenantWideB.Artifact!.Value))).Should().Be("purged:true:0",
                "an expired artifact is purged; its metadata row stays as the receipt");
        }
    }

    /// <summary>
    /// ADR-0107 § 8 and ADR-0108 § 8: the guard's platform arm, on erasures the application role created and moved through
    /// its own arrows. <c>learnstack_platform</c> takes exactly <c>awaiting_privileged_step → completed</c>, with the version
    /// step, the audit log's retained kinds as the only change to the result, and the completion stamped by the guard; the
    /// application role cannot take that arrow, and the platform role cannot take one of the application's.
    /// </summary>
    [Fact]
    public async Task The_platform_arm_admits_exactly_an_erasure_s_completion_and_each_role_keeps_to_its_own_arrows()
    {
        await using var database = await SeededAsync();
        var awaiting = Guid.CreateVersion7();
        var scheduled = Guid.CreateVersion7();
        await using (var app = await OpenAppAsync(database))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await FenceAsync(app, transaction, SchemaFixture.TenantB, null, AdminB);
            await InsertRequestAsync(app, transaction, scheduled, SchemaFixture.TenantB, null, AdminB, [AdminMembershipB], "erasure");
            // A closure's fan-out erasure starts fulfilling, so the job's arrows reach the privileged step without a window.
            await LockAsync(app, transaction, SchemaFixture.TenantB, PersonB, false);
            await ScopeAsync(app, transaction, SchemaFixture.TenantB, SchemaFixture.OrgB1);
            await ExecuteAsync(app, transaction, """
                INSERT INTO public.data_subject_requests(id,tenant_id,organization_id,kind,origin,subject_user_id,requester_user_id,
                    status,membership_snapshot,execute_at,created_at,created_by)
                VALUES(@id,@tenant,@organization,'erasure','closure_fanout',@subject,NULL,'fulfilling',@snapshot::jsonb,NULL,now(),@system)
                """, ("id", awaiting), ("tenant", SchemaFixture.TenantB), ("organization", SchemaFixture.OrgB1), ("subject", PersonB),
                ("snapshot", Snapshot(BranchMembershipB)), ("system", SystemActor));
            await StepAsync(app, transaction, awaiting, "result='{\"consent_evidence\":2,\"audit_actor_reference\":9}'");
            await StepAsync(app, transaction, awaiting, "status='awaiting_privileged_step'");
            await transaction.CommitAsync();
        }

        await using (var app = await OpenAppAsync(database))
        {
            foreach (var (set, reason) in new[]
            {
                ("status='completed',completed_at=now()", "Data request transition refused."),
                ("result='{\"consent_evidence\":2,\"audit_actor_reference\":4}'", "Data request result refused."),
            })
            {
                await using var transaction = await app.BeginTransactionAsync();
                await FenceAsync(app, transaction, SchemaFixture.TenantB, SchemaFixture.OrgB1, null);
                var act = () => StepAsync(app, transaction, awaiting, set);
                (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Be(reason,
                    "the application's arms are unchanged and never take the operator's step: " + set);
            }
        }

        await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.PlatformConnectionString);
        async Task<PostgresException> RefusedAsync(string sql, params (string Name, object? Value)[] values)
        {
            await using var transaction = await platform.BeginTransactionAsync();
            var act = () => ExecuteAsync(platform, transaction, sql, values);
            return (await act.Should().ThrowAsync<PostgresException>(sql)).Which;
        }

        const string Complete = "UPDATE public.data_subject_requests SET {0} WHERE id=@id";
        foreach (var (target, set, reason) in new[]
        {
            (awaiting, "status='completed'", "Data request immutable state refused."),
            (awaiting, "status='completed',row_version=row_version+2", "Data request immutable state refused."),
            (awaiting, "status='completed',row_version=row_version+1,result='{\"consent_evidence\":3,\"audit_actor_reference\":4}'",
                "Data request result refused."),
            (awaiting, "status='completed',row_version=row_version+1,result='{\"audit_actor_reference\":4}'", "Data request result refused."),
            (awaiting, "status='completed',row_version=row_version+1,result='{\"consent_evidence\":2,\"request_receipt\":1}'",
                "Data request result refused."),
            (awaiting, "status='cancelled',row_version=row_version+1", "Data request transition refused."),
            (awaiting, "status='failed',row_version=row_version+1", "Data request transition refused."),
            (awaiting, "status='fulfilling',row_version=row_version+1", "Data request transition refused."),
            (scheduled, "status='fulfilling',row_version=row_version+1", "Data request transition refused."),
            (scheduled, "status='cancelled',row_version=row_version+1", "Data request transition refused."),
            (scheduled, "status='completed',row_version=row_version+1", "Data request transition refused."),
        })
        {
            var refusal = await RefusedAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, Complete, set), ("id", target));
            refusal.SqlState.Should().Be("23514", set);
            refusal.MessageText.Should().Be(reason, set);
        }

        (await RefusedAsync("UPDATE public.data_subject_requests SET last_refusal='blocked_hold' WHERE id=@id", ("id", awaiting)))
            .SqlState.Should().Be("42501", "a column outside the grant never reaches the guard");
        (await RefusedAsync("DELETE FROM public.data_subject_requests WHERE id=@id", ("id", awaiting))).SqlState.Should().Be("42501");

        // The one arrow. The statement's own completion time is replaced by the database clock the guard stamps.
        await using (var transaction = await platform.BeginTransactionAsync())
        {
            var completedAt = await ScalarAsync<DateTime>(platform, transaction, """
                UPDATE public.data_subject_requests SET status='completed',completed_at='2001-01-01T00:00:00Z',row_version=row_version+1,
                    result='{"consent_evidence":2,"audit_actor_reference":4,"unscoped_audit_rows":1}'
                WHERE id=@id RETURNING completed_at
                """, ("id", awaiting));
            completedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1), "no caller supplies the completion time");
            await transaction.CommitAsync();
        }

        await using (var app = await OpenAppAsync(database))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantB, SchemaFixture.OrgB1);
            (await ScalarAsync<string>(app, transaction, """
                SELECT status || ':' || row_version || ':' || (completed_at = updated_at)::text || ':' || updated_by::text || ':'
                    || (result = '{"consent_evidence":2,"audit_actor_reference":4,"unscoped_audit_rows":1}'::jsonb)::text
                FROM public.data_subject_requests WHERE id=@id
                """, ("id", awaiting))).Should().Be($"completed:3:true:{SystemActor}:true",
                "the arm stamps the completion and the system actor, steps the version once and keeps the tenant side's kinds");
        }

        (await RefusedAsync("UPDATE public.data_subject_requests SET status='completed',row_version=row_version+1 WHERE id=@id",
            ("id", awaiting))).MessageText.Should().Be("Data request transition refused.", "a completed erasure is terminal");
    }

    /// <summary>
    /// ADR-0107 § 4: a chunk written while a revocation of its artifact is in flight waits for it and then meets the
    /// committed status, so ciphertext never lands on a withdrawn artifact (which nothing would clean afterwards).
    /// </summary>
    [Fact]
    public async Task A_chunk_insert_racing_a_revocation_waits_for_it_and_is_refused()
    {
        await using var database = await SeededAsync();
        await using var revoker = await OpenAppAsync(database);
        await using var writer = await OpenAppAsync(database);
        var artifact = TenantWideA.Artifact!.Value;
        await using (var revocation = await revoker.BeginTransactionAsync())
        {
            await FenceAsync(revoker, revocation, SchemaFixture.TenantA, null, null);
            // The store's revocation: lock the artifact, record it revoked, then delete its chunks; not yet committed.
            await ExecuteAsync(revoker, revocation, "SELECT id FROM public.data_export_artifacts WHERE id=@id FOR UPDATE", ("id", artifact));
            await ExecuteAsync(revoker, revocation, """
                UPDATE public.data_export_artifacts SET status='revoked',purged_at=now(),row_version=row_version+1,
                    updated_at=now(),updated_by=@subject WHERE id=@id
                """, ("id", artifact), ("subject", PersonA));
            await ExecuteAsync(revoker, revocation, "DELETE FROM public.data_export_chunks WHERE artifact_id=@id", ("id", artifact));

            await using var insert = await writer.BeginTransactionAsync();
            await ScopeAsync(writer, insert, SchemaFixture.TenantA);
            var contender = InsertChunkAsync(writer, insert, SchemaFixture.TenantA, null, artifact, 0);
            await WaitUntilBlockedAsync(database, writer.ProcessID, contender, "the chunk guard reads its artifact behind the revocation's row lock");
            await revocation.CommitAsync();

            var refusal = (await FluentActions.Awaiting(() => contender).Should().ThrowAsync<PostgresException>()).Which;
            refusal.SqlState.Should().Be("23514");
            refusal.MessageText.Should().Be("Data export chunk refused.", "the guard re-read the committed revoked status");
        }

        await using var reader = await OpenAppAsync(database);
        await using var read = await reader.BeginTransactionAsync();
        await ScopeAsync(reader, read, SchemaFixture.TenantA);
        (await ScalarAsync<string>(reader, read, """
            SELECT a.status || ':' || (SELECT count(*) FROM public.data_export_chunks c WHERE c.artifact_id = a.id)::text
            FROM public.data_export_artifacts a WHERE a.id = @id
            """, ("id", artifact))).Should().Be("revoked:0", "a withdrawn artifact keeps no ciphertext");
    }

    /// <summary>
    /// Waits until the backend <paramref name="pid"/> waits on a lock, from <c>pg_locks</c> (readable by every role, unlike
    /// another role's <c>pg_stat_activity</c> wait columns), instead of sleeping and hoping.
    /// </summary>
    private static async Task WaitUntilBlockedAsync(DisposableSchemaDatabase database, int pid, Task contender, string because)
    {
        await using var observer = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            contender.IsCompleted.Should().BeFalse(because);
            if (await ScalarAsync<bool>((NpgsqlConnection)observer, null,
                    "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE pid = @pid AND NOT granted)", ("pid", pid)))
                return;
            DateTime.UtcNow.Should().BeBefore(deadline, because);
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    private async Task<DisposableSchemaDatabase> SeededAsync()
    {
        var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        return database;
    }

    private static Task<NpgsqlConnection> OpenAppAsync(DisposableSchemaDatabase database) =>
        DataExportTestKit.OpenAppAsync(database.AppConnectionString);
}
