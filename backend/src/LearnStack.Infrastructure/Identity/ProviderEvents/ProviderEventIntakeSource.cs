using System.Data;
using System.Runtime.ExceptionServices;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Jobs;
using Npgsql;

namespace LearnStack.Infrastructure.Identity.ProviderEvents;

/// <summary>Two retained machine-only connectors. No connection, arbitrary statement or callback escapes.</summary>
public sealed partial class ProviderEventIntakeSource : ProviderEventRecipient.MintingSource, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly NpgsqlDataSource _source;
    private readonly HashSet<Owner> _owners = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _qualification;
    private Task? _disposal;
    private ProviderEventIntakeProfile? _profile;
    private bool _qualified;
    private bool _closed;

    public ProviderEventIntakeSource(string connectionString)
    {
        NpgsqlConnectionStringBuilder parsed;
        try { parsed = new(connectionString); }
        catch (Exception error) when (error is ArgumentException or FormatException)
        { throw new InvalidOperationException("Provider event intake connection is invalid."); }
        if (parsed.Username != "learnstack_identity_ingress")
            throw new InvalidOperationException("Provider event intake requires its separate machine role.");
        parsed.Pooling = true; parsed.MinPoolSize = 0; parsed.MaxPoolSize = 2;
        parsed.Multiplexing = false; parsed.Enlist = false; parsed.NoResetOnClose = false;
        parsed.IncludeErrorDetail = false; parsed.LogParameters = false; parsed.PersistSecurityInfo = false;
        parsed.Timeout = 5; parsed.CommandTimeout = 5; parsed.CancellationTimeout = 1000;
        parsed.Options = "-c statement_timeout=5000 -c lock_timeout=5000 -c idle_in_transaction_session_timeout=5000";
        try { _source = new NpgsqlDataSourceBuilder(parsed.ConnectionString).Build(); }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException)
        { throw new InvalidOperationException("Provider event intake source configuration is invalid."); }
    }

    public bool IsQualified { get { lock (_gate) return _qualified && !_closed; } }

    public Task QualifyAsync(ProviderEventIntakeProfile profile, DatabaseQualificationProof businessProof,
        CancellationToken cancellationToken = default)
    {
        JobApplicationSourceOwner.RefuseUnrelatedRead();
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(businessProof);
        businessProof.RequireActive();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_profile is not null && _profile != profile)
                throw new InvalidOperationException("Provider event intake cannot be retargeted.");
            _profile = profile;
            return _qualification ??= QualifyCoreAsync(profile, businessProof, cancellationToken);
        }
    }

    private async Task QualifyCoreAsync(ProviderEventIntakeProfile profile, DatabaseQualificationProof proof, CancellationToken token)
    {
        await ExecuteAsync(async (connection, transaction, deadline) =>
        {
            await using (var marker = new NpgsqlCommand("""
                SELECT EXISTS (SELECT FROM pg_catalog.pg_locks WHERE locktype='advisory' AND granted AND mode='ExclusiveLock'
                    AND database=(SELECT oid FROM pg_catalog.pg_database WHERE datname=current_database())
                    AND pid=@business_pid AND objsubid=2 AND classid::bigint=@business_first AND objid::bigint=@business_second)
                """, connection, transaction))
            {
                DatabaseQualificationProof.Bind(marker, proof);
                if (await marker.ExecuteScalarAsync(deadline).ConfigureAwait(false) is not true)
                    throw new InvalidOperationException("Provider intake does not reach the qualified business database.");
            }
            await using (var install = new NpgsqlCommand("""
                INSERT INTO public.identity_provider_event_scan(id,authority_id,realm_id,activation_lower,retention_seconds)
                VALUES(1,@authority,@realm,clock_timestamp()-make_interval(secs=>@retention),@retention)
                ON CONFLICT(id) DO NOTHING;
                SELECT authority_id=@authority AND realm_id=@realm FROM public.identity_provider_event_scan WHERE id=1
                """, connection, transaction))
            {
                BindProfile(install, profile);
                if (await install.ExecuteScalarAsync(deadline).ConfigureAwait(false) is not true)
                    throw new InvalidOperationException("Provider intake stored source does not match configuration.");
            }
            await using (var policy = new NpgsqlCommand(UpdatePolicySql, connection, transaction))
            {
                policy.Parameters.AddWithValue("retention", profile.RetentionSeconds);
                await policy.ExecuteNonQueryAsync(deadline).ConfigureAwait(false);
            }
            proof.RequireActive();
            return true;
        }, token, requireQualified: false).ConfigureAwait(false);
        proof.RequireActive();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _qualified = true;
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken token, bool requireQualified = true)
    {
        JobApplicationSourceOwner.RefuseUnrelatedRead();
        Owner owner;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (requireQualified && !_qualified) throw new InvalidOperationException("Provider intake is not qualified.");
            if (_owners.Count >= 2) throw new InvalidOperationException("Provider intake capacity is occupied.");
            owner = new Owner(); _owners.Add(owner);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        NpgsqlConnection? connection = null;
        ExceptionDispatchInfo? failure = null;
        T result = default!;
        try
        {
            connection = _source.CreateConnection();
            owner.Connection = connection;
            await connection.OpenAsync(deadline.Token).ConfigureAwait(false);
            await RequireRoleAsync(connection, deadline.Token).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, deadline.Token).ConfigureAwait(false);
            result = await operation(connection, transaction, deadline.Token).ConfigureAwait(false);
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            failure = ExceptionDispatchInfo.Capture(new InvalidOperationException("Provider intake database outcome is unavailable; reconcile stable identity."));
        }
        catch (Exception operationFailure) when (operationFailure is not OutOfMemoryException and not StackOverflowException)
        { failure = ExceptionDispatchInfo.Capture(operationFailure); }
        try
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            owner.Connection = null;
            lock (_gate) _owners.Remove(owner);
            owner.Completion.TrySetResult();
        }
        catch (Exception cleanup) when (cleanup is not OutOfMemoryException and not StackOverflowException)
        {
            // Keep the concrete slot and pool retained when actual cleanup is unconfirmed.
            lock (_gate) { _closed = true; _qualified = false; }
            var cleanupFailure = new InvalidOperationException("Provider intake cleanup is unconfirmed.");
            owner.Completion.TrySetException(cleanupFailure);
            failure ??= ExceptionDispatchInfo.Capture(cleanupFailure);
        }
        failure?.Throw();
        return result;
    }

    private static async Task RequireRoleAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT FROM pg_catalog.pg_roles r WHERE r.rolname='learnstack_identity_ingress'
                AND session_user=r.rolname AND current_user=r.rolname AND r.rolcanlogin AND NOT r.rolinherit AND NOT r.rolbypassrls
                AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolreplication
                AND NOT EXISTS (SELECT FROM pg_catalog.pg_roles other WHERE other.oid<>r.oid
                    AND (pg_has_role(r.oid,other.oid,'MEMBER') OR NOT other.rolsuper AND pg_has_role(other.oid,r.oid,'MEMBER')))
                AND NOT EXISTS (SELECT FROM pg_catalog.pg_class c WHERE c.relowner=r.oid)
                AND NOT EXISTS (SELECT FROM pg_catalog.pg_proc p WHERE p.proowner=r.oid)
                AND NOT has_schema_privilege(r.oid,'public','CREATE')
                AND has_function_privilege(r.oid,'public.fn_identity_provider_event_accept(uuid,text,uuid,timestamptz,text,bytea)','EXECUTE')
                AND has_function_privilege(r.oid,'public.fn_identity_provider_event_claim(uuid)','EXECUTE')
                AND has_function_privilege(r.oid,'public.fn_identity_provider_event_retire()','EXECUTE')
                AND NOT EXISTS (SELECT FROM pg_catalog.pg_proc p WHERE p.pronamespace='public'::regnamespace
                    AND p.prosecdef AND has_function_privilege(r.oid,p.oid,'EXECUTE')
                    AND p.proname NOT IN ('fn_identity_provider_event_accept','fn_identity_provider_event_claim','fn_identity_provider_event_retire'))
                AND NOT EXISTS (SELECT FROM pg_catalog.pg_class c WHERE c.relnamespace='public'::regnamespace
                    AND c.relkind IN ('r','p','v','m','f')
                    AND ((c.relname NOT IN ('identity_provider_event_scan','identity_provider_event_problems','identity_provider_event_receipts')
                        AND (has_table_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                            OR has_any_column_privilege(r.oid,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')))
                        OR has_table_privilege(r.oid,c.oid,'DELETE,TRUNCATE,REFERENCES,TRIGGER')
                        OR c.relname='identity_provider_event_receipts' AND has_any_column_privilege(r.oid,c.oid,'INSERT,UPDATE,REFERENCES')))
                AND NOT EXISTS (SELECT FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid=a.attrelid
                    WHERE c.relnamespace='public'::regnamespace AND a.attnum>0 AND NOT a.attisdropped
                        AND has_column_privilege(r.oid,c.oid,a.attnum,'UPDATE') AND NOT
                        (c.relname='identity_provider_event_scan' AND a.attname IN
                            ('retention_seconds','cursor_from','cursor_to','cursor_offset','last_attempted_at','last_traversed_at',
                                'last_completed_at','historical_gap','earliest_gap_at','recovery_anchor_at')
                        OR c.relname='identity_provider_event_problems' AND a.attname IN
                            ('last_seen_at','next_retry_at','attempts','resolved_at','irrecoverable'))))
            """, connection);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not true)
            throw new InvalidOperationException("Provider intake role attributes, ownership or grants are invalid.");
    }

    private static void BindProfile(NpgsqlCommand command, ProviderEventIntakeProfile profile)
    {
        command.Parameters.AddWithValue("authority", profile.AuthorityId.Value);
        command.Parameters.AddWithValue("realm", profile.RealmId);
        command.Parameters.AddWithValue("retention", profile.RetentionSeconds);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _closed = true; _qualified = false;
            return new(_disposal ??= DisposeCoreAsync(_owners.ToArray()));
        }
    }

    private async Task DisposeCoreAsync(Owner[] owners)
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(owners.Select(owner => owner.Completion.Task)).ConfigureAwait(false);
        await _source.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed class Owner
    {
        // Keep the concrete failed connector reachable until confirmed cleanup;
        // retaining only its slot or completion task would lose that ownership.
        internal NpgsqlConnection? Connection { get; set; }
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Installed authority and exact realm, without credentials or person data.</summary>
public sealed record ProviderEventIntakeProfile
{
    public ProviderEventIntakeProfile(IdentityAuthorityId authorityId, string realmId, int retentionSeconds)
    {
        if (!StronglyTypedId.IsAssigned(authorityId) || string.IsNullOrWhiteSpace(realmId) || realmId.Length > 255
            || retentionSeconds is < 604800 or > 2592000) throw new ArgumentException("Provider intake profile is invalid.");
        AuthorityId = authorityId; RealmId = realmId; RetentionSeconds = retentionSeconds;
    }
    public IdentityAuthorityId AuthorityId { get; }
    public string RealmId { get; }
    public int RetentionSeconds { get; }
    public override string ToString() => nameof(ProviderEventIntakeProfile);
}
