using System.Data;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Time;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.Bindings;

/// <summary>
/// The controlled issuer rebind (ADR-0104): the database half the offline tool drives, on the confined role
/// <c>learnstack_identity_rebind</c> and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order, and why.</b> One direct, non-pooled transaction takes the exclusive rebind advisory lock as its first
/// statement, so a start or a creation waits for it. Under that lock the tool reads the stored authority; a run whose
/// target already equals the stored issuer ends there, writing nothing, not even a row. Otherwise the intent row
/// commits on its own short transaction, so an aborted attempt is on record, and only then the freeze check, the
/// continuity proof, the issuer update and the result row run on the main transaction, which commits them together
/// or not at all.
/// </para>
/// <para>
/// <b>Every refusal leaves the state unchanged</b> and a retry repeats the whole proof. An unknown COMMIT is
/// reconciled by reading the issuer on a fresh connection: the target means it applied, the old value means it did
/// not, anything else is reported as unknown.
/// </para>
/// </remarks>
public sealed class IdentityIssuerRebind(IAuditStore audit, IClock clock)
{
    /// <summary>The only database role the tool may use.</summary>
    public const string RoleName = "learnstack_identity_rebind";

    /// <summary>The runtime roles whose sessions freeze the rebind: the platform role runs in the same hosts.</summary>
    private static readonly string[] RuntimeRoles =
        ["learnstack_app", "learnstack_outbox_admin", "learnstack_identity_ingress", "learnstack_platform"];

    private const string AdvisoryLock = "SELECT pg_catalog.pg_advisory_xact_lock(@key)";

    /// <summary>The longest the tool waits for the exclusive lock when none is asked for.</summary>
    public static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(30);

    /// <summary>Runs one rebind to <paramref name="target"/> and returns how it ended; it never throws for a refusal.</summary>
    /// <param name="connectionString">The rebind role's connection string; any other role is refused.</param>
    /// <param name="target">The stable authority and the new issuer.</param>
    /// <param name="admission">The operator's admission for the rebind operation.</param>
    /// <param name="realm">The read-only view of the new realm.</param>
    /// <param name="lockWait">How long the first statement waits for the exclusive lock; <see cref="DefaultLockWait"/> when null.</param>
    /// <param name="cancellationToken">The run's deadline.</param>
    public async Task<IdentityRebindOutcome> RunAsync(
        string connectionString,
        IdentityAuthorityRegistration target,
        PlatformAdmission admission,
        INewRealmContinuitySource realm,
        TimeSpan? lockWait = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(realm);

        var outcome = await RunCoreAsync(connectionString, target, admission, realm, lockWait ?? DefaultLockWait, cancellationToken);
        IdentityRebindMetrics.Record(outcome);
        return outcome;
    }

