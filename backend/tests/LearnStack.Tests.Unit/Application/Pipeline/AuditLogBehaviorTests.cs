using FluentAssertions;
using LearnStack.Application.Pipeline;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Pipeline;

/// <summary>
/// AuditLogBehavior shell contract per ADR-0032 § Sub-decision 2 +
/// ADR-0032 § Sub-decision 2 pipeline behavior order. The shell catches handler
/// exceptions and rethrows via ExceptionDispatchInfo (preserving the
/// original stack); the audit-write itself is deferred to Packet 9 when
/// IAuditStore lights up.
/// </summary>
public sealed class AuditLogBehaviorTests
{
    public sealed record DummyCommand : IRequest<Result<string>>;

    [Fact]
    public async Task Passes_Through_Successful_Result()
    {
        var behavior = new AuditLogBehavior<DummyCommand, Result<string>>(
            NullLogger<AuditLogBehavior<DummyCommand, Result<string>>>.Instance);

        RequestHandlerDelegate<Result<string>> next = () =>
            Task.FromResult(Result.Ok("ok"));

        var result = await behavior.Handle(new DummyCommand(), next, default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ok");
    }

    [Fact]
    public async Task Rethrows_Exception_From_Inner_Handler_Preserving_Stack()
    {
        var behavior = new AuditLogBehavior<DummyCommand, Result<string>>(
            NullLogger<AuditLogBehavior<DummyCommand, Result<string>>>.Instance);

        var thrown = new InvalidOperationException("boom");
        RequestHandlerDelegate<Result<string>> next = () => throw thrown;

        var act = async () => await behavior.Handle(new DummyCommand(), next, default);

        // ExceptionDispatchInfo.Throw rethrows the original instance so
        // reference equality still holds — the rethrow does not box a new
        // wrapper exception.
        var caught = await act.Should().ThrowAsync<InvalidOperationException>();
        caught.Which.Should().BeSameAs(thrown);
    }
}
