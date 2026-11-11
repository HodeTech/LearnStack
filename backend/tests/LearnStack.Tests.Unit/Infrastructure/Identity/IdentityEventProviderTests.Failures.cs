using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identity;
using Polly;
using Polly.Timeout;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Identity;

public sealed partial class IdentityEventProviderTests
{
    [Theory]
    [InlineData("disabled")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("missing-kind")]
    [InlineData("non-numeric")]
    public async Task A_saved_source_policy_outside_the_qualified_contract_refuses_activation(string defect)
    {
        var policy = defect switch
        {
            "disabled" => Policy(enabled: false),
            "short" => Policy(retention: 86400),
            "long" => Policy(retention: 31 * 86400),
            "missing-kind" => Policy(codes: Codes.Where(code => code != "LOGIN")),
            _ => "{\"eventsEnabled\":true,\"eventsExpiration\":\"604800\",\"enabledEventTypes\":[]}",
        };
        var handler = new ScriptHandler((request, _) => Task.FromResult(Json(request.Method == HttpMethod.Post ? Token() : policy)));
        var provider = Create(handler);
        await using var lifetime = (IAsyncDisposable)provider;
        var error = await Assert.ThrowsAsync<IdentityEventProviderException>(() => provider.ReadPolicyAsync());
        error.Failure.Should().Be(IdentityEventProviderFailure.Policy);
    }

    [Fact]
    public async Task A_bad_record_does_not_prevent_a_valid_sibling_from_crossing_the_port()
    {
        var body = "[{\"type\":\"LOGIN\",\"id\":\"00000000-0000-0000-0000-000000000001\",\"realmId\":\"learnstack\",\"time\":\"not-number\"},"
            + JsonSerializer.Serialize(new { type = "LOGIN", id = Guid.NewGuid(), realmId = "learnstack", time = At.ToUnixTimeMilliseconds() }) + "]";
        var handler = new ScriptHandler((request, _) => Task.FromResult(Json(request.Method == HttpMethod.Post ? Token() : body)));
        var provider = Create(handler);
        await using var lifetime = (IAsyncDisposable)provider;
        var page = await provider.ReadAsync(new(At.AddMinutes(-1), At.AddMinutes(1), 0));
        page.Items.Select(item => item.Disposition).Should().Equal(IdentityEventItemDisposition.Malformed, IdentityEventItemDisposition.Fact);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    public async Task An_unreadable_page_is_an_explicit_recovery_signal_with_no_raw_body(string defect)
    {
        var body = defect == "malformed" ? "raw-response-privacy-canary" : new string('x', 1024 * 1024 + 1);
        var handler = new ScriptHandler((request, _) => Task.FromResult(Json(request.Method == HttpMethod.Post ? Token() : body)));
        var provider = Create(handler);
        await using var lifetime = (IAsyncDisposable)provider;
        var page = await provider.ReadAsync(new(At.AddMinutes(-1), At.AddMinutes(1), 0));
        page.Issue.Should().Be(defect == "malformed" ? IdentityEventPageIssue.Malformed : IdentityEventPageIssue.Oversized);
        page.Items.Should().BeEmpty();
        JsonSerializer.Serialize(page).Should().NotContain("privacy-canary");
    }

    [Fact]
    public async Task Redirects_are_not_followed_and_provider_error_text_does_not_escape()
    {
        var handler = new ScriptHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Post ? Json(Token())
            : new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://foreign.invalid/secret-redirect-canary") },
                Content = new StringContent("provider-body-error-canary", Encoding.UTF8, "application/json"),
            }));
        var provider = Create(handler);
        await using var lifetime = (IAsyncDisposable)provider;
        var error = await Assert.ThrowsAsync<IdentityEventProviderException>(() => provider.ReadPolicyAsync());
        error.ToString().Should().NotContain("secret-redirect-canary").And.NotContain("provider-body-error-canary");
        var origin = Issuer[..^"/realms/learnstack".Length];
        handler.Requests.Should().HaveCount(2).And.OnlyContain(request => request.Uri.StartsWith(origin, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Timeout_cancellation_and_shutdown_retain_the_actual_ignoring_HTTP_operation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ResiliencePipelineBuilder().AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = TimeSpan.FromMilliseconds(100),
        }).Build();
        var handler = new ScriptHandler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post) return Json(Token());
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            return Json(Policy());
        });
        var provider = Create(handler, pipeline: pipeline);
        var read = provider.ReadPolicyAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            read.IsCompleted.Should().BeFalse("logical timeout must not free the actual HTTP owner");
            var busy = await Assert.ThrowsAsync<IdentityEventProviderException>(() => provider.ReadPolicyAsync());
            busy.Failure.Should().Be(IdentityEventProviderFailure.Capacity);
            var disposal = ((IAsyncDisposable)provider).DisposeAsync().AsTask();
            disposal.IsCompleted.Should().BeFalse();
            release.TrySetResult();
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => read);
            (failure is IdentityEventProviderException or OperationCanceledException).Should().BeTrue();
            await disposal;
            handler.Requests.Should().HaveCount(2, "no overlapping retry or borrowed read may start");
        }
        finally
        {
            release.TrySetResult();
            await ((IAsyncDisposable)provider).DisposeAsync();
        }
    }
}
