using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0106 and ADR-0107 § 4 through the actual Program, signed provider tokens and real PostgreSQL as
/// <c>learnstack_app</c>: the live-session data-request API end to end, the export job through the actual job runtime,
/// the ticketed download, and every refusal the decision names.
/// </summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class DataRequestHttpTests(PublicReadFixture parent)
{
    private const string OwnPath = "/api/v1/identity/me/data-requests";
    private const string AdminPath = "/api/v1/identity/data-requests";
    private static readonly SeedTenant English = SeedData.English;
    private static readonly Guid LearnerMembership = SeedData.IdentityMemberships[1].MembershipId;

    private async Task<IdentityAuthorizationHttpProof> CreatePreparedAsync()
    {
        var proof = await CreateAsync(parent);
        try
        {
            await using var source = ApplicationDataSource.Build(proof.Database.AppConnectionString);
            var runner = new SeedAdministrationOriginRunner(context => SeedComposition.Build(source, context,
                NullLoggerFactory.Instance, identityAuthority: proof.Database.Registration,
                seedOriginAdmission: new FixedSeedAdministrationOriginAdmission()));
            (await runner.RunAsync(SeedData.All.Select(tenant => tenant.TenantId))).Should().Be(2);
            return proof;
        }
        catch { await proof.DisposeAsync(); throw; }
    }

    private static string Key() => "data-request-" + Guid.NewGuid().ToString("N");

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string path, string method = "GET",
        string? body = null, string? version = null, string? key = null, string? host = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Host = host ?? English.Host;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", "\"" + version + "\"");
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, text);
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("code").GetString().Should().Be(code);
        return document.RootElement.Clone();
    }

    /// <summary>Files an export through the API and returns its id, after checking the 202 contract.</summary>
    private static async Task<(Guid Id, string Version)> FileExportAsync(HttpClient client, string token, string? key = null,
        string path = OwnPath + "/exports", string body = "{}")
    {
        using var response = await SendAsync(client, token, path, "POST", body, key: key ?? Key());
        var accepted = await JsonAsync(response, HttpStatusCode.Accepted);
        accepted.EnumerateObject().Select(property => property.Name).Should().Equal("id", "kind", "status", "version");
        accepted.GetProperty("kind").GetString().Should().Be("export");
        accepted.GetProperty("status").GetString().Should().Be("requested");
        var id = Guid.Parse(accepted.GetProperty("id").GetString()!);
        id.Version.Should().Be(7);
        response.Headers.Location!.OriginalString.Should().EndWith("/" + id.ToString("D"));
        response.Headers.ETag!.Tag.Should().Be("\"" + accepted.GetProperty("version").GetString() + "\"");
        return (id, accepted.GetProperty("version").GetString()!);
    }

    private static async Task<JsonElement> ReceiptAsync(HttpClient client, string token, Guid id, string path = OwnPath)
    {
        using var response = await SendAsync(client, token, path + "/" + id.ToString("D"));
        var receipt = await JsonAsync(response);
        response.Headers.ETag!.Tag.Should().Be("\"" + receipt.GetProperty("version").GetString() + "\"");
        return receipt;
    }

    /// <summary>Runs the request's export job through the actual job runtime of a host on the same database.</summary>
    private static async Task<JobBusinessExecutionResult> RunExportAsync(IdentityAuthorizationHttpProof proof, Guid tenant,
        Guid? organization, Guid request)
    {
        var parameters = BackgroundJobPayloadContract.Serialize(new DataExportJobParameters(request), typeof(DataExportJobParameters),
            BackgroundJobPayloadKind.Parameters);
        Guid job;
        await using (var app = await OpenAppAsync(proof))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, tenant, organization);
            job = await ScalarAsync<Guid>(app, transaction, """
                SELECT id FROM public.background_jobs
                WHERE definition_key=@definition AND parameters=@parameters AND organization_id IS NOT DISTINCT FROM @organization::uuid
                """, ("definition", organization is null ? "identity.data_export_tenant.v1" : "identity.data_export_organization.v1"),
                ("parameters", parameters), ("organization", (object?)organization ?? DBNull.Value));
        }

        await using var host = new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Default", proof.Database.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", proof.Database.OutboxConnectionString);
        });
        var executor = host.Services.GetRequiredService<JobExecutor>();
        var start = await executor.ExecuteAsync(await RoutingClaim.ClaimAsync(proof.Database.OutboxConnectionString,
            BackgroundJobId.From(job)), BackgroundJobTestRun.Create());
        start.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        var result = await executor.ExecuteBusinessAsync(start.Receipt!, start.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional));
        result.CommitAcknowledged.Should().BeTrue("disposition {0}, code {1}", result.Decision.Disposition, result.Decision.ErrorCode);
        return result;
    }

    private static async Task<NpgsqlConnection> OpenAppAsync(IdentityAuthorizationHttpProof proof)
    {
        var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        return app;
    }

    /// <summary>A count read as the application role inside one group.</summary>
    private static async Task<long> CountAsync(IdentityAuthorizationHttpProof proof, Guid tenant, Guid? organization, string sql,
        params (string Name, object? Value)[] values)
    {
        await using var app = await OpenAppAsync(proof);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant, organization);
        return await ScalarAsync<long>(app, transaction, sql, values);
    }

    private static Task<long> AuditCountAsync(IdentityAuthorizationHttpProof proof, Guid tenant, Guid? organization, string operation,
        string? entity = null, string outcome = "success") => CountAsync(proof, tenant, organization, """
            SELECT count(*) FROM public.audit_log
            WHERE operation=@operation AND outcome=@outcome AND organization_id IS NOT DISTINCT FROM @organization::uuid
              AND (@entity::text IS NULL OR entity_id=@entity::text)
            """, ("operation", operation), ("outcome", outcome), ("organization", (object?)organization ?? DBNull.Value),
            ("entity", (object?)entity ?? DBNull.Value));

    /// <summary>Mints a ticket through the API and reads every chunk in order, returning the reassembled bundle.</summary>
    private static async Task<(string Ticket, byte[] Bundle)> DownloadAsync(HttpClient client, string token, Guid request)
    {
        using var minted = await SendAsync(client, token, OwnPath + "/" + request.ToString("D") + "/artifact/tickets", "POST", "{}");
        var grant = await JsonAsync(minted);
        grant.EnumerateObject().Select(property => property.Name).Should().Equal("ticket", "expiresAt", "chunkCount", "byteCount");
        minted.Headers.ETag.Should().BeNull();
        var ticket = grant.GetProperty("ticket").GetString()!;
        ticket.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        var count = grant.GetProperty("chunkCount").GetInt32();
        var bytes = new List<byte>();
        for (var index = 0; index < count; index++)
        {
            using var read = await ChunkAsync(client, token, request, ticket, index);
            var chunk = await JsonAsync(read);
            chunk.EnumerateObject().Select(property => property.Name).Should().Equal("index", "count", "bytes");
            chunk.GetProperty("index").GetInt32().Should().Be(index);
            chunk.GetProperty("count").GetInt32().Should().Be(count);
            bytes.AddRange(Convert.FromBase64String(chunk.GetProperty("bytes").GetString()!));
        }

        bytes.Count.Should().Be(checked((int)grant.GetProperty("byteCount").GetInt64()));
        return (ticket, bytes.ToArray());
    }

    private static Task<HttpResponseMessage> ChunkAsync(HttpClient client, string token, Guid request, string ticket, int index) =>
        SendAsync(client, token, OwnPath + "/" + request.ToString("D") + "/artifact/chunks", "POST",
            JsonSerializer.Serialize(new { ticket, index }));

    private sealed class RoutingClaim : BackgroundJobRoutingClaim.MintingSource
    {
        internal static async Task<BackgroundJobRoutingClaim> ClaimAsync(string connectionString, BackgroundJobId job)
        {
            await using var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
            await using var command = new NpgsqlCommand("""
                UPDATE background_jobs SET ownership_token=@token,ownership_kind='routing',
                    ownership_until=clock_timestamp()+interval '4 minutes' WHERE id=@job
                RETURNING id,tenant_id,organization_id,definition_key,queue,wake_generation,ownership_token,
                    ownership_until,original_due_at,profile,phase,clock_timestamp()
                """, dispatcher);
            command.Parameters.AddWithValue("job", job.Value);
            command.Parameters.AddWithValue("token", Guid.NewGuid());
            await using var row = await command.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            return MintClaim(row);
        }
    }

    private static string Version(JsonElement element) => element.GetProperty("version").GetString()!;

    private static long Number(string value) => long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
}
