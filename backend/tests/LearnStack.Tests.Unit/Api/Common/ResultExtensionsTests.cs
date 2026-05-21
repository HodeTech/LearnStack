using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Common;

/// <summary>
/// ResultExtensions.ToActionResult contract per ADR-0032 § Sub-decision 6 —
/// the sanctioned shape every controller endpoint uses. The failure path
/// returns <see cref="ProblemDetailsActionResult"/>, which defers the
/// <see cref="ProblemDetails"/> body assembly until <c>ExecuteResultAsync</c>
/// runs; these tests verify both the immediate carry (Error + status) and
/// the executed body shape.
/// </summary>
public sealed class ResultExtensionsTests
{
    [Fact]
    public void Success_Maps_To_OkObjectResult()
    {
        var result = Result.Ok("payload");

        var action = result.ToActionResult();

        action.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().Be("payload");
    }

    [Fact]
    public void Failure_Returns_ProblemDetailsActionResult_With_StatusCode_From_Error()
    {
        var error = new Error(new LocalizedMessage("lockey_not_found"));
        var result = Result.Fail<string>(error);

        var action = result.ToActionResult();

        var pdar = action.Should().BeOfType<ProblemDetailsActionResult>().Which;
        pdar.Error.Should().BeSameAs(error);
        pdar.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task ExecuteResultAsync_Populates_ProblemDetails_From_HttpContext()
    {
        // ProblemDetailsActionResult builds the body lazily so the
        // sanctioned controller shape `(await Send(...)).ToActionResult()`
        // (no HttpContext argument) still populates instance + correlation
        // when ASP.NET invokes ExecuteResultAsync.
        var error = new Error(new LocalizedMessage("lockey_not_found"));
        var sut = new ProblemDetailsActionResult(error);

        var actionContext = BuildActionContext(path: "/v1/courses/abc");
        await sut.ExecuteResultAsync(actionContext);

        var body = actionContext.HttpContext.Items["WrittenBody"]
            .Should().BeOfType<ProblemDetails>().Which;
        body.Status.Should().Be(404);
        body.Instance.Should().Be("/v1/courses/abc");
        body.Extensions["code"].Should().Be("not_found");
        body.Extensions["messageKey"].Should().Be("lockey_not_found");
        body.Extensions.Should().ContainKey("correlationId");
    }

    private static ActionContext BuildActionContext(string path)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IActionResultExecutor<ObjectResult>, CapturingObjectResultExecutor>();
        services.AddSingleton(NullLoggerFactory.Instance);
        var sp = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = sp,
        };
        httpContext.Request.Path = new PathString(path);

        return new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());
    }

    private sealed class CapturingObjectResultExecutor : IActionResultExecutor<ObjectResult>
    {
        public Task ExecuteAsync(ActionContext context, ObjectResult result)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(result);
            context.HttpContext.Items["WrittenBody"] = result.Value;
            if (result.StatusCode is { } code)
            {
                context.HttpContext.Response.StatusCode = code;
            }
            return Task.CompletedTask;
        }
    }
}
