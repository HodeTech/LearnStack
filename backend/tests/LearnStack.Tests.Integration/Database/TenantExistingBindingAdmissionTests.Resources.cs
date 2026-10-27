using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class TenantExistingBindingAdmissionTests
{
    [Fact]
    public async Task Completed_binding_returns_the_connector_before_its_HTTP_scope_dispatches_tenant_work()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await DatabaseAsync(issuer);
        var application = "binding-dispatch-" + Guid.NewGuid().ToString("N");
        await using var source = ApplicationDataSource.Build(PoolConnection(database, application, maximum: 1));
        var observation = new BindingObservation();
        using var client = Client(database, observation, services => UseSource(services, source));
        var token = Token(issuer, includeActor: false);
        using (var warm = await SendAsync(client, token)) warm.StatusCode.Should().Be(HttpStatusCode.OK);
        var backend = observation.BackendIds.Last();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        observation.AfterRead = async cancellationToken =>
        {
            if (Interlocked.Increment(ref reads) != 1) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        var heldRequest = SendAsync(client, token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The first request still owns its DI scope but has completed only
            // the global read. A second real binding and tenant read must fit
            // through the one-slot pool without releasing the first request.
            using var concurrent = await SendAsync(client, token).WaitAsync(TimeSpan.FromSeconds(10));
            concurrent.StatusCode.Should().Be(HttpStatusCode.OK);
            heldRequest.IsCompleted.Should().BeFalse();
            observation.BackendIds.Last().Should().Be(backend);
        }
        finally { release.TrySetResult(); }
        using var completed = await heldRequest.WaitAsync(TimeSpan.FromSeconds(10));
        completed.StatusCode.Should().Be(HttpStatusCode.OK, "the original scoped unit must reopen for its tenant owner");
        observation.ClosedOwners.Should().Be(3);
        await WaitForPoolAsync(database, application, state => state.InTransactions == 0 && state.Active == 0);
    }

    [Fact]
    public async Task Small_real_HTTP_sample_reports_lookup_plan_statement_cost_and_bounded_concurrent_pool_occupancy()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await DatabaseAsync(issuer);
        // These are actual globally audited bindings, not rows inserted around the
        // permanent creation/audit guards. This is a bounded local sample only.
        const int population = 32;
        for (var index = 1; index < population; index++)
            await database.AdoptAsync(UserId.From(Guid.CreateVersion7()), "sample-subject-" + index);
        await ExecuteOwnerAsync(database, "ANALYZE public.users");
        var application = "binding-sample-" + Guid.NewGuid().ToString("N");
        using var statements = new BindingStatementCounter();
        using var loggers = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(statements));
        await using var source = ApplicationDataSource.Build(PoolConnection(database, application, maximum: 4), loggers);
        var expected = new VerifiedProviderSubject(issuer.Issuer, Subject, null, SeedData.English.TenantId, null);
        var counterfactual = await WitnessCounterfactualAsync(database, source, expected);
        var controlApplication = "binding-control-" + Guid.NewGuid().ToString("N");
        using var controlStatements = new BindingStatementCounter();
        using var controlLoggers = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(controlStatements));
        await using var controlSource = ApplicationDataSource.Build(PoolConnection(database, controlApplication, maximum: 4), controlLoggers);
        var observation = new BindingObservation();
        var controlObservation = new BindingObservation();
        using var commands = new BindingCommandObservation();
        using var client = Client(database, observation, services => UseSource(services, source));
        using var controlClient = Client(database, controlObservation, services =>
        {
            UseSource(services, controlSource);
            services.Replace(ServiceDescriptor.Singleton<ITenantIdentityBindingResolver>(counterfactual));
        });
        var token = Token(issuer, includeActor: false);
        using (var warm = await SendAsync(client, token, TenantAuthenticationHttpRequest.PublicPath))
            warm.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var warm = await SendAsync(controlClient, token, TenantAuthenticationHttpRequest.PublicPath))
            warm.StatusCode.Should().Be(HttpStatusCode.OK);

        const int samples = 12;
        var anonymous = new List<double>();
        var control = new List<double>();
        var bound = new List<double>();
        var before = statements.Statements;
        var beforeBindings = commands.Commands;
        for (var index = 0; index < samples; index++)
            anonymous.Add(await TimedReadAsync(client, null));
        var anonymousStatements = statements.Statements - before;
        commands.Commands.Should().Be(beforeBindings, "the anonymous comparison performs no global lookup");
        before = statements.Statements;
        var beforeControlStatements = controlStatements.Statements;
        var beforeControlCalls = counterfactual.Calls;
        for (var index = 0; index < samples; index++)
        {
            // Alternate order to reduce a simple first/last-lane bias. Both
            // requests carry the same compact token and traverse stock validation,
            // subject quota and the same real public tenant reader.
            if (index % 2 == 0) control.Add(await TimedReadAsync(controlClient, token));
            bound.Add(await TimedReadAsync(client, token));
            if (index % 2 != 0) control.Add(await TimedReadAsync(controlClient, token));
        }
        var boundStatements = statements.Statements - before;
        var counterfactualStatements = controlStatements.Statements - beforeControlStatements;
        (commands.Commands - beforeBindings).Should().Be(samples, "every authenticated sample reads committed binding state once");
        (counterfactual.Calls - beforeControlCalls).Should().Be(samples);
        controlObservation.Lookups.Should().Be(0, "the current-code counterfactual omits only the binding resolver's database read");
        await WaitForPoolAsync(database, controlApplication, state => state.InTransactions == 0 && state.Active == 0);

        var held = 0;
        var allOccupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        observation.BeforeRead = async cancellationToken =>
        {
            if (Interlocked.Increment(ref held) == 4) allOccupied.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        var concurrent = Enumerable.Range(0, 8).Select(_ => TimedReadAsync(client, token)).ToArray();
        PoolObservation peak;
        try
        {
            await allOccupied.Task.WaitAsync(TimeSpan.FromSeconds(10));
            peak = await PoolAsync(database, application);
            peak.Connections.Should().Be(4);
            peak.InTransactions.Should().Be(4, "four real global owners occupy the four-connection source while other callers await checkout");
            concurrent.Count(task => !task.IsCompleted).Should().Be(8);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(concurrent).WaitAsync(TimeSpan.FromSeconds(15));
        observation.BeforeRead = null;
        await WaitForPoolAsync(database, application, state => state.InTransactions == 0 && state.Active == 0);
        var plan = await ExplainActualLookupAsync(database, commands);
        observation.ClosedOwners.Should().Be(observation.Lookups);

        output.WriteLine("Local observational sample. The authenticated current-code counterfactual returns one previously witnessed Existing binding for the exact verified input; it is neither a historical nor a security-qualified baseline. No production percentile guarantee.");
        output.WriteLine("Bindings={0}; same authenticated public GET samples per lane={1}, interleaved after warmup; counterfactual median/range ms={2:F2}/{3:F2}-{4:F2}; production binding median/range ms={5:F2}/{6:F2}-{7:F2}; median difference ms={8:F2}.",
            population + SeedData.IdentityUsers.Length, samples, Median(control), control.Min(), control.Max(), Median(bound), bound.Min(), bound.Max(), Median(bound) - Median(control));
        output.WriteLine("Anonymous contextual comparison median/range ms={0:F2}/{1:F2}-{2:F2}; driver SQL statements anonymous/counterfactual/production={3}/{4}/{5}; actual production binding SELECTs={6}; counterfactual binding SELECTs=0; observer adds one role/GUC proof SELECT per production lookup.",
            Median(anonymous), anonymous.Min(), anonymous.Max(), anonymousStatements, counterfactualStatements, boundStatements, samples);
        output.WriteLine("Default planner nodes={0}; actual rows={1}; planning/execution ms={2:F3}/{3:F3}; concurrent HTTP=8; pool maximum/observed occupied=4/{4}; remaining active/transaction owners=0.",
            plan.Nodes, plan.Rows, plan.PlanningMilliseconds, plan.ExecutionMilliseconds, peak.InTransactions);
    }

    [Fact]
    public async Task Real_blocked_lookup_cancellation_releases_the_owner_and_reuses_one_clean_physical_connector()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await DatabaseAsync(issuer);
        var application = "binding-cancellation-" + Guid.NewGuid().ToString("N");
        await using var source = ApplicationDataSource.Build(PoolConnection(database, application, maximum: 1));
        var observation = new BindingObservation();
        using var client = Client(database, observation, services => UseSource(services, source));
        var token = Token(issuer, includeActor: false);
        using (var warm = await SendAsync(client, token)) warm.StatusCode.Should().Be(HttpStatusCode.OK);
        var originalBackend = observation.BackendIds.Last();
        var counts = await CountsAsync(database);
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var held = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("LOCK TABLE public.users IN ACCESS EXCLUSIVE MODE", blocker, held))
            await command.ExecuteNonQueryAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(HttpMethod.Get, TenantAuthenticationHttpRequest.AccountPath);
        request.Headers.Host = SeedData.English.Host;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var blocked = client.SendAsync(request, cancellation.Token);
        try
        {
            await WaitForPoolAsync(database, application, state => state.LockWaiters == 1);
            blocked.IsCompleted.Should().BeFalse("the real users SELECT must be blocked before cancellation is issued");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
            // Keep the real blocker held: only cancellation/cleanup can free this
            // owner. Releasing the lock first would make the proof vacuous.
            await WaitForPoolAsync(database, application, state => state.LockWaiters == 0 && state.InTransactions == 0 && state.Active == 0);
        }
        finally { await held.RollbackAsync(); }

        await using (var reused = await source.OpenConnectionAsync())
        await using (var transaction = await reused.BeginTransactionAsync())
        await using (var proof = new NpgsqlCommand("""
            SELECT pg_backend_pid(),current_setting('transaction_read_only'),
                COALESCE(current_setting('app.identity_authority_id',true),''),
                COALESCE(current_setting('app.identity_subject_key',true),''),
                COALESCE(current_setting('app.tenant_id',true),''),COALESCE(current_setting('app.organization_id',true),'')
            """, reused, transaction))
        await using (var row = await proof.ExecuteReaderAsync())
        {
            (await row.ReadAsync()).Should().BeTrue();
            row.GetInt32(0).Should().Be(originalBackend, "ordinary cancellation must return the same connector cleanly to the one-slot pool");
            row.GetString(1).Should().Be("off");
            for (var field = 2; field < 6; field++) row.GetString(field).Should().BeEmpty();
        }
        using var restored = await SendAsync(client, token);
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        observation.ClosedOwners.Should().Be(3);
        (await CountsAsync(database)).Should().Be(counts);
        output.WriteLine("One-slot real pool: observed blocked users SELECT, cancelled while blocker remained held, zero surviving transaction/lock owners, same physical connector reused with cleared identity/tenant GUCs.");
    }

    private static string PoolConnection(IdentityPublicReadFixture database, string application, int maximum) =>
        new NpgsqlConnectionStringBuilder(database.AppConnectionString)
        {
            Pooling = true,
            MaxPoolSize = maximum,
            MinPoolSize = 0,
            Timeout = 10,
            CommandTimeout = 10,
            ApplicationName = application,
        }.ConnectionString;

    private static void UseSource(IServiceCollection services, NpgsqlDataSource source) =>
        services.Replace(ServiceDescriptor.Singleton(new Lazy<NpgsqlDataSource>(() => source)));

    private static async Task<WitnessedCounterfactualBindingResolver> WitnessCounterfactualAsync(
        IdentityPublicReadFixture database, NpgsqlDataSource source, VerifiedProviderSubject expected)
    {
        await using var provider = SeedComposition.Build(source, null, NullLoggerFactory.Instance,
            identityAuthority: database.Registration);
        await provider.GetRequiredService<IdentityAuthorityQualification>().QualifyAsync();
        await using var scope = provider.CreateAsyncScope();
        var witnessed = await scope.ServiceProvider.GetRequiredService<IIdentityBindingReader>().ResolveExistingAsync(
            new GlobalIdentitySubject(database.Registration.AuthorityId, expected.Issuer, expected.Subject), expected.ClaimedActor);
        witnessed.Outcome.Should().Be(IdentityBindingOutcome.Existing);
        witnessed.Actor.Should().Be(Actor);
        return new(expected, witnessed);
    }

    private static async Task<double> TimedReadAsync(HttpClient client, string? token)
    {
        var started = Stopwatch.GetTimestamp();
        using var response = await SendAsync(client, token, TenantAuthenticationHttpRequest.PublicPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await response.Content.ReadAsByteArrayAsync();
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static double Median(List<double> observations)
    {
        var ordered = observations.Order().ToArray();
        return (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
    }

    private static async Task<PoolObservation> PoolAsync(IdentityPublicReadFixture database, string application)
    {
        // Same login as the observed source, so pg_stat_activity exposes actual
        // state without granting fixture-only pg_monitor or privileged app roles.
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var query = new NpgsqlCommand("""
            SELECT count(*)::integer,count(*) FILTER(WHERE state='active')::integer,
                count(*) FILTER(WHERE xact_start IS NOT NULL)::integer,
                count(*) FILTER(WHERE wait_event_type='Lock' AND query LIKE '%users%')::integer
            FROM pg_stat_activity WHERE datname=current_database() AND usename=current_user AND application_name=@application
            """, observer);
        query.Parameters.AddWithValue("application", application);
        await using var row = await query.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return new(row.GetInt32(0), row.GetInt32(1), row.GetInt32(2), row.GetInt32(3));
    }

    private static async Task WaitForPoolAsync(IdentityPublicReadFixture database, string application,
        Func<PoolObservation, bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate(await PoolAsync(database, application))) await Task.Delay(15, deadline.Token);
    }

    private static async Task<LookupPlan> ExplainActualLookupAsync(IdentityPublicReadFixture database,
        BindingCommandObservation observation)
    {
        observation.Sql.Should().NotBeNullOrWhiteSpace("a real production EF binding SELECT must have executed");
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", app, transaction))
            await readOnly.ExecuteNonQueryAsync();
        await AnnounceAsync(app, transaction, new GlobalIdentitySubject(database.Registration.AuthorityId,
            database.Registration.Issuer, Subject));
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + observation.Sql, app, transaction);
        foreach (var parameter in observation.Parameters)
            explain.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.NpgsqlDbType) { Value = parameter.Value });
        using var document = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!);
        var root = document.RootElement[0];
        var plan = root.GetProperty("Plan");
        var actualRows = plan.GetProperty("Actual Rows").GetDouble();
        actualRows.Should().Be(1);
        var nodes = new List<string>();
        AddNodes(plan, nodes);
        return new(string.Join('/', nodes), actualRows,
            root.GetProperty("Planning Time").GetDouble(), root.GetProperty("Execution Time").GetDouble());
    }

    private static void AddNodes(JsonElement plan, List<string> nodes)
    {
        nodes.Add(plan.GetProperty("Node Type").GetString()!);
        if (plan.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray()) AddNodes(child, nodes);
    }

    private sealed record PoolObservation(int Connections, int Active, int InTransactions, int LockWaiters);
    private sealed record LookupPlan(string Nodes, double Rows, double PlanningMilliseconds, double ExecutionMilliseconds);

    /// <summary>Measurement-only current-code counterfactual; it qualifies no authentication or persistence boundary.</summary>
    private sealed class WitnessedCounterfactualBindingResolver(VerifiedProviderSubject expected, IdentityBindingResult witnessed)
        : ITenantIdentityBindingResolver
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public Task<IdentityBindingResult> ResolveExistingAsync(VerifiedProviderSubject subject,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            // Record equality includes issuer, opaque subject, optional actor and
            // both scope claims. Every other input receives no binding authority.
            return Task.FromResult(subject == expected ? witnessed : IdentityBindingResult.Refused(IdentityBindingOutcome.AdmissionDenied));
        }
    }

    private sealed class BindingStatementCounter : ILoggerProvider, ILogger
    {
        private int _statements;
        internal int Statements => Volatile.Read(ref _statements);
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Executing command: ", StringComparison.Ordinal))
                Interlocked.Increment(ref _statements);
        }
        public void Dispose() { }
    }
}
