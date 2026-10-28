using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identity;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Real Program, signed discovery-backed JWTs and fixed-v1 app-role membership data.</summary>
internal sealed class IdentityAuthorizationHttpProof : IAsyncDisposable
{
    private IdentityAuthorizationHttpProof(TenantAuthenticationIssuer issuer, IdentityPublicReadFixture database)
    {
        Issuer = issuer;
        Database = database;
    }

    internal const string DirectoryPath = "/api/v1/identity/memberships";
    internal TenantAuthenticationIssuer Issuer { get; }
    internal IdentityPublicReadFixture Database { get; }
    internal AuthorizationHttpObservation Observation { get; } = new();
    internal static UserId EnglishAdmin => SeedData.IdentityUsers[0].UserId;
    internal static UserId EnglishLearner => SeedData.IdentityUsers[1].UserId;
    internal static UserId YogaAdmin => SeedData.IdentityUsers[2].UserId;

    internal static async Task<IdentityAuthorizationHttpProof> CreateAsync(PublicReadFixture parent)
    {
        var issuer = await TenantAuthenticationIssuer.StartAsync();
        try
        {
            var database = await IdentityPublicReadFixture.CreateAsync(parent, issuer.Issuer);
            try
            {
                await database.SeedHttpFoundationAsync();
                return new(issuer, database);
            }
            catch { await database.DisposeAsync(); throw; }
        }
        catch { await issuer.DisposeAsync(); throw; }
    }

    internal HttpClient Client(Action<IServiceCollection>? configure = null) => Database.ClientFor(SeedData.English.Host,
        services =>
        {
            services.AddSingleton(Observation);
            services.Replace(ServiceDescriptor.Scoped<IIdentityBindingReader, ObservedBindingReader>());
            services.AddScoped<CommittedIdentityAuthorizationReader>();
            services.Replace(ServiceDescriptor.Scoped<IIdentityAuthorizationReader, ObservedAuthorizationReader>());
            services.AddScoped<MembershipDirectoryReader>();
            services.Replace(ServiceDescriptor.Scoped<IMembershipDirectoryReader, ObservedDirectoryReader>());
            configure?.Invoke(services);
        });

    internal string Token(UserId actor, SeedTenant tenant, bool organization = false, string? subject = null,
        OrganizationId? claimedOrganization = null) => AccountSiteHttpTests.Token(Issuer, tenant, organization,
            claimedOrganization: claimedOrganization?.Value, actor: actor.Value,
            subject: subject ?? IdentityPublicReadFixture.FoundationSubject(actor));

