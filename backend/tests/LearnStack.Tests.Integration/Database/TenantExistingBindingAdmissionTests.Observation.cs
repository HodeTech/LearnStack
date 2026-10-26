using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class TenantExistingBindingAdmissionTests
{
    private static void ObserveBindings(IServiceCollection services, BindingObservation observation)
    {
        services.AddSingleton(observation);
        services.AddScoped<UserBindingStore>();
        services.Replace(ServiceDescriptor.Scoped<IUserBindingStore, ObservedBindingStore>());
        services.Replace(ServiceDescriptor.Scoped<IIdentityBindingReader, ObservedBindingReader>());
    }

    private sealed class BindingObservation
    {
        private int _lookups;
        private int _closed;
        internal int Lookups => Volatile.Read(ref _lookups);
        internal int ClosedOwners => Volatile.Read(ref _closed);
        internal ConcurrentQueue<int> BackendIds { get; } = new();
        internal Func<CancellationToken, Task>? BeforeRead { get; set; }
        internal void Enter(int backend)
        {
            Interlocked.Increment(ref _lookups);
            BackendIds.Enqueue(backend);
        }
        internal void Closed() => Interlocked.Increment(ref _closed);
    }

    /// <summary>Observes the exact common owner, then delegates every read to the production store.</summary>
    private sealed class ObservedBindingStore(UserBindingStore inner, IGlobalIdentityUnitOfWork unit,
        ITenantContextAccessor tenant, BindingObservation observation) : IUserBindingStore
    {
        public async Task<User?> FindExactAsync(GlobalIdentitySubject subject, CancellationToken cancellationToken = default)
        {
            unit.Mode.Should().Be(TransactionMode.ReadOnly);
            unit.IsGlobalIdentityTransaction.Should().BeTrue();
            (tenant.Current?.IsResolved ?? false).Should().BeFalse();
            unit.GetGlobalIdentityAnnouncement(unit.Transaction)!.Descriptor.Subject.Should().BeSameAs(subject);
            await using (var command = new NpgsqlCommand("""
                SELECT current_user,rolsuper,rolbypassrls,current_setting('transaction_read_only'),
                    current_setting('app.identity_authority_id',true),current_setting('app.identity_subject_key',true),
                    COALESCE(current_setting('app.tenant_id',true),''),COALESCE(current_setting('app.organization_id',true),''),
                    pg_backend_pid()
                FROM pg_roles WHERE rolname=current_user
                """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction))
            await using (var proof = await command.ExecuteReaderAsync(cancellationToken))
            {
                (await proof.ReadAsync(cancellationToken)).Should().BeTrue();
                proof.GetString(0).Should().Be("learnstack_app");
                proof.GetBoolean(1).Should().BeFalse();
                proof.GetBoolean(2).Should().BeFalse();
                proof.GetString(3).Should().Be("on");
                (proof.GetString(4) == subject.AuthorityId.Value.ToString("D")).Should().BeTrue();
                (proof.GetString(5) == subject.SubjectKey.Hex).Should().BeTrue();
                proof.GetString(6).Should().BeEmpty();
                proof.GetString(7).Should().BeEmpty();
                observation.Enter(proof.GetInt32(8));
            }
            if (observation.BeforeRead is { } before) await before(cancellationToken);
            return await inner.FindExactAsync(subject, cancellationToken);
        }

        public Task AddAsync(User user, CancellationToken cancellationToken = default) => inner.AddAsync(user, cancellationToken);
    }

    /// <summary>The production reader has finished cleanup before its result reaches the API binding stage.</summary>
    private sealed class ObservedBindingReader(GlobalIdentityBindingService inner, IGlobalIdentityUnitOfWork unit,
        IGlobalIdentityScopeAccessor identity, ITenantContextAccessor tenant, IAuditStateCapture capture,
        BindingObservation observation) : IIdentityBindingReader
    {
        public async Task<IdentityBindingResult> ResolveExistingAsync(GlobalIdentitySubject subject,
            UserId? claimedActor = null, CancellationToken cancellationToken = default)
        {
            try { return await inner.ResolveExistingAsync(subject, claimedActor, cancellationToken); }
            finally
            {
                unit.HasActiveTransaction.Should().BeFalse();
                unit.IsGlobalIdentityTransaction.Should().BeFalse();
                identity.Current.Should().BeNull();
                (tenant.Current?.IsResolved ?? false).Should().BeFalse();
                capture.Intents.Should().BeEmpty();
                capture.Changes.Should().BeEmpty();
                capture.HasOpenFrame.Should().BeFalse();
                observation.Closed();
            }
        }
    }

    private sealed class BindingLogSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _entries = new();
        public void Emit(LogEvent logEvent) => _entries.Enqueue(logEvent);
        internal void BeginFaultWindow() => _entries.Clear();
        internal void AssertUnavailable(params string[] privateValues)
        {
            var closed = _entries.Where(entry => entry.MessageTemplate.Text == "Tenant authentication stage {Stage} outcome {Reason}."
                && entry.Properties.TryGetValue("Reason", out var reason)
                && reason is ScalarValue { Value: "binding_store_unavailable" }).ToArray();
            closed.Should().ContainSingle();
            closed[0].Exception.Should().BeNull();
            closed[0].Properties["Stage"].Should().BeOfType<ScalarValue>().Which.Value.Should().Be("binding");
            using var serialized = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            var formatter = new JsonFormatter(renderMessage: true, formatProvider: System.Globalization.CultureInfo.InvariantCulture);
            foreach (var entry in _entries) formatter.Format(entry, serialized);
            var exported = serialized.ToString();
            foreach (var privateValue in privateValues)
                exported.Contains(privateValue, StringComparison.Ordinal).Should().BeFalse(
                    "the fault-window exported events, including structured properties and exceptions, must contain no provider subject, bearer or application actor");
        }
    }

    private static async Task<(long Users, long Audits)> CountsAsync(IdentityPublicReadFixture database)
    {
        var subject = new GlobalIdentitySubject(database.Registration.AuthorityId, database.Registration.Issuer, Subject);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await AnnounceAsync(owner, transaction, subject);
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM public.users),
                (SELECT count(*) FROM public.audit_log WHERE operation='identity.user.create'
                    AND metadata->>'identity_subject_key'=@key)
            """, owner, transaction);
        command.Parameters.AddWithValue("key", subject.SubjectKey.Hex);
        await using var rows = await command.ExecuteReaderAsync();
        (await rows.ReadAsync()).Should().BeTrue();
        return (rows.GetInt64(0), rows.GetInt64(1));
    }

    private static async Task AnnounceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, GlobalIdentitySubject subject)
    {
        await using var announce = new NpgsqlCommand("""
            SELECT set_config('app.identity_authority_id',@authority,true),set_config('app.identity_subject_key',@key,true)
            """, connection, transaction);
        announce.Parameters.AddWithValue("authority", subject.AuthorityId.Value.ToString("D"));
        announce.Parameters.AddWithValue("key", subject.SubjectKey.Hex);
        await announce.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteOwnerAsync(IdentityPublicReadFixture database, string sql)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand(sql, owner);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> SetBindingStateAsync(IdentityPublicReadFixture database, string state,
        string? subjectDigestDefinition = null)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        // The production schema admits no closure writer. This isolated database's
        // fixture temporarily enables owner-only setup and restores its trigger
        // and policy before COMMIT. Collision alone removes the exact digest CHECK
        // until the caller's finally restores the retained subject and definition.
        await using (var setup = new NpgsqlCommand("""
            CREATE POLICY fixture_binding_update ON public.users FOR UPDATE TO learnstack_migration USING(true) WITH CHECK(true);
            ALTER TABLE public.users DISABLE TRIGGER tg_users_no_update;
            """, owner, transaction)) await setup.ExecuteNonQueryAsync();
        if (state == "Collision")
        {
            await AssertDigestRejectsMismatchAsync(owner, transaction);
            await using var definition = new NpgsqlCommand("""
                SELECT pg_get_constraintdef(oid) FROM pg_constraint
                WHERE conrelid='public.users'::regclass AND conname='ck_users_subject_digest' AND convalidated
                """, owner, transaction);
            subjectDigestDefinition = (string?)await definition.ExecuteScalarAsync();
            subjectDigestDefinition.Should().NotBeNullOrWhiteSpace();
            await using var remove = new NpgsqlCommand("ALTER TABLE public.users DROP CONSTRAINT ck_users_subject_digest", owner, transaction);
            await remove.ExecuteNonQueryAsync();
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE public.users SET status=@status,provider_subject=@subject WHERE id=@actor
            """, owner, transaction))
        {
            update.Parameters.AddWithValue("status", state == "Collision" ? "Active" : state);
            update.Parameters.AddWithValue("subject", state == "Closed" ? DBNull.Value
                : state == "Collision" ? "different-retained-subject-canary" : Subject);
            update.Parameters.AddWithValue("actor", Actor.Value);
            (await update.ExecuteNonQueryAsync()).Should().Be(1);
        }
        if (state == "Active" && subjectDigestDefinition is not null)
        {
            // The definition came from this database's exact named catalog row,
            // never caller input or a copied canonical CHECK expression.
            await using var restoreDigest = new NpgsqlCommand("ALTER TABLE public.users ADD CONSTRAINT ck_users_subject_digest "
                + subjectDigestDefinition, owner, transaction);
            await restoreDigest.ExecuteNonQueryAsync();
            await AssertDigestRejectsMismatchAsync(owner, transaction);
        }
        await using (var restore = new NpgsqlCommand("""
            ALTER TABLE public.users ENABLE TRIGGER tg_users_no_update;
            DROP POLICY fixture_binding_update ON public.users;
            """, owner, transaction)) await restore.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return subjectDigestDefinition;
    }

    private static async Task AssertDigestRejectsMismatchAsync(NpgsqlConnection owner, NpgsqlTransaction transaction)
    {
        await using (var savepoint = new NpgsqlCommand("SAVEPOINT fixture_digest_control", owner, transaction))
            await savepoint.ExecuteNonQueryAsync();
        await using var invalid = new NpgsqlCommand("UPDATE public.users SET provider_subject=@mismatch WHERE id=@actor", owner, transaction);
        invalid.Parameters.AddWithValue("mismatch", "different-retained-subject-canary");
        invalid.Parameters.AddWithValue("actor", Actor.Value);
        var failure = await FluentActions.Awaiting(async () => { await invalid.ExecuteNonQueryAsync(); })
            .Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        failure.Which.ConstraintName.Should().Be("ck_users_subject_digest");
        await using var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT fixture_digest_control", owner, transaction);
        await rollback.ExecuteNonQueryAsync();
    }

    private static async Task AssertBindingGuardsRestoredAsync(IdentityPublicReadFixture database, string? subjectDigestDefinition)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var check = new NpgsqlCommand("""
            SELECT (SELECT tgenabled::text FROM pg_trigger WHERE tgrelid='public.users'::regclass AND tgname='tg_users_no_update'),
                NOT EXISTS(SELECT 1 FROM pg_policy WHERE polrelid='public.users'::regclass AND polname='fixture_binding_update'),
                NOT has_table_privilege('learnstack_app','public.users','UPDATE'),
                (SELECT convalidated FROM pg_constraint WHERE conrelid='public.users'::regclass AND conname='ck_users_subject_digest'),
                (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='public.users'::regclass AND conname='ck_users_subject_digest')
            """, owner);
        await using var row = await check.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetString(0).Should().Be("O");
        row.GetBoolean(1).Should().BeTrue();
        row.GetBoolean(2).Should().BeTrue();
        row.GetBoolean(3).Should().BeTrue();
        if (subjectDigestDefinition is not null) row.GetString(4).Should().Be(subjectDigestDefinition);
    }

    /// <summary>Copies the actual EF command solely for a later parameterized EXPLAIN; never emits SQL or values.</summary>
    private sealed class BindingCommandObservation : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable _all;
        private readonly List<IDisposable> _subscriptions = [];
        private int _commands;
        internal int Commands => Volatile.Read(ref _commands);
        internal string? Sql { get; private set; }
        internal NpgsqlParameter[] Parameters { get; private set; } = [];
        internal BindingCommandObservation() => _all = DiagnosticListener.AllListeners.Subscribe(this);
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore")
                _subscriptions.Add(listener.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key != "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuting"
                || value.Value is not CommandEventData { Context: IdentityDbContext } data) return;
            Interlocked.Increment(ref _commands);
            lock (_subscriptions)
            {
                Sql ??= data.Command.CommandText;
                if (Parameters.Length == 0)
                    Parameters = data.Command.Parameters.Cast<NpgsqlParameter>().Select(parameter =>
                        new NpgsqlParameter(parameter.ParameterName, parameter.NpgsqlDbType) { Value = parameter.Value }).ToArray();
            }
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose()
        {
            _all.Dispose();
            foreach (var subscription in _subscriptions) subscription.Dispose();
        }
    }
}
