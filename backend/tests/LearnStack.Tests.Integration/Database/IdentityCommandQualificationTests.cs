using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Small local command-path observations, not production capacity or a historical baseline.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class IdentityCommandQualificationTests(SchemaFixture schema, ITestOutputHelper output)
{
    private const int PoolCapacity = 4;
    private const int WriteBudgetMilliseconds = 500;
    private static readonly IdentityAuthorityRegistration Authority = new(
        IdentityAuthorityId.From(Guid.Parse("01930000-0000-7000-8000-000000009901")),
        "https://command-qualification.invalid/realms/learnstack");

    [Fact]
    public async Task Actual_seed_commands_measure_local_observation_cost_and_pool_occupancy_with_a_real_membership_wait()
    {
        var counterfactual = await MeasureAsync(removeObservationCost: true);
        var guarded = await MeasureAsync(removeObservationCost: false);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            CurrentCodeObservationCostCounterfactual = counterfactual,
            ProductionGuards = guarded,
            WarmP95DeltaMilliseconds = guarded.WarmP95Milliseconds - counterfactual.WarmP95Milliseconds,
            SqlCommandCountDelta = guarded.Samples.Sum(sample => sample.SqlCommands)
                - counterfactual.Samples.Sum(sample => sample.SqlCommands),
            WriteBudgetMilliseconds,
            ColdDefinition = "first_invocation_per_command_type_not_process_or_pool_cold",
            WarmDefinition = "subsequent_invocations_per_command_type",
            SqlCostDefinition = "client_command_spans_include_server_trigger_time_not_internal_statement_counts",
            NoHistoricalBaselineClaim = true,
            NoProductionCapacityClaim = true,
        }));
    }

    private async Task<CommandReport> MeasureAsync(bool removeObservationCost)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await IdentityAuthorityDeploymentInstaller.InstallAsync(owner, Authority);
        var original = await FunctionsAsync(database);
        original.Definers.Length.Should().Be(5);
        // This twin retains every current coordinator, transaction, lock acquisition,
        // trigger and write. Only the heldness observation body changes. It is not a
        // security qualification or a reconstruction of any previously shipped code.
        try
        {
            if (removeObservationCost)
            {
                var replacement = original.Definition.Replace(original.Source, "SELECT TRUE;", StringComparison.Ordinal);
                (replacement != original.Definition).Should().BeTrue();
                await DeploymentAsync(database, replacement);
                var changed = await FunctionsAsync(database);
                (changed.Source.Trim() == "SELECT TRUE;").Should().BeTrue("the counterfactual has the fixed body only");
                (changed.Metadata == original.Metadata).Should().BeTrue("only the observation body changes");
                changed.Definers.SequenceEqual(original.Definers, StringComparer.Ordinal).Should().BeTrue();
            }
            var connectionString = new NpgsqlConnectionStringBuilder(database.AppConnectionString)
            {
                Pooling = true,
                MaxPoolSize = PoolCapacity,
                MinPoolSize = 0,
                ApplicationName = "identity-command-qualification",
            }.ConnectionString;
            await using var source = ApplicationDataSource.Build(connectionString);
            await new SeedRunner(context => SeedComposition.Build(source, context, NullLoggerFactory.Instance),
                NullLogger<SeedRunner>.Instance).RunAsync(CancellationToken.None);
            var witness = await WitnessAsync();
            var admission = new FixedSeedIdentityFoundationAdmission(witness);
            // Npgsql's default pool name is its redacted connection string. Match it
            // privately; neither this value nor any received raw tag is retained in output.
            using var capture = new CommandCapture(source.ConnectionString);
            using var logs = LoggerFactory.Create(builder => builder.AddProvider(capture));
            var runner = new SeedIdentityFoundationRunner(context => SeedComposition.Build(source, context, logs,
                identityAuthority: Authority, seedAdmission: admission), admission);
            await RunWithObservedWaitAsync(database, source, runner, capture);
            capture.ObservePool();
            var samples = capture.Samples();
            samples.Length.Should().Be(24, "sixteen Role, four Membership and four profile acts really committed");
            samples.Count(sample => sample.Command == nameof(InitializeSeedRoleCommand)).Should().Be(16);
            samples.Count(sample => sample.Command == nameof(CreateFirstSeedAdminMembershipCommand)).Should().Be(2);
            samples.Count(sample => sample.Command == nameof(CreateSeedMembershipCommand)).Should().Be(2);
            samples.Count(sample => sample.Command == nameof(SetSeedMembershipProfileCommand)).Should().Be(4);
            samples.Count(sample => sample.Cold).Should().Be(4);
            samples.Should().OnlyContain(sample => sample.ElapsedMilliseconds >= 0 && sample.SqlCommands > 0
                && double.IsFinite(sample.SqlMilliseconds) && sample.SqlMilliseconds >= 0);
            capture.MaximumBusy.Should().BeInRange(2, PoolCapacity,
                "the same measured pool held the blocker and the real waiting command");
            capture.FinalBusy.Should().Be(0, "all actual command and blocker leases returned after COMMIT");
            capture.PoolObservations.Should().BeGreaterThan(0);
            capture.MaximumAcquiredUserWaitMilliseconds.Should().BeGreaterThan(0);
            await AssertCommittedGraphsAsync(database);
            var warmP95 = Percentile95(samples.Where(sample => !sample.Cold).Select(sample => (double)sample.ElapsedMilliseconds));
            return new(samples, samples.Count(sample => sample.Cold), samples.Count(sample => !sample.Cold),
                warmP95, warmP95 < WriteBudgetMilliseconds,
                Percentile95(samples.Where(sample => sample.Cold).Select(sample => (double)sample.ElapsedMilliseconds)),
                Percentile95(samples.Select(sample => sample.SqlMilliseconds)), capture.MaximumBusy,
                capture.FinalBusy, capture.PoolObservations, capture.MaximumAcquiredUserWaitMilliseconds, PoolCapacity);
        }
        finally
        {
            if (removeObservationCost) await DeploymentAsync(database, original.Definition);
            var restored = await FunctionsAsync(database);
            (restored.Definition == original.Definition && restored.Source == original.Source
                && restored.Metadata == original.Metadata).Should().BeTrue("the exact observation function is restored");
            restored.Definers.SequenceEqual(original.Definers, StringComparer.Ordinal).Should().BeTrue(
                "all five SECURITY DEFINER definitions, owners and ACLs remain exact");
        }
        // await using drops each isolated database, including on failed qualification.
    }

    private static async Task RunWithObservedWaitAsync(DisposableSchemaDatabase database, NpgsqlDataSource source,
        SeedIdentityFoundationRunner runner, CommandCapture capture)
    {
        var first = SeedData.IdentityMemberships.First(member => member.IsFirstAdmin);
        var key = IdentityAdvisoryLockKey.ForUser(first.Identity.UserId);
        await using var blocker = await source.OpenConnectionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(blocker);
        await using var transaction = await blocker.BeginTransactionAsync();
        await IdentityMembershipFixture.ExecuteAsync(blocker, transaction,
            "SELECT pg_catalog.pg_advisory_xact_lock(@key)", ("key", key));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var activity = capture.Enter();
        var running = runner.RunAsync(budget.Token);
        try
        {
            await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
            await ObserveWaitAsync(observer, blocker.ProcessID, key, running, budget.Token);
            // The delay follows an observed server wait; it does not stand in for proof
            // that the actual coordinator's User acquisition reached PostgreSQL.
            await Task.Delay(50, budget.Token);
        }
        finally
        {
            await transaction.CommitAsync(CancellationToken.None);
            await running.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private static async Task ObserveWaitAsync(NpgsqlConnection observer, int blocker, long key,
        Task running, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (running.IsCompleted)
            {
                await running;
                throw new InvalidOperationException("The command completed before the required competing advisory wait.");
            }
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_locks held
                    JOIN pg_catalog.pg_stat_activity activity ON activity.pid=held.pid
                    WHERE held.pid<>@blocker AND held.locktype='advisory' AND NOT held.granted
                      AND held.database=(SELECT oid FROM pg_catalog.pg_database WHERE datname=current_database())
                      AND held.objsubid=1 AND held.mode='ExclusiveLock'
                      AND ((held.classid::bigint << 32) | held.objid::bigint)=@key
                      AND activity.usename=current_user
                      AND activity.wait_event_type='Lock' AND activity.wait_event='advisory')
                """, observer) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("blocker", blocker);
            command.Parameters.AddWithValue("key", key);
            if (await command.ExecuteScalarAsync(cancellationToken) is true) return;
            await Task.Delay(10, cancellationToken);
        }
        throw new InvalidOperationException("The actual command never entered the required PostgreSQL advisory wait.");
    }

    private static async Task<SeedProviderWitness> WitnessAsync()
    {
        var document = JsonSerializer.Serialize(new
        {
            version = 1,
            issuer = Authority.Issuer,
            users = SeedData.IdentityUsers.Select((person, index) => new
            { actorId = person.UserId.Value, opaqueSub = $"qualification-provider-fact-{index}" }),
        });
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(document));
        return await SeedProviderWitness.ReadAsync(input, Authority);
    }

    private static async Task AssertCommittedGraphsAsync(DisposableSchemaDatabase database)
    {
        foreach (var tenant in SeedData.All)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value);
            (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction,
                "SELECT count(*) FROM public.roles WHERE row_version=0")).Should().Be(8);
        }
        foreach (var member in SeedData.IdentityMemberships)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, member.Identity.Tenant.TenantId.Value,
                member.OrganizationId?.Value);
            (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction, """
                SELECT count(*) FROM public.memberships member
                JOIN public.membership_profiles profile ON profile.tenant_id=member.tenant_id AND profile.membership_id=member.id
                JOIN public.membership_role_assignments assignment ON assignment.tenant_id=member.tenant_id AND assignment.membership_id=member.id
                WHERE member.id=@member AND member.user_id=@user AND member.row_version=1 AND member.status='Active'
                  AND member.deleted_at IS NULL AND assignment.role_id=@role
                  AND profile.display_name=@display AND profile.preferred_locale=@locale
                """, ("member", member.MembershipId), ("user", member.Identity.UserId.Value),
                ("role", member.Role.RoleId), ("display", member.DisplayName), ("locale", member.PreferredLocale))).Should().Be(1);
        }
        await using var audit = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.PlatformConnectionString);
        (await IdentityMembershipFixture.ScalarAsync<long>(audit, null, """
            SELECT count(*) FROM public.audit_log WHERE module='identity' AND operation<>'identity.user.create' AND outcome='success'
            """)).Should().Be(24, "each observed contextual command has a durable central MUST row");
    }

    private static async Task<FunctionSnapshot> FunctionsAsync(DisposableSchemaDatabase database)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT pg_get_functiondef(oid),prosrc,
                json_build_array(proowner,prolang,provolatile,proisstrict,prosecdef,proconfig,proacl)::text
            FROM pg_catalog.pg_proc WHERE oid='public.fn_identity_lock_held(text,uuid,boolean)'::regprocedure
            """, owner);
        string definition, source, metadata;
        await using (var row = await command.ExecuteReaderAsync())
        {
            (await row.ReadAsync()).Should().BeTrue();
            definition = row.GetString(0); source = row.GetString(1); metadata = row.GetString(2);
        }
        command.CommandText = """
            SELECT pg_get_functiondef(proc.oid),json_build_array(proc.proowner,proc.proacl)::text
            FROM pg_catalog.pg_proc proc JOIN pg_catalog.pg_namespace ns ON ns.oid=proc.pronamespace
            WHERE ns.nspname='public' AND proc.proname LIKE 'fn_identity_%' AND proc.prosecdef
            ORDER BY proc.proname,pg_get_function_identity_arguments(proc.oid)
            """;
        var definers = new List<string>();
        await using var rows = await command.ExecuteReaderAsync();
        while (await rows.ReadAsync()) definers.Add(rows.GetString(0) + rows.GetString(1));
        return new(definition, source, metadata, [.. definers]);
    }

    private static async Task DeploymentAsync(DisposableSchemaDatabase database, string sql)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await IdentityMembershipFixture.ExecuteAsync(owner, null, sql);
    }

    private static double Percentile95(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
    }

    private sealed record FunctionSnapshot(string Definition, string Source, string Metadata, string[] Definers)
    {
        public override string ToString() => "Private identity function census";
    }

    private sealed record CommandSample(string Command, bool Cold, long ElapsedMilliseconds,
        int SqlCommands, double SqlMilliseconds);

    private sealed record CommandReport(CommandSample[] Samples, int ColdSamples, int WarmSamples, double WarmP95Milliseconds,
        bool WarmWithin500Milliseconds, double ColdP95Milliseconds, double SqlP95Milliseconds,
        int MaximumBusyConnections, int FinalBusyConnections, int PoolObservations,
        double MaximumAcquiredUserWaitMilliseconds, int PoolCapacity);

    /// <summary>Allowlisted numeric observations only; no formatter, scope, SQL text or raw tag capture.</summary>
    private sealed class CommandCapture : ILoggerProvider
    {
        private const string RootProperty = "IdentityCommandQualificationCapture";
        private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
        {
            nameof(InitializeSeedRoleCommand), nameof(CreateFirstSeedAdminMembershipCommand),
            nameof(CreateSeedMembershipCommand), nameof(SetSeedMembershipProfileCommand),
        };
        private readonly object _gate = new();
        private readonly string _poolName;
        private readonly ActivityListener _activities = new();
        private readonly MeterListener _metrics = new();
        private readonly Dictionary<ActivitySpanId, PendingCommand> _pending = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly List<CommandSample> _samples = [];
        private int _disposed;

        internal CommandCapture(string poolName)
        {
            _poolName = poolName;
            _metrics.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Npgsql" && instrument.Name == "db.client.connection.count"
                    || instrument.Meter.Name == IdentityLockObservation.MeterName
                        && instrument.Name == "learnstack_identity_lock_wait_seconds")
                    listener.EnableMeasurementEvents(instrument);
            };
            _metrics.SetMeasurementEventCallback<int>((_, value, tags, _) =>
            {
                string? pool = null, state = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "db.client.connection.pool.name") pool = tag.Value as string;
                    if (tag.Key == "db.client.connection.state") state = tag.Value as string;
                }
                if (pool != _poolName || state != "used") return;
                lock (_gate)
                {
                    PoolObservations++;
                    FinalBusy = value;
                    MaximumBusy = Math.Max(MaximumBusy, value);
                }
            });
            _metrics.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            {
                if (!WithinRoot(Activity.Current)) return;
                string? kind = null, outcome = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "lock_kind") kind = tag.Value as string;
                    if (tag.Key == "outcome") outcome = tag.Value as string;
                }
                if (kind == "user" && outcome == "acquired")
                    lock (_gate) MaximumAcquiredUserWaitMilliseconds = Math.Max(MaximumAcquiredUserWaitMilliseconds, value * 1000);
            });
            _metrics.Start();
            _activities.ShouldListenTo = source => source.Name is "Npgsql" or "learnstack.mediatr";
            // Propagation-only sampling keeps Npgsql from materializing query/connection
            // tags for this observer. Its duration and parent chain are sufficient.
            _activities.Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData;
            _activities.ActivityStarted = Started;
            _activities.ActivityStopped = Stopped;
            ActivitySource.AddActivityListener(_activities);
        }

        internal int MaximumBusy { get; private set; }
        internal int FinalBusy { get; private set; }
        internal int PoolObservations { get; private set; }
        internal double MaximumAcquiredUserWaitMilliseconds { get; private set; }
        internal Activity Enter()
        {
            var root = new Activity("identity.command.qualification");
            root.SetCustomProperty(RootProperty, this);
            return root.Start();
        }
        internal void ObservePool() => _metrics.RecordObservableInstruments();
        internal CommandSample[] Samples() { lock (_gate) return [.. _samples]; }
        public ILogger CreateLogger(string categoryName) => new NumericCommandLogger(this,
            categoryName.StartsWith("LearnStack.Application.Pipeline.LoggingBehavior", StringComparison.Ordinal));

        private bool WithinRoot(Activity? activity)
        {
            for (var cursor = activity; cursor is not null; cursor = cursor.Parent)
                if (ReferenceEquals(cursor.GetCustomProperty(RootProperty), this)) return true;
            return false;
        }

        private void Started(Activity activity)
        {
            if (!WithinRoot(activity)) return;
            if (activity.Source.Name == "learnstack.mediatr")
            {
                var name = activity.OperationName;
                if (!name.StartsWith("mediatr.", StringComparison.Ordinal) || !Commands.Contains(name[8..])) return;
                lock (_gate) _pending.Add(activity.SpanId, new(name[8..]));
            }
            else if (activity.Source.Name == "Npgsql")
            {
                lock (_gate)
                    if (ParentCommand(activity) is { } command) command.SqlCommands++;
                ObservePool();
            }
        }

        private void Stopped(Activity activity)
        {
            if (!WithinRoot(activity)) return;
            lock (_gate)
            {
                if (activity.Source.Name == "Npgsql" && ParentCommand(activity) is { } command)
                    command.SqlMilliseconds += activity.Duration.TotalMilliseconds;
                if (activity.Source.Name == "learnstack.mediatr") _pending.Remove(activity.SpanId);
            }
        }

        private PendingCommand? ParentCommand(Activity activity)
        {
            for (var cursor = activity.Parent; cursor is not null; cursor = cursor.Parent)
                if (_pending.TryGetValue(cursor.SpanId, out var command)) return command;
            return null;
        }

        private void Success<TState>(TState state)
        {
            if (!WithinRoot(Activity.Current) || state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            string? name = null;
            long? elapsed = null;
            foreach (var field in fields)
            {
                if (field.Key == "RequestName") name = field.Value as string;
                if (field.Key == "ElapsedMilliseconds" && field.Value is long value) elapsed = value;
            }
            if (name is null || elapsed is null || !Commands.Contains(name) || Activity.Current is not { } current) return;
            lock (_gate)
            {
                if (!_pending.TryGetValue(current.SpanId, out var command) || command.Name != name) return;
                _samples.Add(new(name, _seen.Add(name), elapsed.Value, command.SqlCommands, command.SqlMilliseconds));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _activities.Dispose();
            _metrics.Dispose();
        }

        private sealed class PendingCommand(string name)
        {
            internal string Name { get; } = name;
            internal int SqlCommands { get; set; }
            internal double SqlMilliseconds { get; set; }
        }

        private sealed class NumericCommandLogger(CommandCapture capture, bool approvedCategory) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => approvedCategory && logLevel == LogLevel.Information;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel) && eventId.Id == 1) capture.Success(state);
            }
        }
    }
}
