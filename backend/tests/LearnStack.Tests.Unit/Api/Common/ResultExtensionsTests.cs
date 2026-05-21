using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Common;

/// <summary>
/// ResultExtensions.ToActionResult contract per ADR-0032 § Sub-decision 6 —
/// the sanctioned shape every controller endpoint uses.
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
    public void Failure_Maps_To_ProblemDetails_With_Correct_Status()
    {
        var error = new Error(new LocalizedMessage("lockey_not_found"));
        var result = Result.Fail<string>(error);

        var action = result.ToActionResult();

        var objectResult = action.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(404);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
        var problem = (ProblemDetails)objectResult.Value!;
        problem.Status.Should().Be(404);
        problem.Extensions["code"].Should().Be("not_found");
        problem.Extensions["messageKey"].Should().Be("lockey_not_found");
    }
}
