using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed class TenantIdentityBindingAdmissionTests
{
    private static readonly UserId Actor = UserId.From(Guid.Parse("01930000-0000-7000-8000-00000000ab01"));
    private static VerifiedProviderSubject Subject(UserId? claimed = null) => new(
        "https://issuer.example/realms/learnstack", "private-subject-canary", claimed,
        TenantId.From(Guid.Parse("01930000-0000-7000-8000-00000000ab02")), null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_existing_binding_normalizes_the_stored_actor_after_subject_admission(bool claimed)
    {
        var subject = Subject(claimed ? Actor : null);
        var http = Request(subject);
        var logger = new Observation();
        var dispatched = 0;
        var resolver = new Resolver((actual, token) =>
        {
            actual.Should().BeSameAs(subject);
            token.Should().Be(http.RequestAborted);
            http.Features.Get<SubjectAdmissionDecision>()!.IsAcquired.Should().BeTrue();
            http.Features.Get<TenantCredentialFeature>()!.Identity.Should().BeNull();
            return Task.FromResult(IdentityBindingResult.Bound(Actor, created: false));
        });
        var binding = new TenantIdentityBindingAdmissionMiddleware(_ => { dispatched++; return Task.CompletedTask; }, logger);
        await using var limiter = new TenantSubjectRateLimiter();
        var quota = new TenantSubjectAdmissionMiddleware(context => binding.InvokeAsync(context, resolver), limiter);

        await quota.InvokeAsync(http);

        dispatched.Should().Be(1);
        resolver.Calls.Should().Be(1);
        http.Features.Get<TenantCredentialFeature>()!.Identity.Should().BeEquivalentTo(
            new ValidatedTenantIdentity(subject.Issuer, subject.Subject, Actor, subject.TenantId, subject.OrganizationId));
        logger.Reasons.Should().BeEmpty();
    }

    [Theory]
    [InlineData(IdentityBindingOutcome.NotFound)]
    [InlineData(IdentityBindingOutcome.NonAdmitting)]
    [InlineData(IdentityBindingOutcome.Conflict)]
    [InlineData(IdentityBindingOutcome.AdmissionDenied)]
    [InlineData(IdentityBindingOutcome.AuthorityUnavailable)]
    [InlineData(IdentityBindingOutcome.StorageUnavailable)]
    [InlineData(IdentityBindingOutcome.Indeterminate)]
    [InlineData(IdentityBindingOutcome.Created)]
    public async Task All_nonexisting_outcomes_refuse_uniformly_and_never_preserve_an_actor(IdentityBindingOutcome outcome)
    {
        var http = AdmittedRequest();
        var logger = new Observation();
        var resolver = new Resolver((_, _) => Task.FromResult(outcome == IdentityBindingOutcome.Created
            ? IdentityBindingResult.Bound(Actor, created: true) : IdentityBindingResult.Refused(outcome)));
        var middleware = new TenantIdentityBindingAdmissionMiddleware(
            _ => throw new InvalidOperationException("A refused binding cannot dispatch."), logger);

        await middleware.InvokeAsync(http, resolver);

        AssertRefused(http);
        if (outcome is IdentityBindingOutcome.AuthorityUnavailable or IdentityBindingOutcome.StorageUnavailable)
            logger.Reasons.Should().ContainSingle().Which.Should().Be("binding_store_unavailable");
        else logger.Reasons.Should().BeEmpty();
    }

    [Fact]
    public async Task Reentry_rechecks_the_binding_without_spending_another_subject_permit_or_reusing_the_actor()
    {
        var http = Request(Subject());
        var resolver = new Resolver((_, _) => Task.FromResult(IdentityBindingResult.Refused(IdentityBindingOutcome.NotFound)));
        var next = 0;
        var binding = new TenantIdentityBindingAdmissionMiddleware(_ => { next++; return Task.CompletedTask; }, new Observation());
        await using var limiter = new TenantSubjectRateLimiter(() => TenantSubjectRateLimiter.CreateBudget(permitLimit: 1),
            TenantSubjectRateLimiter.Window, TimeProvider.System);
        var quota = new TenantSubjectAdmissionMiddleware(context => binding.InvokeAsync(context, resolver), limiter);
        resolver.Response = (_, _) => Task.FromResult(IdentityBindingResult.Bound(Actor, created: false));
        await quota.InvokeAsync(http);
        http.Features.Get<TenantCredentialFeature>()!.Identity.Should().NotBeNull();
        resolver.Response = (_, _) => Task.FromResult(IdentityBindingResult.Refused(IdentityBindingOutcome.NonAdmitting));

        await quota.InvokeAsync(http);

        resolver.Calls.Should().Be(2);
        next.Should().Be(1);
        AssertRefused(http);
        limiter.AttemptAcquire(Subject()).Outcome.Should().Be(SubjectAdmissionOutcome.QuotaExhausted);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("subject")]
    [InlineData("actor")]
    [InlineData("scope")]
    public async Task A_cached_quota_decision_cannot_admit_replaced_or_missing_verified_evidence(string mutation)
    {
        var subject = Subject();
        var http = Request(subject);
        var resolver = new Resolver((_, _) => Task.FromResult(IdentityBindingResult.Bound(Actor, created: false)));
        var binding = new TenantIdentityBindingAdmissionMiddleware(_ => Task.CompletedTask, new Observation());
        await using var limiter = new TenantSubjectRateLimiter();
        var quota = new TenantSubjectAdmissionMiddleware(context => binding.InvokeAsync(context, resolver), limiter);
        await quota.InvokeAsync(http);
        http.Features.Get<TenantCredentialFeature>()!.VerifiedSubject = mutation switch
        {
            "missing" => null,
            "subject" => subject with { Subject = "other-private-subject" },
            "actor" => subject with { ClaimedActor = Actor },
            _ => subject with { TenantId = null }
        };

        await quota.InvokeAsync(http);

        resolver.Calls.Should().Be(1);
        AssertRefused(http);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_cached_quota_state_refuses_without_another_debit_or_binding_read(bool removeDecision)
    {
        var http = AdmittedRequest();
        var original = http.Features.Get<TenantCredentialFeature>()!.VerifiedSubject!;
        if (removeDecision) http.Features.Set<SubjectAdmissionDecision>(null);
        else http.Features.Set<TenantSubjectAdmissionProvenance>(null);
        var resolver = new Resolver((_, _) => throw new InvalidOperationException("No binding read is admitted."));
        var binding = new TenantIdentityBindingAdmissionMiddleware(_ => Task.CompletedTask, new Observation());
        await using var limiter = new TenantSubjectRateLimiter(() => TenantSubjectRateLimiter.CreateBudget(permitLimit: 1),
            TenantSubjectRateLimiter.Window, TimeProvider.System);
        var quota = new TenantSubjectAdmissionMiddleware(context => binding.InvokeAsync(context, resolver), limiter);

        await quota.InvokeAsync(http);

        AssertRefused(http);
        resolver.Calls.Should().Be(0);
        limiter.AttemptAcquire(original).IsAcquired.Should().BeTrue("partial request-local state cannot spend a second permit");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Anonymous_and_unclassified_requests_do_no_binding_work(bool classified)
    {
        var http = Request(classified ? null : Subject());
        if (!classified) http.Features.Set<HostClassification>(null);
        var dispatched = 0;
        var resolver = new Resolver((_, _) => throw new InvalidOperationException("No binding lookup is admitted."));
        var middleware = new TenantIdentityBindingAdmissionMiddleware(_ => { dispatched++; return Task.CompletedTask; }, new Observation());

        await middleware.InvokeAsync(http, resolver);

        dispatched.Should().Be(1);
        resolver.Calls.Should().Be(0);
        http.Features.Get<TenantCredentialFeature>()!.Identity.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expected_store_failures_emit_only_the_closed_reason(bool timeout)
    {
        var http = AdmittedRequest();
        var logger = new Observation();
        Exception failure = timeout ? new TimeoutException("private-store-canary") : new NpgsqlException("private-store-canary");
        var resolver = new Resolver((_, _) => Task.FromException<IdentityBindingResult>(failure));
        var middleware = new TenantIdentityBindingAdmissionMiddleware(_ => Task.CompletedTask, logger);

        await middleware.InvokeAsync(http, resolver);

        AssertRefused(http);
        logger.Reasons.Should().Equal("binding_store_unavailable");
        logger.Exceptions.Should().OnlyContain(exception => exception == null);
    }

    [Fact]
    public async Task Caller_cancellation_and_unexpected_errors_remain_their_original_outcome()
    {
        var http = AdmittedRequest();
        var logger = new Observation();
        using var cancellation = new CancellationTokenSource();
        http.RequestAborted = cancellation.Token;
        cancellation.Cancel();
        var resolver = new Resolver((_, token) => Task.FromCanceled<IdentityBindingResult>(token));
        var middleware = new TenantIdentityBindingAdmissionMiddleware(_ => Task.CompletedTask, logger);
        var abort = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middleware.InvokeAsync(http, resolver));
        abort.CancellationToken.Should().Be(cancellation.Token);
        http.Features.Get<TenantCredentialFeature>()!.Identity.Should().BeNull();

        http.RequestAborted = CancellationToken.None;
        var failure = new InvalidOperationException("unexpected-private-canary");
        resolver.Response = (_, _) => Task.FromException<IdentityBindingResult>(failure);
        (await Record.ExceptionAsync(() => middleware.InvokeAsync(http, resolver))).Should().BeSameAs(failure);
        http.Features.Get<TenantCredentialFeature>()!.Identity.Should().BeNull();
        logger.Reasons.Should().BeEmpty();
    }

    private static DefaultHttpContext AdmittedRequest()
    {
        var subject = Subject();
        var http = Request(subject);
        http.Features.Set(new TenantSubjectAdmissionProvenance(subject));
        http.Features.Set(new SubjectAdmissionDecision(SubjectAdmissionOutcome.Acquired, null));
        return http;
    }

    private static DefaultHttpContext Request(VerifiedProviderSubject? subject)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        http.Features.Set(HostClassification.Platform("platform.example"));
        http.Features.Set(new TenantCredentialFeature
        {
            VerifiedSubject = subject,
            Identity = new ValidatedTenantIdentity("stale-issuer", "stale-subject", Actor, null, null)
        });
        return http;
    }

    private static void AssertRefused(HttpContext http)
    {
        http.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        http.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer");
        http.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
        http.Response.Body.Length.Should().Be(0);
        http.Features.Get<TenantCredentialFeature>()!.Identity.Should().BeNull();
    }

    private sealed class Resolver(Func<VerifiedProviderSubject, CancellationToken, Task<IdentityBindingResult>> response)
        : ITenantIdentityBindingResolver
    {
        internal int Calls { get; private set; }
        internal Func<VerifiedProviderSubject, CancellationToken, Task<IdentityBindingResult>> Response { get; set; } = response;
        public Task<IdentityBindingResult> ResolveExistingAsync(VerifiedProviderSubject subject, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Response(subject, cancellationToken);
        }
    }

    private sealed class Observation : ILogger<TenantIdentityBindingAdmissionMiddleware>
    {
        internal List<string> Reasons { get; } = [];
        internal List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Exceptions.Add(exception);
            Reasons.Add(((IEnumerable<KeyValuePair<string, object?>>)state!).Single(pair => pair.Key == "Reason").Value!.ToString()!);
        }
    }
}
