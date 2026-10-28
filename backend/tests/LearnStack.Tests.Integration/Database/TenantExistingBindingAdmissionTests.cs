using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Core;
using Xunit;
using Xunit.Abstractions;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Ordinary HTTP admission through the real application-role global self reader.</summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class TenantExistingBindingAdmissionTests(PublicReadFixture parent, ITestOutputHelper output)
{
    private const string Subject = "binding-http-private-subject-canary";
    private static UserId Actor => UserId.From(TenantAuthenticationIssuer.Actor);

    [Fact]
    public async Task Actorless_and_equal_evidence_resolve_the_stored_actor_before_the_real_tenant_read()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await DatabaseAsync(issuer);
        var observation = new BindingObservation();
        using var client = Client(database, observation);
        var counts = await CountsAsync(database);
        var reads = database.Observation.Reads;

        foreach (var includeActor in new[] { false, true })
        {
            using var response = await SendAsync(client, Token(issuer, includeActor: includeActor));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            TenantAuthenticationHttpTests.NoStore(response);
            var account = await response.Content.ReadFromJsonAsync<JsonElement>();
            account.GetProperty("actorId").GetGuid().Should().Be(Actor.Value);
            account.GetProperty("tenantId").GetGuid().Should().Be(SeedData.English.TenantId.Value);
        }

        using var publicResponse = await SendAsync(client, Token(issuer, includeActor: false),
            TenantAuthenticationHttpRequest.PublicPath);
        publicResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        database.Observation.Reads.Should().Be(reads + 3,
            "the existing reader independently verifies the actual tenant transaction is read-only and application-role");
        observation.Lookups.Should().Be(3);
        observation.ClosedOwners.Should().Be(3,
            "normalization must return with no global owner, capture or resolved tenant context before tenant reconciliation");
        (await CountsAsync(database)).Should().Be(counts, "ordinary admission allocates no User and writes no creation audit");
    }

    [Fact]
    public async Task Unknown_and_conflicting_bindings_refuse_even_public_reads_without_allocation_or_adoption()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await DatabaseAsync(issuer);
        var observation = new BindingObservation();
        using var client = Client(database, observation);
        var counts = await CountsAsync(database);
        using (var control = await SendAsync(client, Token(issuer))) control.StatusCode.Should().Be(HttpStatusCode.OK);
        var reads = database.Observation.Reads;

        foreach (var path in new[] { TenantAuthenticationHttpRequest.AccountPath, TenantAuthenticationHttpRequest.PublicPath })
            foreach (var token in new[]
            {
            Token(issuer, subject: "unbound-private-subject-canary", includeActor: false),
            Token(issuer, subject: "unbound-private-subject-canary"),
            Token(issuer, actor: Guid.CreateVersion7()),
        })
            {
                using var response = await SendAsync(client, token, path);
                await FixedUnauthorizedAsync(response);
            }

        observation.Lookups.Should().Be(7);
        observation.ClosedOwners.Should().Be(7);
        database.Observation.Reads.Should().Be(reads);
        (await CountsAsync(database)).Should().Be(counts);
    }

    [Theory]
    [InlineData("Closing")]
    [InlineData("Closed")]
    [InlineData("Collision")]
    public async Task Committed_nonadmitting_or_collision_state_is_rechecked_without_an_identity_cache(string state)
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await DatabaseAsync(issuer);
        var observation = new BindingObservation();
        using var client = Client(database, observation);
        var token = Token(issuer, includeActor: false);
        using (var control = await SendAsync(client, token)) control.StatusCode.Should().Be(HttpStatusCode.OK);
        var counts = await CountsAsync(database);
        var subjectDigestDefinition = await SetBindingStateAsync(database, state);
        try
        {
            foreach (var path in new[] { TenantAuthenticationHttpRequest.AccountPath, TenantAuthenticationHttpRequest.PublicPath })
            {
                using var refused = await SendAsync(client, token, path);
                await FixedUnauthorizedAsync(refused);
            }
            observation.Lookups.Should().Be(3);
            (await CountsAsync(database)).Should().Be(counts);
        }
        finally { await SetBindingStateAsync(database, "Active", subjectDigestDefinition); }
        using var restored = await SendAsync(client, token);
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertBindingGuardsRestoredAsync(database, subjectDigestDefinition);
    }

    [Fact]
    public async Task Anonymous_and_earlier_refusals_do_no_binding_work_and_exhausted_subject_quota_precedes_SQL()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await DatabaseAsync(issuer);
        var observation = new BindingObservation();
        using var limiter = new TenantSubjectRateLimiter();
        using var client = Client(database, observation, services =>
            services.Replace(ServiceDescriptor.Singleton(limiter)));
        using (var anonymous = await SendAsync(client, null, TenantAuthenticationHttpRequest.PublicPath))
            anonymous.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var absent = await SendAsync(client, null)) absent.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var invalid = await SendAsync(client, "not-a-bearer-token")) await FixedUnauthorizedAsync(invalid);
        using (var unknownHost = await TenantAuthenticationHttpRequest.Send(client, "unmapped.invalid",
            TenantAuthenticationHttpRequest.PublicPath, Token(issuer))) unknownHost.StatusCode.Should().Be(HttpStatusCode.NotFound);
        observation.Lookups.Should().Be(0);

        using (var control = await SendAsync(client, Token(issuer, includeActor: false)))
            control.StatusCode.Should().Be(HttpStatusCode.OK);
        // Prime the unchanged production registry directly, avoiding 599 redundant
        // database reads. The final permit and its HTTP refusal use the same issuer/sub key.
        var verified = new VerifiedProviderSubject(issuer.Issuer, Subject, null, null, null);
        for (var permit = 1; permit < TenantSubjectRateLimiter.PermitPerWindow; permit++)
            limiter.AttemptAcquire(verified).IsAcquired.Should().BeTrue();
        using var exhausted = await SendAsync(client, Token(issuer, actor: Guid.CreateVersion7()),
            TenantAuthenticationHttpRequest.PublicPath);
        exhausted.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        exhausted.Headers.RetryAfter.Should().NotBeNull();
        TenantAuthenticationHttpTests.NoStore(exhausted);
        observation.Lookups.Should().Be(1, "changed actor evidence does not bypass the issuer/subject budget or reach binding SQL");
    }

    [Fact]
    public async Task Actual_store_permission_fault_is_uniform_401_with_a_fixed_private_diagnostic_and_recovers()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await DatabaseAsync(issuer);
        var observation = new BindingObservation();
        var logs = new BindingLogSink();
        using var client = Client(database, observation, services => services.AddSingleton<ILogEventSink>(logs));
        var token = Token(issuer, includeActor: false);
        using (var control = await SendAsync(client, token)) control.StatusCode.Should().Be(HttpStatusCode.OK);
        var counts = await CountsAsync(database);
        logs.BeginFaultWindow();
        await ExecuteOwnerAsync(database, "REVOKE SELECT ON public.users FROM learnstack_app");
        try
        {
            using var unavailable = await SendAsync(client, token, TenantAuthenticationHttpRequest.PublicPath);
            await FixedUnauthorizedAsync(unavailable);
            logs.AssertUnavailable(Subject, token, Actor.Value.ToString("D"));
            (await CountsAsync(database)).Should().Be(counts);
        }
        finally { await ExecuteOwnerAsync(database, "GRANT SELECT ON public.users TO learnstack_app"); }
        using var restored = await SendAsync(client, token);
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        observation.Lookups.Should().Be(3, "the planted SQL fault must be reached between two actual positive reads");
        observation.ClosedOwners.Should().Be(3);
    }

    private Task<IdentityPublicReadFixture> DatabaseAsync(TenantAuthenticationIssuer issuer) =>
        IdentityPublicReadFixture.CreateWithMembershipsAsync(parent, issuer.Issuer, new Dictionary<UserId, string> { [Actor] = Subject });

    private static HttpClient Client(IdentityPublicReadFixture database, BindingObservation observation,
        Action<IServiceCollection>? configure = null) => database.ClientFor(SeedData.English.Host, services =>
    {
        ObserveBindings(services, observation);
        configure?.Invoke(services);
    }, builder => builder.UseSetting("Serilog:MinimumLevel:Default", "Debug"));

    private static string Token(TenantAuthenticationIssuer issuer, string subject = Subject, bool includeActor = true,
        Guid? actor = null)
    {
        var payload = AccountSiteHttpTests.Token(issuer, SeedData.English, subject: subject, actor: actor);
        if (includeActor) return payload;
        // AccountSiteHttpTests.Token returns the compact token; recreate its payload
        // through the issuer to exercise genuinely absent compatibility evidence.
        using var document = JsonDocument.Parse(issuer.Payload(
            ("sub", subject), ("tenant_id", SeedData.English.TenantId.Value.ToString("D"))));
        var withoutActor = "{" + string.Join(',', document.RootElement.EnumerateObject()
            .Where(property => !property.NameEquals("user_id") && !property.NameEquals("organization_id"))
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + property.Value.GetRawText())) + "}";
        return issuer.Token(withoutActor);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string? token,
        string path = TenantAuthenticationHttpRequest.AccountPath) =>
        TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host, path, token);

    private static async Task FixedUnauthorizedAsync(HttpResponseMessage response)
    {
        await TenantAuthenticationHttpTests.Problem(response, HttpStatusCode.Unauthorized, "unauthorized");
        TenantAuthenticationHttpTests.NoStore(response);
        response.Headers.WwwAuthenticate.Should().ContainSingle().Which.ToString().Should().Be("Bearer");
        (await response.Content.ReadAsStringAsync()).Should().NotContain(Subject).And.NotContain("binding_store_unavailable");
    }
}
