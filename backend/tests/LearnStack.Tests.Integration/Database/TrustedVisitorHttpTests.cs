using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TrustedVisitorHttpTests(PublicReadFixture fixture)
{
    private const string Secret = "test-only-trusted-visitor-secret-at-least-32";
    private const string Rotated = "test-only-second-rotation-secret-at-least-32";
    private const string Site = "/api/v1/public/site";

    [Fact]
    public async Task Trusted_socket_requests_keep_API_host_resolution_and_real_application_role_reads()
    {
        using var host = new SocketHost(fixture);
        var before = fixture.Observation.Reads;
        using var english = await Send(host, Site, "203.0.113.9", SeedData.English.Host);
        english.StatusCode.Should().Be(HttpStatusCode.OK);
        var first = await english.Content.ReadFromJsonAsync<JsonElement>();
        first.GetProperty("displayName").GetString().Should().Be(SeedData.English.DisplayName);
        using var yoga = await Send(host, Site, "203.0.113.10", SeedData.Yoga.Host, Rotated);
        yoga.StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await yoga.Content.ReadFromJsonAsync<JsonElement>();
        second.GetProperty("displayName").GetString().Should().Be(SeedData.Yoga.DisplayName);
        fixture.Observation.Reads.Should().Be(before + 2,
            "the production reader observer asserts READ ONLY and learnstack_app without BYPASSRLS");
        host.Lookups.Should().Be(2);
        english.Headers.CacheControl?.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Direct_and_hop_calls_share_one_canonical_IP_budget_across_hosts_and_header_spellings()
    {
        using var host = new SocketHost(fixture);
        for (var index = 0; index < RateLimitingExtensions.AnonymousPermitPerWindow; index++)
        {
            // The actual socket is 127.0.0.1; mapped spellings cannot buy a second quota.
            using var response = await Send(host, Site,
                index % 2 == 0 ? "::ffff:127.0.0.1" : "198.51.100.4",
                index % 2 == 0 ? SeedData.English.Host : SeedData.Yoga.Host,
                index % 2 == 0 ? Secret : null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        var lookups = host.Lookups;
        using var denied = await Send(host, Site, "0:0:0:0:0:ffff:7f00:1", "new-budget.example");
        await RateLimited(denied);
        host.Lookups.Should().Be(lookups, "the limiter precedes host classification");
    }

    [Fact]
    public async Task One_visitor_cannot_exhaust_another_or_multiply_quota_by_novel_hosts()
    {
        using var host = new SocketHost(fixture);
        for (var index = 0; index < RateLimitingExtensions.AnonymousPermitPerWindow; index++)
        {
            using var response = await Send(host, Site, "2001:db8::1",
                "novel-" + index.ToString(CultureInfo.InvariantCulture) + ".example");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        host.Lookups.Should().Be(RateLimitingExtensions.AnonymousPermitPerWindow);
        using var denied = await Send(host, Site, "2001:0DB8::0001", "another-novel.example");
        await RateLimited(denied);
        host.Lookups.Should().Be(RateLimitingExtensions.AnonymousPermitPerWindow);
        host.Factory.Services.GetRequiredService<UnknownHostCache>().Count.Should().Be(
            RateLimitingExtensions.AnonymousPermitPerWindow);
        using var independent = await Send(host, Site, "2001:db8::2", SeedData.Yoga.Host);
        independent.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("127.1")]
    [InlineData("2001:db8::1%eth0")]
    [InlineData("127.0.0.1, 203.0.113.9")]
    [InlineData("oversized")]
    public async Task Bad_authenticated_metadata_spends_fallback_quota_and_never_reaches_lookup(string? visitor)
    {
        using var host = new SocketHost(fixture);
        if (visitor == "oversized") visitor = new string('1', AnonymousRequestIdentity.MaximumAddressLength + 1);
        for (var index = 0; index < RateLimitingExtensions.AnonymousPermitPerWindow; index++)
        {
            using var refused = await Send(host, Site, visitor, SeedData.English.Host);
            refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
            refused.Headers.CacheControl?.NoStore.Should().BeTrue();
            var error = await refused.Content.ReadFromJsonAsync<JsonElement>();
            error.GetProperty("status").GetInt32().Should().Be(404);
        }
        using var denied = await Send(host, Site, visitor, "novel.example");
        await RateLimited(denied);
        host.Lookups.Should().Be(0);
        using var positive = await Send(host, Site, "203.0.113.9", SeedData.English.Host);
        positive.StatusCode.Should().Be(HttpStatusCode.OK, "valid visitor metadata has a separate quota");
    }

    [Fact]
    public async Task Repeated_raw_visitor_headers_are_refused_before_lookup()
    {
        using var host = new SocketHost(fixture);
        using var socket = new TcpClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(IPAddress.Loopback, host.Client.BaseAddress!.Port, deadline.Token);
        var request = $"GET {Site} HTTP/1.1\r\nHost: {SeedData.English.Host}\r\n"
            + $"{TrustedHopOptions.SecretHeaderName}: {Secret}\r\n"
            + $"{AnonymousRequestIdentity.VisitorHeaderName}: 203.0.113.9\r\n"
            + $"{AnonymousRequestIdentity.VisitorHeaderName}: 203.0.113.10\r\nConnection: close\r\n\r\n";
        await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request), deadline.Token);
        using var reader = new StreamReader(socket.GetStream());
        var response = await reader.ReadToEndAsync(deadline.Token);
        response.Should().StartWith("HTTP/1.1 404").And.Contain("no-store");
        host.Lookups.Should().Be(0);
        using var positive = await Send(host, Site, "203.0.113.9", SeedData.English.Host);
        positive.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("wrong-network")]
    [InlineData("repeated-secret")]
    [InlineData("unconfigured")]
    public async Task Untrusted_visitor_and_forwarding_headers_never_mint_partitions(string mode)
    {
        using var host = new SocketHost(fixture, mode);
        for (var index = 0; index <= RateLimitingExtensions.AnonymousPermitPerWindow; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
            request.Headers.TryAddWithoutValidation(TrustedHopOptions.SecretHeaderName,
                mode == "repeated-secret" ? [Secret, Secret] : [mode == "wrong-secret" ? "wrong" : Secret]);
            request.Headers.TryAddWithoutValidation(AnonymousRequestIdentity.VisitorHeaderName,
                "203.0.113." + (index + 1).ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100." + (index + 1).ToString(CultureInfo.InvariantCulture));
            using var response = await host.Client.SendAsync(request);
            response.StatusCode.Should().Be(index < RateLimitingExtensions.AnonymousPermitPerWindow
                ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests);
        }
    }

    [Fact]
    public async Task Physical_peer_ceiling_bounds_rotating_trusted_visitors_before_lookup()
    {
        using var host = new SocketHost(fixture);
        for (var index = 0; index < RateLimitingExtensions.PeerPermitPerWindow; index++)
        {
            var visitor = "203.0.113." + (index / RateLimitingExtensions.AnonymousPermitPerWindow + 1)
                .ToString(CultureInfo.InvariantCulture);
            using var response = await Send(host, "/healthz", visitor, SeedData.English.Host);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        using var denied = await Send(host, Site, "203.0.113.99", "novel.example");
        await RateLimited(denied);
        host.Lookups.Should().Be(0);
    }

    private static async Task<HttpResponseMessage> Send(SocketHost host, string path, string? visitor, string tenantHost, string? secret = Secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        // Direct controls use the real Host; hop controls use the trusted host field.
        request.Headers.Host = tenantHost;
        request.Headers.TryAddWithoutValidation(TrustedHopOptions.HostHeaderName, tenantHost);
        if (secret is not null) request.Headers.TryAddWithoutValidation(TrustedHopOptions.SecretHeaderName, secret);
        if (visitor is not null) request.Headers.TryAddWithoutValidation(AnonymousRequestIdentity.VisitorHeaderName, visitor);
        return await host.Client.SendAsync(request);
    }

    [Fact]
    public async Task Visitor_refusals_charge_the_peer_once_and_preserve_its_remaining_allowance()
    {
        using var host = new SocketHost(fixture);
        for (var index = 0; index < 330; index++)
        {
            using var response = await Send(host, "/healthz", "203.0.113.1", SeedData.English.Host);
            response.StatusCode.Should().Be(index < 60 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests);
        }
        // 330 actual requests have spent 330 peer permits, including 270 IP refusals.
        // Framework AttemptAcquire/AcquireAsync must not charge those refusals twice.
        for (var index = 0; index < 270; index++)
        {
            var visitor = "203.0.113." + (index / 60 + 2).ToString(CultureInfo.InvariantCulture);
            using var response = await Send(host, "/healthz", visitor, SeedData.English.Host);
            response.StatusCode.Should().Be(HttpStatusCode.OK, "request {0} is within the physical allowance", index + 331);
        }
        using var denied = await Send(host, Site, "203.0.113.99", "novel.example");
        await RateLimited(denied);
        host.Lookups.Should().Be(0);
    }

    private static async Task RateLimited(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter?.Delta.Should().BeGreaterThan(TimeSpan.Zero);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("status").GetInt32().Should().Be(429);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    private sealed class SocketHost : IDisposable
    {
        private readonly CountedResolver _resolver;
        public WebApplicationFactory<Program> Factory { get; }
        public HttpClient Client { get; }
        public int Lookups => _resolver.Lookups;

        public SocketHost(PublicReadFixture fixture, string mode = "configured")
        {
            Factory = fixture.WithWebHostBuilder(builder =>
            {
                if (mode != "unconfigured")
                {
                    builder.UseSetting("Tenancy:TrustedHop:Networks:0", mode == "wrong-network" ? "192.0.2.0/24" : "127.0.0.1/32");
                    builder.UseSetting("Tenancy:TrustedHop:Secrets:0", Secret);
                    builder.UseSetting("Tenancy:TrustedHop:Secrets:1", Rotated);
                }
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostToTenantResolver>();
                    services.RemoveAll<IHostResolutionInvalidator>();
                    services.AddSingleton<CachedHostToTenantResolver>();
                    services.AddSingleton<CountedResolver>();
                    services.AddSingleton<IHostToTenantResolver>(provider => provider.GetRequiredService<CountedResolver>());
                    services.AddSingleton<IHostResolutionInvalidator>(provider => provider.GetRequiredService<CachedHostToTenantResolver>());
                });
            });
            Factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var addresses = Factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            addresses.Should().NotBeNull();
            Client.BaseAddress = new Uri(addresses!.Addresses.Single());
            _resolver = Factory.Services.GetRequiredService<CountedResolver>();
        }

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private sealed class CountedResolver(CachedHostToTenantResolver inner) : IHostToTenantResolver
    {
        private int _lookups;
        public int Lookups => Volatile.Read(ref _lookups);
        public Task<HostResolution?> ResolveAsync(string host, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _lookups);
            return inner.ResolveAsync(host, cancellationToken);
        }
    }
}
