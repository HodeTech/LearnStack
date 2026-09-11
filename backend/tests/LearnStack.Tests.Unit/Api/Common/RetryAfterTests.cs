using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Observability;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Common;

/// <summary>
/// Every 503 carries <c>Retry-After</c>, on both Problem Details paths.
/// </summary>
/// <remarks>
/// API Design § Status Codes requires it, Error Handling said Packet 9 set it, and none of
/// the three 503 responses carried it (the fifth review of Packet 9). One case per path,
/// because the paths share the helper and not the call site — and one that is not a 503, so
/// the header is not simply stamped on everything.
/// </remarks>
public sealed class RetryAfterTests
{
    [Fact]
    public async Task A_503_result_carries_Retry_After()
    {
        var context = ActionContext();

        await new ProblemDetailsActionResult(
            new Error(new LocalizedMessage("lockey_dependency_unavailable"))).ExecuteResultAsync(context);

        context.HttpContext.Response.Headers.RetryAfter.ToString()
            .Should().Be(RetryAfter.ServiceUnavailableSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_result_that_is_not_a_503_carries_none()
    {
        var context = ActionContext();

        await new ProblemDetailsActionResult(
            new Error(new LocalizedMessage("lockey_not_found"))).ExecuteResultAsync(context);

        context.HttpContext.Response.Headers.ContainsKey("Retry-After").Should().BeFalse();
    }

    [Fact]
    public async Task A_503_exception_carries_Retry_After()
    {
        // audit_unavailable arrives as an exception, not a Result — the other path.
        var handler = new LearnStackExceptionHandler(
            new SilentErrorTracker(),
            new NoTenantAccessor(),
            NullLogger<LearnStackExceptionHandler>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        await handler.TryHandleAsync(
            httpContext, new AuditWriteFailedException("the MUST row could not be written"), default);

        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        httpContext.Response.Headers.RetryAfter.ToString()
            .Should().Be(RetryAfter.ServiceUnavailableSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static ActionContext ActionContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IActionResultExecutor<ObjectResult>, NoopExecutor>();

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

        return new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
    }

    private sealed class NoopExecutor : IActionResultExecutor<ObjectResult>
    {
        public Task ExecuteAsync(ActionContext context, ObjectResult result) => Task.CompletedTask;
    }

    private sealed class SilentErrorTracker : IErrorTrackingProvider
    {
        public ValueTask CaptureAsync(
            Exception exception, CapturedContext context, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class NoTenantAccessor : ITenantContextAccessor
    {
        public ITenantContext? Current { get; set; }
    }
}
