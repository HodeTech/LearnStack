using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Secrets;
using LearnStack.Tests.Fixtures;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class ProviderEventAdapterTests
{
    private async Task AssertActualMirrorAsync(KeycloakAdminClient admin, string realm, ISecretProvider secrets, UserId affected, Guid sourceEvent)
    {
        await using var parent = new PublicReadFixture();
        await parent.InitializeAsync();
        await using var database = await IdentityPublicReadFixture.CreateAsync(parent, fixture.Issuer);
        var subjects = new List<object>();
        foreach (var persona in SeedData.IdentityUsers)
        {
            var account = await admin.UserAsync(persona.Username);
            subjects.Add(new { actorId = persona.UserId.Value, opaqueSub = account.GetProperty("id").GetString()! });
        }
        await using (var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, issuer = fixture.Issuer, users = subjects })))
            await database.SeedFoundationAsync(await SeedProviderWitness.ReadAsync(input, database.Registration));
        var originalTenant = SeedData.IdentityUsers.Single(persona => persona.UserId == affected).Tenant;
        foreach (var tenant in SeedData.All.Where(tenant => tenant != originalTenant))
        {
            var added = await database.AddMembershipAsync(tenant, affected);
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var tx = await app.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(app, tx, tenant.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, tx, tenant.TenantId.Value, affected.Value, exclusive: false);
            await IdentityMembershipFixture.ExecuteAsync(app, tx, "UPDATE public.memberships SET status='Suspended' WHERE id=@id", ("id", added));
            await tx.CommitAsync();
        }
        var connection = new NpgsqlConnectionStringBuilder(parent.Postgres.IdentityIngressConnectionString)
        { Database = new NpgsqlConnectionStringBuilder(database.AppConnectionString).Database }.ConnectionString;
        var provider = Create(database.Registration.AuthorityId, realm, secrets);
        await using var providerOwner = (IAsyncDisposable)provider;
        await using var api = new MirrorApi(database, connection, realm, provider);
        _ = api.Services;
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var total = 0L;
            foreach (var tenant in SeedData.All)
            {
                await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
                await SchemaQueries.AssertApplicationRoleAsync(app);
                await using var tx = await app.BeginTransactionAsync();
                await SchemaQueries.SetTenantAsync(app, tx, tenant.TenantId.Value);
                total += await IdentityMembershipFixture.ScalarAsync<long>(app, tx, """
                    SELECT count(*) FROM public.audit_log WHERE operation='audit.provider_event.observe'
                        AND actor_user_id=@system AND entity_type='User' AND entity_id=@affected AND outcome='success'
                        AND metadata->'provider_event'->>'provider_event_id'=@source
                        AND metadata->'provider_event'->>'scope'='provider_account_history'
                    """, ("system", UserId.SystemActor.Value), ("affected", affected.Value.ToString("D")), ("source", sourceEvent.ToString("D")));
            }
            if (total == SeedData.All.Count) break;
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(40))
                throw new TimeoutException("Actual provider history did not reach both tenant Audit owners.");
            await Task.Delay(20);
        }
        // The two tenant rows are independent of realm/BFF claims and of which tenant the login UI originally used.
    }

    private sealed class MirrorApi(IdentityPublicReadFixture database, string ingress, string realm, IIdentityEventProvider provider) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development"); InvitationEmailFixtureConfiguration.AddTo(builder);
            builder.UseSetting("ConnectionStrings:Default", database.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", database.OutboxConnectionString);
            builder.UseSetting("ConnectionStrings:IdentityIngress", ingress);
            builder.UseSetting("Identity:AuthorityId", database.Registration.AuthorityId.Value.ToString("D"));
            builder.UseSetting("Authentication:Tenant:Issuer", database.Registration.Issuer);
            builder.UseSetting("IdentityEvents:RealmId", realm); builder.UseSetting("Telemetry:OtlpEndpoint", "");
            builder.ConfigureTestServices(services => { services.RemoveAll<IIdentityEventProvider>(); services.AddSingleton(provider); });
        }
    }
}
