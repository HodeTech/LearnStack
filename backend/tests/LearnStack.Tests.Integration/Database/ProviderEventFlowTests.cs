using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Errors;
using LearnStack.Modules.Identity.Application.Contracts.ProviderEvents;
using LearnStack.Modules.Identity.Application.ProviderEvents;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Real Program, machine source, ordinary producer, outbox/inbox and central Audit; provider HTTP is a separate proof.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class ProviderEventFlowTests(IdentityMembershipFixture fixture) : IClassFixture<IdentityMembershipFixture>
{
    [Fact]
    public async Task Actual_background_intake_produces_one_attributed_tenant_audit_and_duplicate_restart_does_not_repeat_it()
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        var id = Guid.NewGuid();
        var fact = new IdentityProviderEvent(id, at, IdentityProviderEventKind.LoginFailure,
            IdentitySubjectKey.Compute(IdentityMembershipFixture.Authority.AuthorityId,
                "fixture-subject-" + IdentityMembershipFixture.PersonA.ToString("N")));
        for (var run = 0; run < 2; run++)
        {
            await using var api = new Api(fixture, new Provider(fact));
            _ = api.Services;
            await WaitAsync(async () => await CountAsync(id) == 1);
            await using var machine = await PostgresFixture.OpenAsync(fixture.Postgres.IdentityIngressConnectionString);
            (await IdentityMembershipFixture.ScalarAsync<int>((NpgsqlConnection)machine, null,
                "SELECT target_count FROM public.identity_provider_event_receipts WHERE provider_event_id=@id", ("id", id))).Should().Be(1);
        }
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var tx = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, tx, SchemaFixture.TenantA);
        await using var row = new NpgsqlCommand("""
            SELECT actor_user_id,entity_type,entity_id,outcome,operation_type,operation_class,metadata::text,timestamp
            FROM public.audit_log WHERE operation='audit.provider_event.observe' AND metadata->'provider_event'->>'provider_event_id'=@id
            """, app, tx);
        row.Parameters.AddWithValue("id", id.ToString("D"));
        await using var reader = await row.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetGuid(0).Should().Be(UserId.SystemActor.Value);
        reader.GetString(1).Should().Be("User");
        reader.GetString(2).Should().Be(IdentityMembershipFixture.PersonA.ToString("D"));
        reader.GetString(3).Should().Be("success", "Audit observation succeeds even when the provider reports login failure");
        reader.GetString(4).Should().Be("SecurityEvent"); reader.GetString(5).Should().Be("Must");
        using var metadata = JsonDocument.Parse(reader.GetString(6));
        var observed = metadata.RootElement.GetProperty("provider_event");
        observed.GetProperty("scope").GetString().Should().Be("provider_account_history");
        observed.GetProperty("provider_outcome").GetString().Should().Be("error");
        observed.GetProperty("source_occurred_at").GetDateTimeOffset().Should().Be(at);
        reader.GetFieldValue<DateTimeOffset>(7).Should().BeAfter(at);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_central_MUST_write_keeps_the_actual_inbox_and_delivery_incomplete_until_retry()
    {
        var id = Guid.NewGuid();
        var fact = new IdentityProviderEvent(id, DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()),
            IdentityProviderEventKind.Logout, IdentitySubjectKey.Compute(IdentityMembershipFixture.Authority.AuthorityId,
                "fixture-subject-" + IdentityMembershipFixture.PersonB.ToString("N")));
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.MigrationConnectionString);
        await IdentityMembershipFixture.ExecuteAsync(owner, null, "ALTER TABLE public.audit_log ADD CONSTRAINT refuse_provider_observation CHECK(operation<>'audit.provider_event.observe') NOT VALID");
        try
        {
            await using var api = new Api(fixture, new Provider(fact));
            _ = api.Services;
            await WaitAsync(async () => await DeliveryCountAsync(id, "d.last_error IS NOT NULL") == 1);
            (await CountAsync(id, SchemaFixture.TenantB)).Should().Be(0);
            (await DeliveryCountAsync(id, "d.processed_at IS NOT NULL")).Should().Be(0);
            await IdentityMembershipFixture.ExecuteAsync(owner, null, "ALTER TABLE public.audit_log DROP CONSTRAINT refuse_provider_observation");
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.OutboxConnectionString);
            await IdentityMembershipFixture.ExecuteAsync(app, null, "UPDATE public.event_deliveries SET available_after=clock_timestamp() WHERE subscription_id='audit.provider-event-mirror.v1' AND processed_at IS NULL");
            await WaitAsync(async () => await CountAsync(id, SchemaFixture.TenantB) == 1);
            (await DeliveryCountAsync(id, "d.processed_at IS NOT NULL")).Should().Be(1);
        }
        finally
        {
            await IdentityMembershipFixture.ExecuteAsync(owner, null, "ALTER TABLE public.audit_log DROP CONSTRAINT IF EXISTS refuse_provider_observation");
        }
    }

    private async Task<long> CountAsync(Guid id, Guid? tenant = null)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var tx = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, tx, tenant ?? SchemaFixture.TenantA);
        return await IdentityMembershipFixture.ScalarAsync<long>(app, tx,
            "SELECT count(*) FROM public.audit_log WHERE operation='audit.provider_event.observe' AND metadata->'provider_event'->>'provider_event_id'=@id", ("id", id.ToString("D")));
    }

    [Fact]
    public async Task Provider_outage_and_a_failed_claim_do_not_block_a_later_already_accepted_target()
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        var key = IdentitySubjectKey.Compute(IdentityMembershipFixture.Authority.AuthorityId,
            "fixture-subject-" + IdentityMembershipFixture.PersonA.ToString("N"));
        var facts = new[] { new IdentityProviderEvent(Guid.NewGuid(), at, IdentityProviderEventKind.Login, key),
            new IdentityProviderEvent(Guid.NewGuid(), at.AddMilliseconds(1), IdentityProviderEventKind.Logout, key) };
        var failure = new FirstProductionFailure();
        await using var api = new Api(fixture, new InterruptedProvider(facts), services =>
        {
            services.AddScoped<ProviderEventTargetPublisher>();
            services.RemoveAll<IProviderEventTargetPublisher>();
            services.AddScoped<IProviderEventTargetPublisher>(sp => new FailFirstPublisher(sp.GetRequiredService<ProviderEventTargetPublisher>(), failure));
        });
        _ = api.Services;
        await WaitAsync(async () => await CountAsync(facts[0].SourceEventId) + await CountAsync(facts[1].SourceEventId) == 1);
        failure.Failed.Should().BeTrue();
        (await CountAsync(facts[0].SourceEventId) + await CountAsync(facts[1].SourceEventId)).Should().Be(1);
    }

    private async Task<long> DeliveryCountAsync(Guid id, string condition)
    {
        Guid[] events;
        await using (var tenant = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString))
        await using (var tx = await tenant.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(tenant);
            await IdentityMembershipFixture.ScopeAsync(tenant, tx, SchemaFixture.TenantB);
            await using var query = new NpgsqlCommand("SELECT event_id FROM public.identity_provider_event_targets WHERE provider_event_id=@id", tenant, tx);
            query.Parameters.AddWithValue("id", id);
            var values = new List<Guid>();
            await using var rows = await query.ExecuteReaderAsync();
            while (await rows.ReadAsync()) values.Add(rows.GetGuid(0));
            events = [.. values];
        }
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.OutboxConnectionString);
        return await IdentityMembershipFixture.ScalarAsync<long>(app, null, """
            SELECT count(*) FROM public.event_deliveries d
            WHERE d.subscription_id='audit.provider-event-mirror.v1' AND d.event_id=ANY(@events)
            """ + " AND " + condition, ("events", events));
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var started = Stopwatch.GetTimestamp();
        while (!await condition())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(40))
                throw new TimeoutException("Provider flow did not complete its expected durable boundary.");
            await Task.Delay(20);
        }
    }

    private sealed class Api(IdentityMembershipFixture fixture, IIdentityEventProvider provider, Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            InvitationEmailFixtureConfiguration.AddTo(builder);
            builder.UseSetting("ConnectionStrings:Default", fixture.Postgres.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", fixture.Postgres.OutboxConnectionString);
            builder.UseSetting("ConnectionStrings:IdentityIngress", fixture.Postgres.IdentityIngressConnectionString);
            builder.UseSetting("Identity:AuthorityId", IdentityMembershipFixture.Authority.AuthorityId.Value.ToString("D"));
            builder.UseSetting("Authentication:Tenant:Issuer", IdentityMembershipFixture.Authority.Issuer);
            builder.UseSetting("IdentityEvents:RealmId", "learnstack");
            builder.UseSetting("Telemetry:OtlpEndpoint", "");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IIdentityEventProvider>(); services.AddSingleton(provider);
                configure?.Invoke(services);
            });
        }
    }

    private sealed class FirstProductionFailure { internal bool Failed; }
    private sealed class FailFirstPublisher(ProviderEventTargetPublisher actual, FirstProductionFailure failure) : IProviderEventTargetPublisher
    {
        public Task ProduceAsync(ProviderEventRecipient recipient, CancellationToken cancellationToken = default)
        {
            if (!failure.Failed) { failure.Failed = true; throw new InvalidOperationException("Owned fixture producer failure."); }
            return actual.ProduceAsync(recipient, cancellationToken);
        }
    }
    private sealed class InterruptedProvider(IdentityProviderEvent[] facts) : IIdentityEventProvider
    {
        private bool _read;
        public Task<IdentityEventSourcePolicy> ReadPolicyAsync(CancellationToken cancellationToken = default) => Task.FromResult(new IdentityEventSourcePolicy(604800));
        public Task<IdentityEventPage> ReadAsync(IdentityEventRead request, CancellationToken cancellationToken = default)
        {
            if (_read) throw new IdentityEventProviderException(IdentityEventProviderFailure.Unavailable);
            _read = true;
            return Task.FromResult(IdentityEventPage.Read(facts.Select(IdentityEventReadItem.Observed)));
        }
    }

    private sealed class Provider(IdentityProviderEvent fact) : IIdentityEventProvider
    {
        public Task<IdentityEventSourcePolicy> ReadPolicyAsync(CancellationToken cancellationToken = default) => Task.FromResult(new IdentityEventSourcePolicy(604800));
        public Task<IdentityEventPage> ReadAsync(IdentityEventRead request, CancellationToken cancellationToken = default) =>
            Task.FromResult(IdentityEventPage.Read(new[] { fact }.Where(item => item.OccurredAt >= request.From && item.OccurredAt <= request.To)
                .Skip(request.Offset).Take(request.Maximum).Select(IdentityEventReadItem.Observed)));
    }
}