    internal static Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string? host = null,
        string path = DirectoryPath, string? tenantAssertion = null, string? organizationAssertion = null,
        string method = "GET") => TenantAuthenticationHttpRequest.Send(client, host ?? SeedData.English.Host,
            path, token, tenantAssertion: tenantAssertion, organizationAssertion: organizationAssertion, method: method);

    internal static async Task<JsonElement> PageAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        TenantAuthenticationHttpTests.NoStore(response);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    internal static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        await TenantAuthenticationHttpTests.Problem(response, status, code);
        TenantAuthenticationHttpTests.NoStore(response);
    }

    internal async Task<List<AccountHttpAuditRow>> AuditAsync(HttpResponseMessage response, SeedTenant tenant,
        OrganizationId? organization = null) => await AuditAsync(TenantAuthenticationReconciliationTests.Correlation(response), tenant, organization);

    internal async Task<List<AccountHttpAuditRow>> AuditAsync(string correlation, SeedTenant tenant,
        OrganizationId? organization = null)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant.TenantId.Value, organization?.Value);
        await using var command = new NpgsqlCommand("""
            SELECT tenant_id,organization_id,actor_user_id,operation,outcome,correlation_id,metadata::text
            FROM public.audit_log WHERE correlation_id=@correlation
            """, app, transaction);
        command.Parameters.AddWithValue("correlation", correlation);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<AccountHttpAuditRow>();
        while (await reader.ReadAsync())
            rows.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6)));
        return rows;
    }

    internal async Task SetMembershipStatusAsync(SeedTenant tenant, UserId actor, Guid membership,
        string status, OrganizationId? organization = null)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant.TenantId.Value, organization?.Value);
        await LockAsync(app, transaction, tenant.TenantId.Value, actor.Value, true);
        await using var update = new NpgsqlCommand("""
            UPDATE public.memberships SET status=@status,
                deleted_at=CASE WHEN @status='Revoked' THEN clock_timestamp() ELSE NULL END,
                deleted_by=CASE WHEN @status='Revoked' THEN @actor ELSE NULL END
            WHERE id=@member
            """, app, transaction);
        update.Parameters.AddWithValue("status", status);
        update.Parameters.AddWithValue("actor", actor.Value);
        update.Parameters.AddWithValue("member", membership);
        (await update.ExecuteNonQueryAsync()).Should().Be(1);
        await transaction.CommitAsync();
    }

    internal async Task OwnerAsync(string sql)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.MigrationConnectionString);
        await ExecuteAsync(owner, null, sql);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Database.DisposeAsync(); }
        finally { await Issuer.DisposeAsync(); }
    }

    private sealed class ObservedBindingReader(GlobalIdentityBindingService inner,
        AuthorizationHttpObservation observation) : IIdentityBindingReader
    {
        public async Task<IdentityBindingResult> ResolveExistingAsync(GlobalIdentitySubject subject,
            UserId? claimedActor = null, CancellationToken cancellationToken = default)
        {
            observation.BindingRead();
            return await inner.ResolveExistingAsync(subject, claimedActor, cancellationToken);
        }
    }

    private sealed class ObservedAuthorizationReader(CommittedIdentityAuthorizationReader inner,
        AuthorizationHttpObservation observation) : IIdentityAuthorizationReader
    {
        public async Task<IdentityAuthorizationResult> ReadAsync(AuthorizationCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            observation.AuthorizationRead();
            return await inner.ReadAsync(candidate, cancellationToken);
        }
    }

    private sealed class ObservedDirectoryReader(MembershipDirectoryReader inner, IUnitOfWork unit,
        AuthorizationHttpObservation observation) : IMembershipDirectoryReader
    {
        public async Task<IReadOnlyList<MembershipDirectoryItem>> ReadAsync(MembershipId? after, int take,
            CancellationToken cancellationToken = default)
        {
            observation.DirectoryRead(take);
            unit.Mode.Should().Be(TransactionMode.ReadWrite);
            await using (var command = new NpgsqlCommand("""
                SELECT current_user,rolsuper,rolbypassrls,current_setting('transaction_read_only'),
                    current_setting('app.scope',true)
                FROM pg_roles WHERE rolname=current_user
                """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction))
            await using (var row = await command.ExecuteReaderAsync(cancellationToken))
            {
                (await row.ReadAsync(cancellationToken)).Should().BeTrue();
                row.GetString(0).Should().Be("learnstack_app");
                row.GetBoolean(1).Should().BeFalse();
                row.GetBoolean(2).Should().BeFalse();
                row.GetString(3).Should().Be("off");
                row.GetString(4).Should().Be("tenant");
            }
            return await inner.ReadAsync(after, take, cancellationToken);
        }
    }
}

internal sealed class AuthorizationHttpObservation
{
    private int _bindings;
    private int _authorization;
    private int _directory;
    internal int BindingReads => Volatile.Read(ref _bindings);
    internal void BindingRead() => Interlocked.Increment(ref _bindings);
    internal int AuthorizationReads => Volatile.Read(ref _authorization);
    internal int DirectoryReads => Volatile.Read(ref _directory);
    internal void AuthorizationRead() => Interlocked.Increment(ref _authorization);
    internal int LastTake { get; private set; }
    internal void DirectoryRead(int take) { LastTake = take; Interlocked.Increment(ref _directory); }
}
