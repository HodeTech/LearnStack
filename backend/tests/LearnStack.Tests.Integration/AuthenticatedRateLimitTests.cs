using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>The actual production chain and durable recorder, below both unchanged ingress floors.</summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class AuthenticatedRateLimitTests(PublicReadFixture fixture)
{
    [Fact]
    public async Task Authenticated_Rate_Limits_Use_Validated_Subject()
    {
        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        var observed = new SubjectQuotaObservation();
        using var client = fixture.ClientFor(SeedData.English.Host, services =>
        {
            AddQuotaBindings(services, issuer);
            services.RemoveAll<TenantSubjectRateLimiter>();
            services.AddSingleton(_ => new TenantSubjectRateLimiter(
                () => TenantSubjectRateLimiter.CreateBudget(permitLimit: 3),
                TenantSubjectRateLimiter.Window, TimeProvider.System));
            // Observe calls while retaining the real scoped auditing decorator/store.
            services.AddScoped<AuditingTenantAssertionRecorder>();
            services.AddScoped<ITenantAssertionRecorder>(provider => new ObservedQuotaRecorder(
                provider.GetRequiredService<AuditingTenantAssertionRecorder>(), observed));
            services.AddSingleton<IStartupFilter>(observed);
            services.AddSingleton<ILogEventSink>(observed);
        }, builder =>
        {
            builder.UseSetting(TenantAuthenticationOptions.IssuerConfigurationKey, issuer.Issuer);
            builder.UseSetting("Serilog:MinimumLevel:Default", "Debug");
        });
        var reads = fixture.Observation.Reads;
        var audits = await fixture.AuditCountAsync(SeedData.English);
        for (var index = 0; index < 3; index++)
        {
            using var admitted = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
                token: AccountSiteHttpTests.Token(issuer, SeedData.English,
                    claimedTenant: SeedData.Yoga.TenantId.Value, tokenId: "rotated-" + index.ToString(CultureInfo.InvariantCulture)));
            await TenantAuthenticationHttpTests.Problem(admitted, HttpStatusCode.NotFound, "tenant_mismatch");
            var correlation = TenantAuthenticationReconciliationTests.Correlation(admitted);
            AccountHttpAudit.Assert((await AccountHttpAudit.Read(fixture, SeedData.English, correlation))
                .Should().ContainSingle().Subject, SeedData.English, correlation, "jwt-claim", null,
                "assertedTenantId", SeedData.Yoga.TenantId.Value);
        }
        observed.Rejections.Count.Should().Be(3, "each admitted signed mismatch invokes the real recorder once");
        (await fixture.AuditCountAsync(SeedData.English)).Should().Be(audits + 3);

        // Same validated issuer/sub, with changed jti, actor, host and tenant/org selectors.
        using var refused = await TenantAuthenticationHttpRequest.Send(client, SeedData.Yoga.Host,
            token: AccountSiteHttpTests.Token(issuer, SeedData.Yoga, organization: true,
                tokenId: "fresh-token", actor: Guid.Parse("d63c55bb-38ca-4308-ad1a-d4f36997b497")));
        await TenantAuthenticationHttpTests.Problem(refused, HttpStatusCode.TooManyRequests, "rate_limited");
        (refused.Headers.RetryAfter?.Delta).Should().BeGreaterThan(TimeSpan.Zero);
        observed.Rejections.Count.Should().Be(3);
        fixture.Observation.Reads.Should().Be(reads, "subject refusal runs before the actual account reader");

        using var claimless = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
            TenantAuthenticationHttpRequest.PublicPath, issuer.Token(issuer.Without("tenant_id", "organization_id")));
        await TenantAuthenticationHttpTests.Problem(claimless, HttpStatusCode.TooManyRequests, "rate_limited");
        observed.ClaimlessEvents.Should().Be(0, "the fixed Debug event follows subject admission");
        observed.Rejections.Count.Should().Be(3, "quota refusal stops the actual recorder before durable store I/O");
        (await fixture.AuditCountAsync(SeedData.English)).Should().Be(audits + 3);

        using var subjectB = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
            token: AccountSiteHttpTests.Token(issuer, SeedData.English, subject: "independent-subject-B"));
        subjectB.StatusCode.Should().Be(HttpStatusCode.OK,
            "B uses the same physical peer/visitor and fewer than 60 requests have crossed the actual chain");
        TenantAuthenticationHttpTests.NoStore(subjectB);
        using var anonymous = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
            TenantAuthenticationHttpRequest.PublicPath);
        anonymous.StatusCode.Should().Be(HttpStatusCode.OK);
        observed.PhysicalPeers.Should().HaveCount(7).And.OnlyContain(peer => peer.Equals(IPAddress.Loopback));
        fixture.Observation.Reads.Should().Be(reads + 2);
        observed.Rejections.Count.Should().Be(3);
    }

    [Fact]
    public async Task Production_visitor_quota_blocks_validated_subjects_with_replenishment_disabled()
    {
        using var production = NoQueueAdmissionLimiter.CreateVisitorBudget();
        RateLimitingExtensions.AnonymousPermitPerWindow.Should().Be(60);
        RateLimitingExtensions.Window.Should().Be(TimeSpan.FromMinutes(1));
        production.GetStatistics()!.CurrentAvailablePermits.Should().Be(60);
        production.ReplenishmentPeriod.Should().Be(TimeSpan.FromMinutes(1));
        production.IsAutoReplenishing.Should().BeTrue();

        await using var issuer = await TenantAuthenticationIssuer.StartAsync();
        using var client = fixture.ClientFor(SeedData.English.Host, services =>
        {
            AddQuotaBindings(services, issuer);
            // Keep the production coordinator, quota and window. Disable only
            // its visitor timer so slow DB-backed HTTP calls cannot replenish
            // this proof's spent quota. Above asserts the stock automatic-timer
            // configuration; this HTTP lane does not observe replenishment.
            services.RemoveAll<NoQueueAdmissionLimiter>();
            services.AddSingleton(_ => new NoQueueAdmissionLimiter(
                RateLimitingExtensions.CreatePeerBudget(),
                () => NoQueueAdmissionLimiter.CreateVisitorBudget(autoReplenishment: false),
                RateLimitingExtensions.Window, TimeProvider.System));
        }, builder =>
            builder.UseSetting(TenantAuthenticationOptions.IssuerConfigurationKey, issuer.Issuer));
        var token = AccountSiteHttpTests.Token(issuer, SeedData.English);
        var reads = fixture.Observation.Reads;
        for (var index = 0; index < RateLimitingExtensions.AnonymousPermitPerWindow; index++)
        {
            using var allowed = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host, token: token);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK, "request {0} is below the unchanged visitor floor", index + 1);
        }
        using var rejected = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
            token: AccountSiteHttpTests.Token(issuer, SeedData.English, subject: "fresh-subject"));
        await TenantAuthenticationHttpTests.Problem(rejected, HttpStatusCode.TooManyRequests, "rate_limited");
        (rejected.Headers.RetryAfter?.Delta).Should().BeGreaterThan(TimeSpan.Zero);
        fixture.Observation.Reads.Should().Be(reads + RateLimitingExtensions.AnonymousPermitPerWindow);
    }

    private static void AddQuotaBindings(IServiceCollection services, TenantAuthenticationIssuer issuer)
    {
        services.AddSingleton<ITenantIdentityBindingResolver>(new FixtureTenantIdentityBindingResolver(issuer.Issuer,
            ["opaque-keycloak-subject", "independent-subject-B", "fresh-subject", "admission-subject-A",
                "admission-subject-B", "admission-subject-C", "blocked-retiring-subject",
                "blocked-retained-subject", "blocked-competing-subject"]));
        // These admission proofs own fixture bindings outside Identity. Substitute
        // only their bounded live-coverage read; actual HTTP authority is exercised
        // separately against committed Membership rows in IdentityAuthorizationHttpTests.
        services.AddSingleton<IIdentityAuthorizationReader, AdmissionAuthorizationReader>();
    }

    private sealed class AdmissionAuthorizationReader : IIdentityAuthorizationReader
    {
        public Task<IdentityAuthorizationResult> ReadAsync(AuthorizationCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var covered = candidate.IsActive && candidate.Actor.Value == TenantAuthenticationIssuer.Actor
                && candidate.OrganizationId is null
                && (candidate.TenantId == SeedData.English.TenantId || candidate.TenantId == SeedData.Yoga.TenantId);
            return Task.FromResult(new IdentityAuthorizationResult(covered ? IdentityAuthorizationOutcome.Covered
                    : IdentityAuthorizationOutcome.NotCovered, candidate.Actor, candidate.TenantId, candidate.OrganizationId,
                covered ? [new AuthorizationMembershipFacts(Guid.Parse("019b24b6-0cf0-7000-8000-0000000000a1"), null, [])] : []));
        }
    }

    private sealed class SubjectQuotaObservation : IStartupFilter, ILogEventSink
    {
        internal IServiceProvider Services { get; private set; } = null!;
        internal ConcurrentQueue<IPAddress> PhysicalPeers { get; } = new();
        internal ConcurrentQueue<TenantAssertionRejection> Rejections { get; } = new();
        private int _claimlessEvents;
        internal int ClaimlessEvents => Volatile.Read(ref _claimlessEvents);
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            Services = app.ApplicationServices;
            app.Use((context, continuation) =>
            {
                // TestServer owns no socket. Supply only its physical-peer feature;
                // both production ingress budgets see this same peer on A and B.
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                PhysicalPeers.Enqueue(context.Connection.RemoteIpAddress!);
                return continuation(context);
            });
            next(app);
        };
        public void Emit(LogEvent logEvent)
        {
            if (logEvent.MessageTemplate.Text == "Tenant authentication outcome tenant_claim_absent.")
                Interlocked.Increment(ref _claimlessEvents);
        }
    }

    private sealed class ObservedQuotaRecorder(ITenantAssertionRecorder inner, SubjectQuotaObservation observed)
        : ITenantAssertionRecorder
    {
        public Task RecordRejectionAsync(TenantAssertionRejection rejection)
        {
            observed.Rejections.Enqueue(rejection);
            return inner.RecordRejectionAsync(rejection);
        }
        public void RecordUnresolved(TenantAssertionDimension dimension) => inner.RecordUnresolved(dimension);
        public void RecordNoTenantRefusal(TenantNoTenantRefusal refusal) => inner.RecordNoTenantRefusal(refusal);
    }
}
