using FluentAssertions;
using LearnStack.Application.Pipeline;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Pipeline;

/// <summary>
/// TenantContextBehavior shell contract — short-circuits with
/// Result.Fail(tenant_mismatch) when the resolution stage has not
/// populated ITenantContext. Until Packet 7 lands the resolver this is the
/// loud-fail guard for any handler executed without context.
/// </summary>
public sealed class TenantContextBehaviorTests
{
    public sealed record DummyCommand : IRequest<Result<string>>;

    [Fact]
    public async Task Short_Circuits_When_Context_Unresolved()
    {
        var behavior = new TenantContextBehavior<DummyCommand, Result<string>>(
            UnresolvedTenantContext.Instance);

        var called = false;
        RequestHandlerDelegate<Result<string>> next = () =>
        {
            called = true;
            return Task.FromResult(Result.Ok("should not run"));
        };

        var result = await behavior.Handle(new DummyCommand(), next, default);

        called.Should().BeFalse();
        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("tenant_mismatch");
    }
}
