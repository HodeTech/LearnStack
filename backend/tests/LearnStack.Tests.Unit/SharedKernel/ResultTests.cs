using FluentAssertions;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel;

public sealed class ResultTests
{
    [Fact]
    public void Ok_WhenGivenValue_ReturnsSuccessResult()
    {
        var result = Result<int>.Ok(42);

        result.IsSuccess.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.Value.Should().Be(42);
        result.Error.Should().BeNull();
        result.SuccessMessage.Should().BeNull();
    }

    [Fact]
    public void Ok_WithNullReferenceValue_Throws()
    {
        // Standards 09 § Forbidden bans Result<T> with IsSuccess=true and
        // Value=null; the factory must reject this at the call site.
        var act = () => Result<string>.Ok(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithMessage("*Result<None>*");
    }

    [Fact]
    public void Ok_WithNone_IsTheCanonicalPayloadlessSuccess()
    {
        var result = Result<None>.Ok(None.Value);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(None.Value);
    }

    [Fact]
    public void IsSuccess_ProvesValueNonNull_ToFlowAnalysis()
    {
        // The assertion is that this method COMPILES. Every dereference below is
        // written without `!` and without a #pragma; under <Nullable>enable</Nullable>
        // each one is a CS8602 unless [MemberNotNullWhen] is present, and CI builds
        // with TreatWarningsAsErrors. Delete the annotations on Result<T> and this
        // test stops building — which is the only way to test a compile-time
        // contract from inside the test suite.
        var result = Result<string>.Ok("payload");

        if (result.IsSuccess)
        {
            result.Value.Length.Should().Be(7);
        }

        if (result.IsFailure)
        {
            result.Error.Message.Key.Should().NotBeNull();
        }
    }

    [Fact]
    public void IsFailure_ProvesErrorNonNull_ThroughTheInterface()
    {
        // Same contract, reached through IResultBase. The annotations do not flow
        // from an interface to its implementations, so both carry their own copy;
        // this exercises the interface's.
        // CA1859 prefers the concrete type; the interface IS the contract under
        // test here, same as IResultBase_IsImplementedByResult below.
#pragma warning disable CA1859
        IResultBase result = Result<string>.Fail(
            new Error(LocalizedMessage.Of("lockey_not_found")));
#pragma warning restore CA1859

        if (result.IsFailure)
        {
            result.Error.Message.Key.Should().Be("lockey_not_found");
        }

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Ok_WithSuccessMessage_RetainsIt()
    {
        var message = LocalizedMessage.Of("lockey_course_published");

        var result = Result<int>.Ok(42, message);

        result.SuccessMessage.Should().Be(message);
    }

    [Fact]
    public void Fail_WhenGivenError_ReturnsFailureResult()
    {
        var error = new Error(LocalizedMessage.Of("lockey_business_rule_violation"));

        var result = Result<int>.Fail(error);

        result.IsSuccess.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Value.Should().Be(default);
        result.Error.Should().Be(error);
    }

    [Fact]
    public void FailFor_TResponseIsResultOfT_ReturnsConcreteResult()
    {
        // ADR-0032 § Sub-decision 3 shape: TResponse is Result<TValue>; the
        // factory MUST return Result<TValue>, NOT Result<Result<TValue>>.
        var error = new Error(LocalizedMessage.Of("lockey_validation_failed"));

        var result = Result.FailFor<Result<string>>(error);

        result.Should().BeOfType<Result<string>>();
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void FailFor_NonResultGeneric_Throws()
    {
        // FailFor is a pipeline helper; calling it with a non-Result<T>
        // type parameter is a coding bug and must fail loud rather than
        // silently producing an unexpected shape.
        var error = new Error(LocalizedMessage.Of("lockey_business_rule_violation"));

        var act = () => Result.FailFor<FakeResult>(error);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Result<T>*");
    }

    [Fact]
    public void IResultBase_IsImplementedByResult()
    {
        // The cast is the point of this test — pipeline behaviors operate
        // through the non-generic IResultBase surface. CA1859 would prefer
        // the concrete type for performance; suppressed locally because the
        // explicit interface contract is what we are asserting.
#pragma warning disable CA1859
        IResultBase result = Result<int>.Ok(7);
#pragma warning restore CA1859

        result.IsSuccess.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    // Helper type used by FailFor_NonResultGeneric_Throws.
    private sealed record FakeResult(bool IsSuccess = true) : IResultBase
    {
        public bool IsFailure => !IsSuccess;

        public LocalizedMessage? SuccessMessage => null;

        public Error? Error => null;
    }
}