    private async Task<IdentityRebindOutcome> RunCoreAsync(
        string connectionString,
        IdentityAuthorityRegistration target,
        PlatformAdmission admission,
        INewRealmContinuitySource realm,
        TimeSpan lockWait,
        CancellationToken cancellationToken)
    {
        // The admission is the operator's authority: this operation only, fresh when the run begins (the proof
        // may run longer than the admission's age, which bounds the start and not the work), and spent once.
        if (!ReferenceEquals(admission.Operation, PlatformOperations.IdentityAuthorityRebind)
            || string.IsNullOrEmpty(admission.Operator.CorrelationId)
            || (clock.UtcNow - admission.IssuedAt).Duration() > PlatformAdmission.MaximumAge
            || !admission.TryConsume())
        {
            return Refused(IdentityRebindRefusal.NotAdmitted);
        }

        var connection = TryNormalize(connectionString, lockWait);
        if (connection is null)
        {
            return Refused(IdentityRebindRefusal.RoleInvalid);
        }

        NpgsqlConnection? main = null;
        NpgsqlTransaction? transaction = null;
        try
        {
            main = await OpenAsync(connection, cancellationToken);
            if (!await HasExactPostureAsync(main, cancellationToken))
            {
                return Refused(IdentityRebindRefusal.RoleInvalid);
            }

            transaction = await main.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await ExecuteAsync(main, transaction, AdvisoryLock, cancellationToken,
                ("key", IdentityAdvisoryLockKey.ForAuthorityRebind()));

            var (storedAuthority, storedIssuer) = await ReadAuthorityAsync(main, transaction, cancellationToken);
            if (storedAuthority == target.AuthorityId.Value
                && string.Equals(storedIssuer, target.Issuer, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new IdentityRebindOutcome(IdentityRebindResult.AlreadyApplied);
            }

            if (!await TryWriteIntentAsync(connection, admission, storedAuthority, storedIssuer, target.Issuer, cancellationToken))
            {
                return Refused(IdentityRebindRefusal.AuditFailed);
            }

            if (storedAuthority != target.AuthorityId.Value)
            {
                return Refused(IdentityRebindRefusal.AuthorityMismatch);
            }

            var sessions = await CountRuntimeSessionsAsync(main, transaction, cancellationToken);
            if (sessions > 0)
            {
                return new IdentityRebindOutcome(IdentityRebindResult.Refused, IdentityRebindRefusal.LiveSessions, null, sessions);
            }

            var snapshot = await ReadRealmAsync(realm, target.Issuer, cancellationToken);
            if (snapshot is null)
            {
                return Refused(IdentityRebindRefusal.NewRealmUnavailable);
            }

            if (!string.Equals(snapshot.DiscoveredIssuer, target.Issuer, StringComparison.Ordinal))
            {
                return Refused(IdentityRebindRefusal.NewRealmIssuerMismatch);
            }

            if (!snapshot.Complete || snapshot.SigningKeyCount < 1)
            {
                return Refused(IdentityRebindRefusal.NewRealmIncomplete);
            }

            var stored = await ReadUsersAsync(main, transaction, storedAuthority, cancellationToken);
            var continuity = IdentityRebindContinuity.Evaluate(target.AuthorityId, stored, snapshot.Subjects);
            if (!continuity.Holds)
            {
                return new IdentityRebindOutcome(
                    IdentityRebindResult.Refused, IdentityRebindRefusal.ContinuityBroken, continuity, sessions);
            }

            // The freeze is asked again at the last moment: the proof may have run for minutes.
            var late = await CountRuntimeSessionsAsync(main, transaction, cancellationToken);
            if (late > 0)
            {
                return new IdentityRebindOutcome(
                    IdentityRebindResult.Refused, IdentityRebindRefusal.LiveSessions, continuity, late);
            }

            await ChangeIssuerAsync(main, transaction, storedIssuer, target.Issuer, cancellationToken);
            await audit.WriteIdentityRebindResultAsync(
                Draft(admission, admission.Operation.AuditSlug, AuditOutcome.Success, storedAuthority,
                    storedIssuer, target.Issuer, continuity),
                admission, main, transaction, cancellationToken);

            return await CommitAsync(transaction, connection, storedIssuer, target.Issuer, continuity, cancellationToken);
        }
        catch (AuditWriteFailedException)
        {
            return Refused(IdentityRebindRefusal.AuditFailed);
        }
        catch (PostgresException failure) when (failure.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return Refused(IdentityRebindRefusal.RebindInProgress);
        }
        catch (Exception failure) when (failure is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return Refused(IdentityRebindRefusal.DatabaseUnavailable);
        }
        catch (OperationCanceledException)
        {
            return Refused(IdentityRebindRefusal.Cancelled);
        }
        finally
        {
            // An uncommitted transaction rolls back here, and with it the lock. A failure to dispose never replaces
            // the outcome of the run: an applied rebind must not be reported as an exception.
            await DisposeQuietlyAsync(transaction);
            await DisposeQuietlyAsync(main);
        }
    }

    private static async ValueTask DisposeQuietlyAsync(IAsyncDisposable? resource)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            await resource.DisposeAsync();
        }
#pragma warning disable CA1031 // Cleanup never replaces the outcome of the run.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static async Task<IdentityRebindOutcome> CommitAsync(
        NpgsqlTransaction transaction,
        string connection,
        string oldIssuer,
        string newIssuer,
        IdentityRebindContinuityResult continuity,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.CommitAsync(cancellationToken);
            return new IdentityRebindOutcome(IdentityRebindResult.Applied, Continuity: continuity);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // The server may have committed before the connection failed. Read the issuer on a fresh connection:
            // the target means it applied, the old value means it did not, and anything else is not guessed at.
            try
            {
                // The shared form of the lock, under the connection's own lock_timeout: it is granted only once the
                // original transaction has ended, so the read cannot see the issuer before an in-flight COMMIT lands.
                await using var reader = await OpenAsync(connection, CancellationToken.None);
                await using var check = await reader.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
                await ExecuteAsync(reader, check, "SELECT pg_catalog.pg_advisory_xact_lock_shared(@key)", CancellationToken.None,
                    ("key", IdentityAdvisoryLockKey.ForAuthorityRebind()));
                await using var command = new NpgsqlCommand(
                    "SELECT issuer FROM public.identity_authority WHERE id = 1", reader, check);
                var current = (string?)await command.ExecuteScalarAsync(CancellationToken.None);
                if (string.Equals(current, newIssuer, StringComparison.Ordinal))
                {
                    return new IdentityRebindOutcome(IdentityRebindResult.Applied, Continuity: continuity);
                }

                return string.Equals(current, oldIssuer, StringComparison.Ordinal)
                    ? new IdentityRebindOutcome(IdentityRebindResult.Refused, IdentityRebindRefusal.CommitFailed, continuity)
                    : new IdentityRebindOutcome(IdentityRebindResult.Unknown, Continuity: continuity);
            }
            catch (Exception reconcile) when (reconcile is NpgsqlException or TimeoutException or InvalidOperationException)
            {
                return new IdentityRebindOutcome(IdentityRebindResult.Unknown, Continuity: continuity);
            }
        }
    }

    /// <summary>Normalizes the connection string, or null when it is malformed or not the rebind role's.</summary>
    private static string? TryNormalize(string connectionString, TimeSpan lockWait)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!string.Equals(builder.Username, RoleName, StringComparison.Ordinal))
        {
            return null;
        }

        // A direct, never-pooled, never-multiplexed connection of its own, so the lock belongs to this transaction
        // and no other session shares it. The first statement is the lock, so the wait is bounded here by the
        // server and not by a statement; a transaction that idles longer than a proof could is the tool's fence.
        builder.Pooling = false;
        builder.Multiplexing = false;
        builder.Enlist = false;
        builder.IncludeErrorDetail = false;
        builder.LogParameters = false;
        builder.Timeout = 15;
        builder.CommandTimeout = 120;
        builder.ApplicationName = "learnstack-identity-rebind";

        // The main connection sits idle in its transaction while the new realm is read, so keep it alive through
        // middleboxes that cut a quiet connection.
        builder.TcpKeepAlive = true;
        builder.KeepAlive = 30;
        builder.Options = FormattableString.Invariant(
            $"-c lock_timeout={(int)Math.Max(1, lockWait.TotalMilliseconds)} -c statement_timeout=120000 -c idle_in_transaction_session_timeout=900000");
        return builder.ConnectionString;
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The role is exactly what the migration granted: itself, no attribute that escalates, no membership either way,
    /// no privilege on any other relation, and on the three it uses exactly the declared table and column grants.
    /// </summary>
    private static async Task<bool> HasExactPostureAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT (session_user::text = current_user::text AND current_user::text = @role) AS session_ok,
                   (r.rolcanlogin AND NOT r.rolinherit AND NOT r.rolbypassrls AND NOT r.rolsuper
                    AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication) AS attributes_ok,
                   NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.member = r.oid OR m.roleid = r.oid) AS no_membership,
                   NOT (has_schema_privilege(r.oid, 'public', 'CREATE')
                        OR has_database_privilege(r.oid, current_database(), 'CREATE')) AS no_creation,
                   (SELECT count(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
                        AND c.relname NOT IN ('identity_authority', 'users', @audit)
                        AND (has_any_column_privilege(r.oid, c.oid, 'SELECT,INSERT,UPDATE,REFERENCES')
                             OR has_table_privilege(r.oid, c.oid, 'DELETE,TRUNCATE,TRIGGER'))) AS stray_relations,
                   (SELECT count(*) FROM pg_catalog.pg_attribute a
                      JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
                      JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname = 'public' AND a.attnum > 0 AND NOT a.attisdropped AND (
                            (c.relname = 'users'
                             AND (has_column_privilege(r.oid, c.oid, a.attnum, 'INSERT,UPDATE,REFERENCES')
                                  OR has_column_privilege(r.oid, c.oid, a.attnum, 'SELECT')
                                     <> (a.attname IN ('authority_id', 'subject_key', 'provider_subject', 'status'))))
                         OR (c.relname = 'identity_authority'
                             AND (has_column_privilege(r.oid, c.oid, a.attnum, 'INSERT,REFERENCES')
                                  OR NOT has_column_privilege(r.oid, c.oid, a.attnum, 'SELECT')
                                  OR has_column_privilege(r.oid, c.oid, a.attnum, 'UPDATE') <> (a.attname = 'issuer')))
                         OR (c.relname = @audit
                             AND (has_column_privilege(r.oid, c.oid, a.attnum, 'SELECT,UPDATE,REFERENCES')
                                  OR NOT has_column_privilege(r.oid, c.oid, a.attnum, 'INSERT'))))) AS column_drift,
                   NOT (has_table_privilege(r.oid, 'public.users', 'INSERT,UPDATE,DELETE,TRUNCATE,TRIGGER,REFERENCES')
                        OR has_table_privilege(r.oid, 'public.identity_authority', 'INSERT,UPDATE,DELETE,TRUNCATE,TRIGGER,REFERENCES')
                        OR has_table_privilege(r.oid, to_regclass('public.' || quote_ident(@audit)),
                                               'SELECT,UPDATE,DELETE,TRUNCATE,TRIGGER,REFERENCES')) AS no_table_grants
            FROM pg_catalog.pg_roles r WHERE r.rolname = current_user
            """, connection);
        command.Parameters.AddWithValue("role", RoleName);
        command.Parameters.AddWithValue("audit", IdentityRebindAuditInstaller.Table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        bool Flag(string column) => reader.GetBoolean(reader.GetOrdinal(column));
        long Count(string column) => reader.GetInt64(reader.GetOrdinal(column));
        return Flag("session_ok") && Flag("attributes_ok") && Flag("no_membership") && Flag("no_creation")
            && Count("stray_relations") == 0 && Count("column_drift") == 0 && Flag("no_table_grants");
    }

    private static async Task<(Guid Authority, string Issuer)> ReadAuthorityAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT authority_id, issuer FROM public.identity_authority WHERE id = 1", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("No identity authority is installed.");
        }

        var result = (reader.GetGuid(0), reader.GetString(1));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The identity authority is not a singleton.");
        }

        return result;
    }

    /// <summary>
    /// The intent row, on its own connection and its own short transaction so it commits whether or not the main
    /// transaction does. It takes no lock the main transaction holds, because it reads nothing.
    /// </summary>
    private async Task<bool> TryWriteIntentAsync(
        string connection, PlatformAdmission admission, Guid authority, string oldIssuer, string newIssuer,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var intent = await OpenAsync(connection, cancellationToken);
            await using var transaction = await intent.BeginTransactionAsync(cancellationToken);
            await audit.WriteIdentityRebindIntentAsync(
                Draft(admission, admission.Operation.AttemptAuditSlug!, AuditOutcome.Indeterminate, authority,
                    oldIssuer, newIssuer, continuity: null),
                admission, intent, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception failure) when (failure is AuditWriteFailedException or NpgsqlException or TimeoutException)
        {
            return false;
        }
    }

    private static async Task<int> CountRuntimeSessionsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // pg_stat_activity shows another role's session as a row with its role and database even where its query
        // is hidden, which is all the freeze needs: a session of a runtime role, other than this one's own.
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM pg_catalog.pg_stat_activity
            WHERE datname = current_database() AND usename::text = ANY(@roles) AND pid <> pg_catalog.pg_backend_pid()
            """, connection, transaction);
        command.Parameters.AddWithValue("roles", RuntimeRoles);
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<NewRealmSnapshot?> ReadRealmAsync(
        INewRealmContinuitySource realm, string issuer, CancellationToken cancellationToken)
    {
        try
        {
            return await realm.ReadAsync(issuer, cancellationToken);
        }
        catch (NewRealmUnavailableException)
        {
            return null;
        }
    }

    private static async Task<List<IdentityRebindStoredUser>> ReadUsersAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid authority, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT subject_key, provider_subject, status FROM public.users WHERE authority_id = @authority",
            connection, transaction);
        command.Parameters.AddWithValue("authority", authority);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var users = new List<IdentityRebindStoredUser>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Enum.TryParse<UserStatus>(reader.GetString(2), ignoreCase: false, out var status) || !Enum.IsDefined(status))
            {
                throw new InvalidOperationException("A stored user has a status this rebind does not know.");
            }

            users.Add(new IdentityRebindStoredUser(
                IdentitySubjectKey.FromBytes(reader.GetFieldValue<byte[]>(0)),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                status));
        }

        return users;
    }

    private static async Task ChangeIssuerAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string oldIssuer, string newIssuer,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE public.identity_authority SET issuer = @new WHERE id = 1 AND issuer = @old", connection, transaction);
        command.Parameters.AddWithValue("new", newIssuer);
        command.Parameters.AddWithValue("old", oldIssuer);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The identity authority issuer did not change as read.");
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private AuditEntryDraft Draft(
        PlatformAdmission admission, string operation, AuditOutcome outcome, Guid authority, string oldIssuer,
        string newIssuer, IdentityRebindContinuityResult? continuity) => new()
        {
            Id = AuditEntryId.From(Guid.CreateVersion7()),
            TenantId = TenantId.PlatformSentinel,
            OrganizationId = null,
            ActorUserId = null,
            ActorEmail = null,
            ModuleName = "identity",
            Operation = operation,
            OperationType = OperationType.SecurityEvent,
            OperationClass = OperationClass.Must,
            EntityType = nameof(IdentityAuthority),
            EntityId = authority.ToString("D"),
            Outcome = outcome,
            ErrorKey = null,
            Reason = admission.Operation.ReasonSlug,
            BeforeState = "{\"issuer\":" + AuditJson.Quote(oldIssuer) + "}",
            AfterState = "{\"issuer\":" + AuditJson.Quote(newIssuer) + "}",
            Changes = null,
            CorrelationId = admission.Operator.CorrelationId,
            IpAddress = null,
            UserAgent = null,
            Timestamp = clock.UtcNow,
            Metadata = "{\"operator\":" + PlatformOperatorAttribution.From(admission).ToOperatorJson(admission.Ticket)
            + (continuity is null ? string.Empty : ",\"continuity\":" + ContinuityJson(continuity)) + "}",
        };

    private static string ContinuityJson(IdentityRebindContinuityResult counts) =>
        "{\"active\":" + counts.StoredActive + ",\"closing\":" + counts.StoredClosing + ",\"closed\":" + counts.StoredClosed
        + ",\"reported\":" + counts.Enumerated + ",\"unkeyable\":" + counts.Unkeyable
        + ",\"unbootstrapped\":" + counts.Unbootstrapped + "}";

    private static IdentityRebindOutcome Refused(IdentityRebindRefusal refusal) =>
        new(IdentityRebindResult.Refused, refusal);
}
