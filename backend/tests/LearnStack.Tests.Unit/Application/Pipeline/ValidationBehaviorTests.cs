using FluentAssertions;
using FluentValidation;
using LearnStack.Application.Pipeline;
using LearnStack.SharedKernel.Results;
using MediatR;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Pipeline;

/// <summary>
/// ValidationBehavior contract per ADR-0032 § Sub-decision 3 and Standards
/// 09 § Validation Errors. The behavior never throws
/// FluentValidation.ValidationException; it returns
/// Result.Fail(validation_failed, details) instead.
/// </summary>
public sealed class ValidationBehaviorTests
{
    public sealed record TestCommand(string Name) : IRequest<Result<string>>;

    private sealed class TestCommandValidator : AbstractValidator<TestCommand>
    {
        public TestCommandValidator()
        {
            RuleFor(c => c.Name)
                .NotEmpty()
                .WithErrorCode("lockey_name_required");
        }
    }

    [Fact]
    public async Task Returns_Result_Fail_When_Validation_Fails()
    {
        var behavior = new ValidationBehavior<TestCommand, Result<string>>(
            [new TestCommandValidator()]);

        RequestHandlerDelegate<Result<string>> next = () =>
            Task.FromResult(Result.Ok("should not reach"));

        var result = await behavior.Handle(new TestCommand(string.Empty), next, default);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("validation_failed");
        result.Error.Details.Should().NotBeNull();
        // FluentValidation keys properties in their original PascalCase shape;
        // ProblemDetailsFactory at the HTTP boundary projects to camelCase per
        // Standards 09 § Validation Errors.
        result.Error.Details!["Name"].Should().Contain(m => m.Key == "lockey_name_required");
    }

    [Fact]
    public async Task Calls_Inner_Handler_When_Validation_Succeeds()
    {
        var behavior = new ValidationBehavior<TestCommand, Result<string>>(
            [new TestCommandValidator()]);

        var called = false;
        RequestHandlerDelegate<Result<string>> next = () =>
        {
            called = true;
            return Task.FromResult(Result.Ok("ok"));
        };

        var result = await behavior.Handle(new TestCommand("alice"), next, default);

        called.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ok");
    }

    [Fact]
    public async Task Never_Throws_ValidationException()
    {
        // ADR-0032 § Sub-decision 3 binding: the pipeline never raises
        // FluentValidation.ValidationException; the architecture-test
        // catalogue entry ValidationBehavior_DoesNotThrow_ValidationException
        // cites this assertion.
        var behavior = new ValidationBehavior<TestCommand, Result<string>>(
            [new TestCommandValidator()]);

        RequestHandlerDelegate<Result<string>> next = () =>
            Task.FromResult(Result.Ok("unreachable"));

        var act = async () => await behavior.Handle(new TestCommand(string.Empty), next, default);

        await act.Should().NotThrowAsync<ValidationException>();
    }
}
