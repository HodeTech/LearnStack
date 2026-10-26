using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using LearnStack.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class TenantExistingBindingAdmissionTests
{
    [Fact]
    public async Task Value_bearing_binding_read_failure_has_fixed_HTTP_diagnostics_and_recovers_with_exact_RLS_cleanup()
    {
        const string neutral = "binding-read-neutral-78e4";
        const string nested = "binding-read-nested-69f2";
        const string sensitive = "binding-read-sensitive-96a3";
        var payload = JsonSerializer.Serialize(new { value = neutral, items = new[] { new { value = nested, token = sensitive } } });
        var canaries = new[] { neutral, nested, sensitive };
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await IdentityPublicReadFixture.CreateAsync(parent, issuer.Issuer,
            new Dictionary<LearnStack.SharedKernel.Identifiers.UserId, string> { [Actor] = payload });
        var subject = new GlobalIdentitySubject(database.Registration.AuthorityId, issuer.Issuer, payload);
        var observation = new BindingObservation();
        var logs = new ReadDiagnosticLogSink();
        var spans = new ReadDiagnosticExporter();
        using var source = new ReadFailureWitness();
        using var client = Client(database, observation, services =>
        {
            services.AddSingleton<ILogEventSink>(logs);
            services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(spans)));
        });
        var token = Token(issuer, subject: payload, includeActor: false);
        using (var control = await SendAsync(client, token)) control.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadStoredSubjectAsync(database, subject)).Should().Be(payload);
        var reads = database.Observation.Reads;
        var originalPolicies = await ReadFaultShapeAsync(database);
        originalPolicies.FunctionExists.Should().BeFalse();
        await ExecuteOwnerAsync(database, """
            CREATE FUNCTION public.identity_read_sink_fault(value text) RETURNS boolean LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION USING ERRCODE='P0399', MESSAGE=value; END $$;
            REVOKE ALL ON FUNCTION public.identity_read_sink_fault(text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION public.identity_read_sink_fault(text) TO learnstack_app;
            CREATE POLICY identity_read_sink_fault ON public.users AS RESTRICTIVE FOR SELECT TO learnstack_app
                USING(public.identity_read_sink_fault(provider_subject));
            """);
        try
        {
            var dirty = await ReadFaultShapeAsync(database);
            dirty.FunctionExists.Should().BeTrue();
            dirty.Policies.Should().NotBe(originalPolicies.Policies,
                "the restoration comparison must detect the deliberately dirty fixture before cleanup");
            logs.Entries.Clear();
            spans.Items.Clear();
            using var refused = await SendAsync(client, token, TenantAuthenticationHttpRequest.PublicPath);
            await FixedUnauthorizedAsync(refused);
            var response = await refused.Content.ReadAsStringAsync();
            source.Failure.Should().NotBeNull("the real application-role binding SELECT must reach the provider fault");
            source.Failure!.SqlState.Should().Be("P0399");
            source.Failure.MessageText.Should().Be(payload,
                "the failing RLS function receives the actual retained provider subject, including neutral and nested values");
            source.Commands.Should().Be(1);
            var serializedLogs = logs.Serialize();
            var exportedSpans = string.Join('\n', spans.Items.Select(span => span.Json));
            var efErrors = spans.Items.Where(span => span.Source == "OpenTelemetry.Instrumentation.EntityFrameworkCore"
                && span.Status == ActivityStatusCode.Error).ToArray();
            efErrors.Should().ContainSingle().Which.Description.Should().Be("Identity storage command failed.");
            serializedLogs.Should().Contain("binding_store_unavailable").And.Contain("IdentityStorageReadFailed")
                .And.NotContain("QueryIterationFailed").And.NotContain("P0399").And.NotContain("Npgsql.PostgresException");
            logs.Entries.Where(entry => entry.MessageTemplate.Text == "Identity storage read failed.")
                .Should().ContainSingle().Which.Exception.Should().BeNull();
            serializedLogs.Should().NotContain("L1 exception handler captured",
                "the actual reader returns a uniform binding refusal, not a fabricated L1 exception path");
            output.WriteLine(JsonSerializer.Serialize(new
            {
                SourceReached = source.Failure.MessageText == payload,
                HttpStatus = (int)refused.StatusCode,
                QueryIterationEvent = serializedLogs.Contains("QueryIterationFailed", StringComparison.Ordinal),
                LogContainsCanary = canaries.Any(serializedLogs.Contains),
                TraceContainsCanary = canaries.Any(exportedSpans.Contains),
                ResponseContainsCanary = canaries.Any(response.Contains),
                ExportedSpans = spans.Items.Count,
                IdentityErrorSpans = efErrors.Length,
            }));
            foreach (var canary in canaries)
            {
                serializedLogs.Should().NotContain(canary);
                exportedSpans.Should().NotContain(canary);
                response.Should().NotContain(canary);
            }
            database.Observation.Reads.Should().Be(reads, "the refused binding never reaches the tenant reader");
        }
        finally
        {
            await ExecuteOwnerAsync(database, """
                DROP POLICY identity_read_sink_fault ON public.users;
                DROP FUNCTION public.identity_read_sink_fault(text);
                """);
        }
        (await ReadFaultShapeAsync(database)).Should().Be(originalPolicies,
            "the exact policy inventory is restored and the case-owned function is gone");
        (await ReadStoredSubjectAsync(database, subject)).Should().Be(payload);
        using var restored = await SendAsync(client, token);
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        observation.Lookups.Should().Be(3);
        observation.ClosedOwners.Should().Be(3);
    }

    [Fact]
    public async Task Sibling_query_failure_preserves_its_existing_EF_iteration_event_and_span_description()
    {
        using var sinks = new IdentityDiagnosticSinks();
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(parent.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(app, transaction, SeedData.English.TenantId.Value);
        await using var db = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(app).UseLoggerFactory(sinks.Loggers).Options,
            new StaticTenantContextAccessor(new SeedTenantContext(SeedData.English.TenantId, null)));
        await db.Database.UseTransactionAsync(transaction);
        var query = () => db.Database.SqlQueryRaw<int>("SELECT CAST('sibling-query-control' AS integer) AS \"Value\"").SingleAsync();
        var failure = (await query.Should().ThrowAsync<PostgresException>()).Which;
        sinks.LogText.Should().Contain("QueryIterationFailed").And.NotContain("IdentityStorageReadFailed");
        sinks.Traces.Where(trace => trace.Source == "OpenTelemetry.Instrumentation.EntityFrameworkCore"
            && trace.Status == ActivityStatusCode.Error).Should().ContainSingle().Which.Description.Should().Be(failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Identity_FromSql_failures_keep_a_fixed_read_signal_for_sync_and_async_commands(bool asynchronous)
    {
        const string value = "identity-fromsql-read-canary-4791";
        using var sinks = new IdentityDiagnosticSinks();
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(parent.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(app, transaction, SeedData.English.TenantId.Value);
        await using var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(app).UseLoggerFactory(sinks.Loggers).Options,
            new StaticTenantContextAccessor(new SeedTenantContext(SeedData.English.TenantId, null)), EmptyIdentityScope.Instance);
        await db.Database.UseTransactionAsync(transaction);
        var query = db.Database.SqlQuery<int>($"SELECT CAST({value} AS integer) AS \"Value\"");
        PostgresException failure;
        if (asynchronous)
        {
            var read = () => query.SingleAsync();
            failure = (await read.Should().ThrowAsync<PostgresException>()).Which;
        }
        else
        {
            var read = () => query.Single();
            failure = read.Should().Throw<PostgresException>().Which;
        }
        failure.MessageText.Should().Contain(value, "the actual provider received the parameter in both execution modes");
        sinks.LogText.Should().Contain("IdentityStorageReadFailed").And.Contain("3192")
            .And.NotContain("QueryIterationFailed").And.NotContain(value);
        var error = sinks.Traces.Where(trace => trace.Source == "OpenTelemetry.Instrumentation.EntityFrameworkCore"
            && trace.Status == ActivityStatusCode.Error).Should().ContainSingle().Which;
        error.Description.Should().Be("Identity storage command failed.");
        error.Json.Should().NotContain(value);
    }

    private static async Task<string?> ReadStoredSubjectAsync(IdentityPublicReadFixture database, GlobalIdentitySubject subject)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction, subject);
        await using var command = new NpgsqlCommand("SELECT provider_subject FROM public.users WHERE id=@actor", app, transaction);
        command.Parameters.AddWithValue("actor", Actor.Value);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<(string Policies, bool FunctionExists)> ReadFaultShapeAsync(IdentityPublicReadFixture database)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(jsonb_agg(jsonb_build_object('name',polname,'permissive',polpermissive,
                'command',polcmd,'roles',polroles::text,'using',pg_get_expr(polqual,polrelid),
                'check',pg_get_expr(polwithcheck,polrelid)) ORDER BY polname),'[]'::jsonb)::text,
                to_regprocedure('public.identity_read_sink_fault(text)') IS NOT NULL
            FROM pg_policy WHERE polrelid='public.users'::regclass
            """, owner);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return (row.GetString(0), row.GetBoolean(1));
    }

    private sealed class ReadDiagnosticLogSink : ILogEventSink
    {
        internal ConcurrentQueue<LogEvent> Entries { get; } = new();
        public void Emit(LogEvent logEvent) => Entries.Enqueue(logEvent);
        internal string Serialize()
        {
            using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            var formatter = new JsonFormatter(renderMessage: true, formatProvider: System.Globalization.CultureInfo.InvariantCulture);
            foreach (var entry in Entries) formatter.Format(entry, writer);
            return writer.ToString();
        }
    }

    private sealed record ReadSpan(string Source, ActivityStatusCode Status, string? Description, string Json);
    private sealed class ReadDiagnosticExporter : BaseExporter<Activity>
    {
        internal ConcurrentQueue<ReadSpan> Items { get; } = new();
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
                Items.Enqueue(new(activity.Source.Name, activity.Status, activity.StatusDescription, JsonSerializer.Serialize(new
                {
                    activity.DisplayName,
                    activity.Id,
                    activity.ParentId,
                    activity.StatusDescription,
                    Status = activity.Status.ToString(),
                    Tags = activity.TagObjects.ToArray(),
                    Events = activity.Events.Select(item => new { item.Name, Tags = item.Tags.ToArray() }).ToArray(),
                })));
            return ExportResult.Success;
        }
    }

    private sealed class ReadFailureWitness : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly IDisposable _all;
        private readonly List<IDisposable> _subscriptions = [];
        internal PostgresException? Failure { get; private set; }
        internal int Commands { get; private set; }
        internal ReadFailureWitness() => _all = DiagnosticListener.AllListeners.Subscribe(this);
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore") _subscriptions.Add(listener.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == "Microsoft.EntityFrameworkCore.Database.Command.CommandError"
                && value.Value is CommandErrorEventData { Context: IdentityDbContext, Exception: PostgresException failure })
            {
                Failure = failure;
                Commands++;
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
