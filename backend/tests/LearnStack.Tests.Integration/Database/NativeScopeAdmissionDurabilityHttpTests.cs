using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class NativeScopeAdmissionDurabilityHttpTests(PublicReadFixture parent)
{
    private const string Path = "/api/v1/identity/session-scope/admissions";
    private const string Operation = "identity.membership.admit_session_scope";
    private const string Hop = "native-admission-proof-public-hop-separate-purpose";
    private const long CommitGate = 7_803_003_002;
    private static readonly string Native = WebEncoders.Base64UrlEncode(
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

    [Theory]
    [InlineData(false, HttpStatusCode.ServiceUnavailable, "audit_unavailable")]
    [InlineData(true, HttpStatusCode.InternalServerError, "internal_error")]
    public async Task Native_association_is_withheld_on_actual_MUST_INSERT_or_deferred_COMMIT_failure(
        bool deferred, HttpStatusCode status, string code)
    {
        await using var proof = await IdentityAuthorizationHttpProof.CreateAsync(parent);
        using var client = Client(proof.Database);
        var token = proof.Token(IdentityAuthorizationHttpProof.EnglishAdmin, SeedData.English);
        using (var control = await SendAsync(client, token))
            await AssertAssociationAsync(control);
        await InstallAuditTriggerAsync(proof, deferred, gate: false);
        try
        {
            using var refused = await SendAsync(client, token);

            // The ordinary RW owner preserves unexpected COMMIT faults. Unlike
            // an INSERT AuditWriteFailedException, fixture-only P0399 is not a
            // classified audit outage. Both paths must withhold the association.
            await TenantAuthenticationHttpTests.Problem(refused, status, code);
            TenantAuthenticationHttpTests.NoStore(refused);
            refused.Headers.Contains("Set-Cookie").Should().BeFalse();
            (await refused.Content.ReadAsStringAsync()).Should().NotContain("actorId")
                .And.NotContain("authorityId").And.NotContain("subjectKey");
            (await proof.AuditAsync(refused, SeedData.English)).Should().BeEmpty();
        }
        finally { await RemoveAuditTriggerAsync(proof); }

        using var recovered = await SendAsync(client, token);
        await AssertAssociationAsync(recovered);
        var row = (await proof.AuditAsync(recovered, SeedData.English)).Should().ContainSingle().Which;
        row.Operation.Should().Be(Operation);
        row.Outcome.Should().Be("success");
    }

    [Fact]
    public async Task Native_association_response_waits_for_the_physical_MUST_COMMIT()
    {
        await using var proof = await IdentityAuthorizationHttpProof.CreateAsync(parent);
        using var client = Client(proof.Database);
        await InstallAuditTriggerAsync(proof, deferred: true, gate: true);
        HttpResponseMessage? completedResponse = null;
        try
        {
            await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(blocker);
            await using var transaction = await blocker.BeginTransactionAsync();
            await ExecuteAsync(blocker, transaction, "SELECT pg_advisory_xact_lock(@key)", ("key", CommitGate));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var request = SendAsync(client, proof.Token(IdentityAuthorizationHttpProof.EnglishAdmin, SeedData.English));
            HttpResponseMessage response;
            try
            {
                await using var monitor = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
                while (!await ScalarAsync<bool>(monitor, null, """
                    SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity
                        WHERE @blocker=ANY(pg_catalog.pg_blocking_pids(pid)) AND query='COMMIT' AND state='active')
                    """, ("blocker", blocker.ProcessID)))
                {
                    request.IsCompleted.Should().BeFalse("the association cannot leave before its actual audit COMMIT");
                    await Task.Delay(20, deadline.Token);
                }
                request.IsCompleted.Should().BeFalse();
                await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
                await using var observation = await observer.BeginTransactionAsync();
                await ScopeAsync(observer, observation, SeedData.English.TenantId.Value);
                (await ScalarAsync<long>(observer, observation,
                    "SELECT count(*) FROM public.audit_log WHERE operation=@operation", ("operation", Operation)))
                    .Should().Be(0, "the real pending event is still invisible while its physical COMMIT waits");
            }
            finally
            {
                // Join the actual request after releasing its PostgreSQL blocker,
                // including when a pre-release assertion fails.
                await transaction.RollbackAsync(CancellationToken.None);
                response = await request.WaitAsync(TimeSpan.FromSeconds(20));
                completedResponse = response;
            }
            await AssertAssociationAsync(response);
            var row = (await proof.AuditAsync(response, SeedData.English)).Should().ContainSingle().Which;
            row.Operation.Should().Be(Operation);
            row.Outcome.Should().Be("success");
        }
        finally
        {
            completedResponse?.Dispose();
            await RemoveAuditTriggerAsync(proof);
        }
    }

    private static HttpClient Client(IdentityPublicReadFixture database) => database.ClientFor(SeedData.English.Host,
        services => services.AddSingleton<IStartupFilter, LoopbackPeer>(), builder =>
        {
            builder.UseSetting("Tenancy:TrustedHop:Networks:0", "127.0.0.1/32");
            builder.UseSetting("Tenancy:TrustedHop:Secrets:0", Hop);
            builder.UseSetting("Identity:NativeSession:CurrentSecret", Native);
        });

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path);
        request.Headers.Host = SeedData.English.Host;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(TrustedHopOptions.HostHeaderName, SeedData.English.Host);
        request.Headers.Add(TrustedHopOptions.SecretHeaderName, Hop);
        request.Headers.Add("X-LearnStack-Visitor-Address", "198.51.100.24");
        request.Headers.Add(NativeSessionSourcePolicy.SecretHeader, Native);
        request.Headers.Add(NativeSessionSourcePolicy.TenantHeader, SeedData.English.TenantId.Value.ToString("D"));
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task AssertAssociationAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        TenantAuthenticationHttpTests.NoStore(response);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("actorId").GetString().Should()
            .Be(IdentityAuthorizationHttpProof.EnglishAdmin.Value.ToString("D"));
        document.RootElement.GetProperty("authorityId").GetString().Should().NotBeNullOrWhiteSpace();
        document.RootElement.GetProperty("subjectKey").GetString().Should().HaveLength(64);
    }

    private static async Task InstallAuditTriggerAsync(IdentityAuthorizationHttpProof proof, bool deferred, bool gate)
    {
        var action = gate ? $"PERFORM pg_catalog.pg_advisory_xact_lock({CommitGate});"
            : "RAISE EXCEPTION 'Native admission audit fixture refusal' USING ERRCODE='P0399';";
        await proof.OwnerAsync($$"""
            CREATE FUNCTION public.fn_native_admission_audit_proof() RETURNS trigger LANGUAGE plpgsql AS $proof$
            BEGIN
                IF NEW.operation='{{Operation}}' THEN {{action}} END IF;
                RETURN NEW;
            END $proof$;
            """);
        await proof.OwnerAsync(deferred ? """
            CREATE CONSTRAINT TRIGGER tg_native_admission_audit_proof AFTER INSERT ON public.audit_log
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_native_admission_audit_proof()
            """ : """
            CREATE TRIGGER tg_native_admission_audit_proof BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.fn_native_admission_audit_proof()
            """);
    }

    private static Task RemoveAuditTriggerAsync(IdentityAuthorizationHttpProof proof) => proof.OwnerAsync("""
        DROP TRIGGER IF EXISTS tg_native_admission_audit_proof ON public.audit_log;
        DROP FUNCTION IF EXISTS public.fn_native_admission_audit_proof();
        """);

    private sealed class LoopbackPeer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return following(context);
            });
            next(app);
        };
    }
}
