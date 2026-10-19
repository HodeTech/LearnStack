using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobPreparedPlanTests
{
    [Fact]
    public void Exact_successful_typed_result_captures_canonical_plan_and_independently_fixed_fingerprint()
    {
        var id = Guid.Parse("01234567-89ab-7cde-8f01-23456789abcd");
        var plan = new Plan(id);
        var captured = BackgroundJobPreparedPlan.Capture(Result<Plan>.Ok(plan), typeof(Plan), BackgroundJobId.From(id));
        captured.Json.Should().Be("{\"ArtifactId\":\"01234567-89ab-7cde-8f01-23456789abcd\"}");
        // Expected SHA/UUID values are independently generated with Python hashlib.
        captured.Fingerprint.Should().Be("2e6935f5d73a54a0e28e9f83bb12233f105cf1b7205e4ba5a176998e242c8737");
        captured.OperationId.Should().Be(Guid.Parse("cd8bf56f-1346-87c2-b418-c1414f1161e9"));
        typeof(Plan).GetProperty(nameof(Plan.ArtifactId))!.SetValue(plan, Guid.NewGuid());
        captured.Json.Should().Be("{\"ArtifactId\":\"01234567-89ab-7cde-8f01-23456789abcd\"}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_unrelated_success_result_or_failed_prepare_cannot_become_a_plan(bool failed)
    {
        IResultBase response = failed ? Result<Plan>.Fail(LearnStack.Application.Jobs.BackgroundJobExecutionAdmission.Refused)
            : Result<OtherPlan>.Ok(new(Guid.NewGuid()));
        Action capture = () => _ = BackgroundJobPreparedPlan.Capture(response, typeof(Plan), BackgroundJobId.From(Guid.CreateVersion7()));
        capture.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Invalid_plan_identifiers_are_refused_without_a_reflection_exception_wrapper()
    {
        Action capture = () => _ = BackgroundJobPreparedPlan.Capture(Result<Plan>.Ok(new(Guid.Empty)), typeof(Plan),
            BackgroundJobId.From(Guid.CreateVersion7()));
        capture.Should().Throw<ArgumentException>().Which.Should().NotBeOfType<System.Reflection.TargetInvocationException>();
    }

    public sealed record Plan(Guid ArtifactId) : IBackgroundJobPlan;
    public sealed record OtherPlan(Guid ArtifactId) : IBackgroundJobPlan;
}
