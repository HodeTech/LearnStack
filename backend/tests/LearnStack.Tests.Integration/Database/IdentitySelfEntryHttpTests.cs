using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Time;
using Npgsql;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class IdentitySelfEntryHttpTests(PublicReadFixture parent)
{
    private const string Hop = "self-entry-test-public-hop-with-separate-purpose";
    private static readonly string Native = WebEncoders.Base64UrlEncode(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
    private static readonly string Previous = WebEncoders.Base64UrlEncode(Enumerable.Range(33, 32).Select(value => (byte)value).ToArray());
    private const string Self = "/api/v1/identity/self";

    [Fact]
    public async Task Actual_actorless_HTTP_bootstrap_converges_and_never_publishes_a_tenant_session()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await IdentityPublicReadFixture.CreateAsync(parent, issuer.Issuer);
        using var client = Client(database);
        var token = issuer.Token(issuer.Without("user_id", "tenant_id", "organization_id"));
        using (var missing = await SendAsync(client, token))
        {
            missing.StatusCode.Should().Be(HttpStatusCode.OK);
            (await JsonAsync(missing)).GetProperty("state").GetString().Should().Be("missing");
        }
        string? actor;
        using (var created = await SendAsync(client, token, Self + "/bootstrap", "POST", "{}"))
        {
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            created.Headers.Location!.OriginalString.Should().Be(Self);
            var json = await JsonAsync(created);
            actor = json.GetProperty("actorId").GetString();
            json.GetProperty("authorityId").GetString().Should().Be(database.Registration.AuthorityId.Value.ToString("D"));
            json.GetProperty("subjectKey").GetString().Should().Be(
                new GlobalIdentitySubject(database.Registration.AuthorityId, issuer.Issuer, "opaque-keycloak-subject").SubjectKey.Hex);
            created.Headers.Contains("Set-Cookie").Should().BeFalse();
        }
        using (var existing = await SendAsync(client, token, Self + "/bootstrap", "POST", "{}"))
        {
            existing.StatusCode.Should().Be(HttpStatusCode.OK);
            (await JsonAsync(existing)).GetProperty("actorId").GetString().Should().Be(actor);
        }
        using (var contexts = await SendAsync(client, token, Self + "/contexts"))
        {
            contexts.StatusCode.Should().Be(HttpStatusCode.OK);
            (await JsonAsync(contexts)).GetProperty("items").GetArrayLength().Should().Be(0);
        }
        using var account = await SendAsync(client, token, "/api/v1/account/site");
        await TenantAuthenticationHttpTests.Problem(account, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Actual_self_boundary_masks_missing_source_and_refuses_unclassified_or_malformed_input()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await IdentityPublicReadFixture.CreateAsync(parent, issuer.Issuer);
        using var client = Client(database);
        var token = issuer.Token(issuer.Without("user_id", "tenant_id", "organization_id"));
        foreach (var path in new[] { Self, Self + "/contexts", Self + "/bootstrap" })
        {
            using var noSource = await SendAsync(client, token, path, native: null);
            await TenantAuthenticationHttpTests.Problem(noSource, HttpStatusCode.NotFound, "not_found");
            using var invalidSource = await SendAsync(client, token, path, native: Hop);
            await TenantAuthenticationHttpTests.Problem(invalidSource, HttpStatusCode.Unauthorized, "unauthorized");
        }
        using (var method = await SendAsync(client, token, Self, "POST", "{}"))
        {
            method.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            method.Content.Headers.Allow.Should().ContainSingle().Which.Should().Be("GET");
        }
        foreach (var body in new[] { "", "[]", "{\"confirm\":true}", "{} {}", new string(' ', 513) })
        {
            using var malformed = await SendAsync(client, token, Self + "/bootstrap", "POST", body);
            await TenantAuthenticationHttpTests.Problem(malformed, HttpStatusCode.BadRequest, "validation_failed");
        }
        foreach (var header in new[] { TenantAssertionMiddleware.TenantHeaderName, TenantAssertionMiddleware.OrganizationHeaderName })
        {
            using var assertion = await SendAsync(client, token, assertionHeader: header);
            await TenantAuthenticationHttpTests.Problem(assertion, HttpStatusCode.BadRequest, "validation_failed");
        }
        using var stranger = await SendAsync(client, token, Self + "/other", "POST", "{}");
        stranger.StatusCode.Should().NotBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Actual_own_discovery_crosses_only_captured_self_scopes_then_closes_each_label_owner()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await IdentityPublicReadFixture.CreateAsync(parent, issuer.Issuer);
        await database.SeedHttpFoundationAsync();
        var actor = SeedData.IdentityUsers[1].UserId;
        await database.AddMembershipAsync(SeedData.Yoga, actor,
            SeedData.Yoga.DefaultOrganization.OrganizationId, BuiltInRoleKey.Learner);
        var payload = issuer.Without("user_id", "tenant_id", "organization_id");
        using var claims = JsonDocument.Parse(payload);
        var values = claims.RootElement.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.Clone());
        values["sub"] = JsonSerializer.SerializeToElement(IdentityPublicReadFixture.FoundationSubject(actor));
        var token = issuer.Token(JsonSerializer.Serialize(values));
        using var client = Client(database);
        using var first = await SendAsync(client, token, Self + "/contexts?limit=1");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await JsonAsync(first);
        page.GetProperty("items").GetArrayLength().Should().Be(1);
        var item = page.GetProperty("items")[0];
        item.GetProperty("tenantId").GetString().Should().Be(SeedData.English.TenantId.Value.ToString("D"));
        item.GetProperty("entryOrigin").GetString().Should().Be("https://" + SeedData.English.Host + ":3000");
        var cursor = page.GetProperty("pageInfo").GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrEmpty();
        using var second = await SendAsync(client, token, Self + "/contexts?cursor=" + cursor);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var next = await JsonAsync(second);
        next.GetProperty("items").GetArrayLength().Should().Be(1);
        next.GetProperty("pageInfo").GetProperty("hasNext").GetBoolean().Should().BeFalse();
        next.GetProperty("pageInfo").GetProperty("hasPrevious").GetBoolean().Should().BeFalse();
        next.GetProperty("items")[0].GetProperty("tenantId").GetString().Should().Be(SeedData.Yoga.TenantId.Value.ToString("D"));
        (await second.Content.ReadAsStringAsync()).Should().NotContain("displayName").And.NotContain("roleIds")
            .And.NotContain("membershipId").And.NotContain("subjectKey");
        using var foreign = await SendAsync(client, issuer.Token(issuer.Without("user_id", "tenant_id", "organization_id")),
            Self + "/contexts?cursor=" + cursor);
        await TenantAuthenticationHttpTests.Problem(foreign, HttpStatusCode.BadRequest, "validation_failed");
        (await JsonAsync(foreign)).GetProperty("errors").GetProperty("cursor")[0]
            .GetProperty("key").GetString().Should().Be("lockey_invalid_value");
        (await foreign.Content.ReadAsStringAsync()).Should().NotContain(cursor!);
    }

    private static HttpClient Client(IdentityPublicReadFixture database, Action<IServiceCollection>? configure = null,
        Action<IWebHostBuilder>? settings = null) => database.ClientFor(SeedData.English.Host,
        services => { services.AddSingleton<IStartupFilter, LoopbackPeer>(); configure?.Invoke(services); }, builder =>
        {
            builder.UseSetting("Tenancy:TrustedHop:Networks:0", "127.0.0.1/32");
            builder.UseSetting("Tenancy:TrustedHop:Secrets:0", Hop);
            builder.UseSetting("Identity:NativeSession:CurrentSecret", Native);
            builder.UseSetting("Identity:NativeSession:HttpsPort", "3000");
            builder.UseSetting("Identity:SelfBootstrap:Enabled", "true");
            builder.UseSetting("Serilog:MinimumLevel:Default", "Verbose");
            settings?.Invoke(builder);
        });

    [Fact]
    public async Task Http2_GET_with_body_and_without_length_or_transfer_headers_refuses_before_any_self_read()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await IdentityPublicReadFixture.CreateAsync(parent, issuer.Issuer);
        using var client = Client(database);
        var token = issuer.Token(issuer.Without("user_id", "tenant_id", "organization_id"));
        foreach (var path in new[] { Self, Self + "/contexts" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new UnknownLengthJson(),
            };
            request.Headers.Host = SeedData.English.Host;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add(TrustedHopOptions.HostHeaderName, SeedData.English.Host);
            request.Headers.Add(TrustedHopOptions.SecretHeaderName, Hop);
            request.Headers.Add("X-LearnStack-Visitor-Address", "198.51.100.23");
            request.Headers.Add(NativeSessionSourcePolicy.SecretHeader, Native);
            request.Content.Headers.ContentLength.Should().BeNull();
            request.Headers.TransferEncoding.Should().BeEmpty();
            using var response = await client.SendAsync(request);
            await TenantAuthenticationHttpTests.Problem(response, HttpStatusCode.BadRequest, "validation_failed");
        }
    }

    [Fact]
    public async Task Malformed_self_context_requests_keep_SQL_ports_lazy_and_name_only_the_invalid_known_field()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await IdentityPublicReadFixture.CreateAsync(parent, issuer.Issuer);
        var discoveryFactories = 0;
        var labelFactories = 0;
        using var client = Client(database, services =>
        {
            services.Replace(ServiceDescriptor.Scoped<Func<IIdentitySelfDiscovery>>(_ => () =>
            {
                Interlocked.Increment(ref discoveryFactories);
                throw new InvalidOperationException("Discovery construction probe.");
            }));
            services.Replace(ServiceDescriptor.Scoped<Func<IOwnMembershipScopeLabels>>(_ => () =>
            {
                Interlocked.Increment(ref labelFactories);
                throw new InvalidOperationException("Label construction probe.");
            }));
        });
        var token = issuer.Token(issuer.Without("user_id", "tenant_id", "organization_id"));
        const string Canary = "private-invalid-pagination-canary";
        foreach (var (query, field) in new (string, string?)[]
        {
            ("?limit=0", "limit"), ("?limit=-1", "limit"), ("?limit=02", "limit"),
            ("?limit=1&limit=2", "limit"), ("?cursor=" + Canary, "cursor"),
            ("?cursor=", "cursor"), ("?unexpected=" + Canary, null),
        })
        {
            using var refused = await SendAsync(client, token, Self + "/contexts" + query);
            await TenantAuthenticationHttpTests.Problem(refused, HttpStatusCode.BadRequest, "validation_failed");
            var body = await refused.Content.ReadAsStringAsync();
            body.Should().NotContain(Canary);
            if (field is not null)
            {
                using var problem = JsonDocument.Parse(body);
                var errors = problem.RootElement.GetProperty("errors");
                errors.EnumerateObject().Should().ContainSingle().Which.Name.Should().Be(field);
                errors.GetProperty(field)[0].GetProperty("key").GetString().Should().Be("lockey_invalid_value");
            }
        }
        using (var body = await SendAsync(client, token, Self + "/contexts", body: "{}"))
            await TenantAuthenticationHttpTests.Problem(body, HttpStatusCode.BadRequest, "validation_failed");
        discoveryFactories.Should().Be(0, "all shape refusals precede SQL-port construction");
        labelFactories.Should().Be(0);
        using var valid = await SendAsync(client, token, Self + "/contexts?limit=1");
        await TenantAuthenticationHttpTests.Problem(valid, HttpStatusCode.InternalServerError, "internal_error");
        discoveryFactories.Should().Be(1, "the throwing factory is really wired into the admitted path");
        labelFactories.Should().Be(0);
    }

    private sealed class UnknownLengthJson : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync("{}"u8.ToArray()).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_source_retirement_during_scope_capture_or_label_checkout_returns_fixed_401(bool labels)
    {
        await using var proof = await IdentityAuthorizationHttpProof.CreateAsync(parent);
        var clock = new ExpiryClock(DateTimeOffset.UtcNow);
        using var client = Client(proof.Database, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IClock>(clock));
            if (labels)
            {
                services.AddScoped<PreContextSelfDiscoveryScopeReader>();
                services.Replace(ServiceDescriptor.Scoped<IOwnMembershipScopeLabels>(provider =>
                    new ExpiringLabels(provider.GetRequiredService<PreContextSelfDiscoveryScopeReader>(), clock)));
            }
            else
            {
                services.AddScoped<UserBindingStore>();
                services.Replace(ServiceDescriptor.Scoped<IUserBindingStore>(provider =>
                    new ExpiringBindingRead(provider.GetRequiredService<UserBindingStore>(), clock)));
            }
        }, builder =>
        {
            builder.UseSetting("Identity:NativeSession:PreviousSecret", Previous);
            builder.UseSetting("Identity:NativeSession:PreviousRetiresAt", clock.UtcNow.AddMinutes(2).ToString("O",
                System.Globalization.CultureInfo.InvariantCulture));
        });
        var token = proof.Token(IdentityAuthorizationHttpProof.EnglishLearner, SeedData.English);
        using var response = await SendAsync(client, token, Self + "/contexts", native: Previous);
        await TenantAuthenticationHttpTests.Problem(response, HttpStatusCode.Unauthorized, "unauthorized");
        clock.Expired.Should().BeTrue("the real operation must reach the chosen retirement boundary");
        (await response.Content.ReadAsStringAsync()).Should().NotContain("items").And.NotContain("subjectKey");
    }

    [Fact]
    public async Task Actual_hosting_diagnostics_do_not_emit_a_self_discovery_cursor()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await IdentityPublicReadFixture.CreateAsync(parent, issuer.Issuer);
        var sink = new CursorSink();
        var traces = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => traces.Enqueue(activity.OperationName + " " + string.Join(" ", activity.TagObjects)),
        };
        ActivitySource.AddActivityListener(listener);
        using var client = Client(database, services => services.AddSingleton<ILogEventSink>(sink));
        var token = issuer.Token(issuer.Without("user_id", "tenant_id", "organization_id"));
        const string CursorCanary = "private-self-cursor-canary-28bd79";
        using var response = await SendAsync(client, token, Self + "/contexts?cursor=" + CursorCanary);
        await TenantAuthenticationHttpTests.Problem(response, HttpStatusCode.BadRequest, "validation_failed");
        sink.Events.Should().Contain(value => value.Contains("Request", StringComparison.Ordinal),
            "the real hosting logger must reach the configured production sink");
        sink.Events.Should().NotContain(value => value.Contains(CursorCanary, StringComparison.Ordinal),
            "raw cursor text is personal relationship input, including when malformed");
        traces.Should().NotBeEmpty("the real ASP.NET request activity must be sampled by the control");
        traces.Should().NotContain(value => value.Contains(CursorCanary, StringComparison.Ordinal),
            "trace URL/query attributes must not reveal the self-discovery cursor either");
    }

    [Fact]
    public async Task Actual_native_scope_admission_commits_one_MUST_event_then_ordinary_reads_do_not_repeat_it()
    {
        await using var proof = await IdentityAuthorizationHttpProof.CreateAsync(parent);
        var payload = proof.Issuer.Without("user_id", "tenant_id", "organization_id");
        using var parsed = JsonDocument.Parse(payload);
        var claims = parsed.RootElement.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.Clone());
        claims["sub"] = JsonSerializer.SerializeToElement(IdentityPublicReadFixture.FoundationSubject(IdentityAuthorizationHttpProof.EnglishAdmin));
        var token = proof.Issuer.Token(JsonSerializer.Serialize(claims));
        using var client = Client(proof.Database);
        using var response = await SendAsync(client, token, "/api/v1/identity/session-scope/admissions", "POST", "{}",
            tenant: SeedData.English.TenantId.Value);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(response)).GetProperty("actorId").GetString()
            .Should().Be(IdentityAuthorizationHttpProof.EnglishAdmin.Value.ToString("D"));
        var audit = (await proof.AuditAsync(response, SeedData.English)).Should().ContainSingle().Which;
        audit.Operation.Should().Be("identity.membership.admit_session_scope");
        audit.Outcome.Should().Be("success");
        audit.Metadata.Should().Contain("AdmitNativeSessionScope");
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.English.TenantId.Value);
            await using var resource = new NpgsqlCommand("""
                SELECT a.entity_id=m.id::text FROM public.audit_log a
                JOIN public.memberships m ON m.tenant_id=a.tenant_id AND m.user_id=a.actor_user_id
                WHERE a.correlation_id=@correlation AND a.operation='identity.membership.admit_session_scope'
                    AND m.organization_id IS NULL
                """, app, transaction);
            resource.Parameters.AddWithValue("correlation", audit.Correlation);
            (await resource.ExecuteScalarAsync()).Should().Be(true, "the MUST row names the actual admitted Membership");
        }
        using var account = await SendAsync(client, token, "/api/v1/account/site", tenant: SeedData.English.TenantId.Value);
        account.StatusCode.Should().Be(HttpStatusCode.OK);
        (await proof.AuditAsync(account, SeedData.English)).Should().BeEmpty();
        using var directory = await SendAsync(client, token, IdentityAuthorizationHttpProof.DirectoryPath,
            tenant: SeedData.English.TenantId.Value);
        directory.StatusCode.Should().Be(HttpStatusCode.OK);
        (await proof.AuditAsync(directory, SeedData.English)).Should().ContainSingle().Which.Operation.Should().Be("identity.membership.list");
        using var globalSelf = await SendAsync(client, token);
        globalSelf.StatusCode.Should().Be(HttpStatusCode.OK);
        (await proof.AuditAsync(globalSelf, SeedData.English)).Should().BeEmpty();
    }

    [Fact]
    public async Task Legacy_and_public_boundaries_remain_closed_when_native_scope_is_available()
    {
        await using var proof = await IdentityAuthorizationHttpProof.CreateAsync(parent);
        using var client = Client(proof.Database);
        var admin = proof.Token(IdentityAuthorizationHttpProof.EnglishAdmin, SeedData.English);
        using (var legacyPost = await SendAsync(client, admin,
            "/api/v1/identity/session-scope/admissions", "POST", "{}", native: null))
        {
            await TenantAuthenticationHttpTests.Problem(legacyPost, HttpStatusCode.NotFound, "not_found");
            (await legacyPost.Content.ReadAsStringAsync()).Should().NotContain("actorId").And.NotContain("subjectKey");
        }
        using (var publicRead = await SendAsync(client, admin, "/api/v1/public/site", native: null))
            publicRead.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var nativePublic = await SendAsync(client, admin, "/api/v1/public/site",
            tenant: SeedData.English.TenantId.Value))
            await TenantAuthenticationHttpTests.Problem(nativePublic, HttpStatusCode.NotFound, "not_found");
        using var foreign = await SendAsync(client,
            proof.Token(SeedData.IdentityUsers[2].UserId, SeedData.Yoga),
            "/api/v1/identity/session-scope/admissions", "POST", "{}", tenant: SeedData.English.TenantId.Value);
        await TenantAuthenticationHttpTests.Problem(foreign, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Tenant_wide_membership_can_admit_a_covered_native_branch_without_rewriting_provider_scope()
    {
        await using var proof = await IdentityAuthorizationHttpProof.CreateAsync(parent);
        using var payload = JsonDocument.Parse(proof.Issuer.Without("user_id", "tenant_id", "organization_id"));
        var claims = payload.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        claims["sub"] = JsonSerializer.SerializeToElement(IdentityPublicReadFixture.FoundationSubject(IdentityAuthorizationHttpProof.EnglishAdmin));
        var token = proof.Issuer.Token(JsonSerializer.Serialize(claims));
        using var client = Client(proof.Database);
        var branch = SeedData.English.DefaultOrganization.OrganizationId;
        using var admission = await SendAsync(client, token, "/api/v1/identity/session-scope/admissions", "POST", "{}",
            tenant: SeedData.English.TenantId.Value, organization: branch.Value);
        admission.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(admission)).GetProperty("organizationId").GetString().Should().Be(branch.Value.ToString("D"));
        (await proof.AuditAsync(admission, SeedData.English, branch)).Should().ContainSingle().Which.Operation
            .Should().Be("identity.membership.admit_session_scope");
        using var account = await SendAsync(client, token, "/api/v1/account/site",
            tenant: SeedData.English.TenantId.Value, organization: branch.Value);
        account.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(account)).GetProperty("organizationId").GetString().Should().Be(branch.Value.ToString("D"));
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string path = Self,
        string method = "GET", string? body = null, string? native = "configured", Guid? tenant = null,
        string? assertionHeader = null, Guid? organization = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Host = SeedData.English.Host;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(TrustedHopOptions.HostHeaderName, SeedData.English.Host);
        request.Headers.Add(TrustedHopOptions.SecretHeaderName, Hop);
        request.Headers.Add("X-LearnStack-Visitor-Address", "198.51.100.23");
        if (native is not null) request.Headers.Add(NativeSessionSourcePolicy.SecretHeader, native == "configured" ? Native : native);
        if (tenant is { } selected) request.Headers.Add(NativeSessionSourcePolicy.TenantHeader, selected.ToString("D"));
        if (organization is { } branch) request.Headers.Add(NativeSessionSourcePolicy.OrganizationHeader, branch.ToString("D"));
        if (assertionHeader is not null) request.Headers.Add(assertionHeader, SeedData.English.TenantId.Value.ToString("D"));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        TenantAuthenticationHttpTests.NoStore(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

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

    private sealed class CursorSink : ILogEventSink
    {
        private readonly ConcurrentQueue<string> _events = new();
        internal string[] Events => _events.ToArray();
        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class ExpiryClock(DateTimeOffset initial) : IClock
    {
        private int _expired;
        internal bool Expired => Volatile.Read(ref _expired) != 0;
        public DateTimeOffset UtcNow => initial.AddMinutes(Expired ? 3 : 0);
        internal void Expire() => Interlocked.Exchange(ref _expired, 1);
    }

    [Fact]
    public async Task Native_retirement_during_bootstrap_lookup_does_not_disclose_the_disabled_creation_policy()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        await using var database = await IdentityPublicReadFixture.CreateAsync(parent, issuer.Issuer);
        var clock = new ExpiryClock(DateTimeOffset.UtcNow);
        using var client = Client(database, services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IClock>(clock));
            services.AddScoped<UserBindingStore>();
            services.Replace(ServiceDescriptor.Scoped<IUserBindingStore>(provider =>
                new ExpiringBindingRead(provider.GetRequiredService<UserBindingStore>(), clock)));
        }, builder =>
        {
            builder.UseSetting("Identity:SelfBootstrap:Enabled", "false");
            builder.UseSetting("Identity:NativeSession:PreviousSecret", Previous);
            builder.UseSetting("Identity:NativeSession:PreviousRetiresAt", clock.UtcNow.AddMinutes(2).ToString("O",
                System.Globalization.CultureInfo.InvariantCulture));
        });
        var token = issuer.Token(issuer.Without("user_id", "tenant_id", "organization_id"));
        using var response = await SendAsync(client, token, Self + "/bootstrap", "POST", "{}", native: Previous);
        await TenantAuthenticationHttpTests.Problem(response, HttpStatusCode.Unauthorized, "unauthorized");
        clock.Expired.Should().BeTrue();
        response.Headers.Contains("Retry-After").Should().BeFalse();
    }

    private sealed class ExpiringBindingRead(UserBindingStore inner, ExpiryClock clock) : IUserBindingStore
    {
        public async Task<User?> FindExactAsync(GlobalIdentitySubject subject, CancellationToken cancellationToken = default)
        {
            var value = await inner.FindExactAsync(subject, cancellationToken);
            clock.Expire();
            return value;
        }
        public Task AddAsync(User aggregate, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The discovery retirement control admits no User creation.");
    }

    private sealed class ExpiringLabels(PreContextSelfDiscoveryScopeReader inner, ExpiryClock clock) : IOwnMembershipScopeLabels
    {
        public Task<OwnMembershipScopeLabel?> ReadAsync(OwnMembershipScopeWitness witness, CancellationToken cancellationToken = default)
        {
            clock.Expire();
            return inner.ReadAsync(witness, cancellationToken);
        }
    }
}
