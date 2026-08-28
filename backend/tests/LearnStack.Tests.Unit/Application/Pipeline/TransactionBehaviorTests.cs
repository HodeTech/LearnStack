using System.Data.Common;
using FluentAssertions;
using LearnStack.Application.Pipeline;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Pipeline;

/// <summary>
/// <c>TransactionBehavior</c> — step 6. The commit boundary: what it calls, in
/// what order, and which outcome resolves the transaction which way.
/// </summary>
/// <remarks>
/// Against a recording <c>IUnitOfWork</c> rather than a database, because the
/// question here is the protocol. Whether the protocol produces the right rows in
/// PostgreSQL is <c>UnitOfWorkTests</c>, which runs against a real one.
/// </remarks>
public sealed class TransactionBehaviorTests
{
    public sealed record DummyCommand : IRequest<Result<string>>;

    [Fact]
    public async Task Opens_The_Transaction_Then_Sets_The_Session_Variables_Then_Runs_The_Handler()
    {
        // The order is the assertion. SET LOCAL is transaction-local, so issuing
        // it before the BEGIN discards it; issuing it after the handler protects
        // nothing the handler did.
        var unitOfWork = new RecordingUnitOfWork();
        var behavior = Build(unitOfWork);

        var result = await behavior.Handle(
            new DummyCommand(), () => Next(unitOfWork, Result.Ok("ok")), default);

        result.IsSuccess.Should().BeTrue();
        unitOfWork.Calls.Should().Equal("begin", "set-tenant", "handler", "commit");
    }

    [Fact]
    public async Task Rolls_Back_A_Failure_Result()
    {
        // A business-rule violation is a fail-Result, not an exception
        // (ADR-0032 § Sub-decision 4) — and it must still not commit: the handler
        // may have written before deciding it could not finish.
        var unitOfWork = new RecordingUnitOfWork();
        var behavior = Build(unitOfWork);

        var failure = Result.FailFor<Result<string>>(
            new Error(new LocalizedMessage("lockey_business_rule_violation")));

        var result = await behavior.Handle(
            new DummyCommand(), () => Next(unitOfWork, failure), default);

        result.IsFailure.Should().BeTrue();
        unitOfWork.Calls.Should().Equal("begin", "set-tenant", "handler", "rollback");
    }

    [Fact]
    public async Task Rolls_Back_And_Rethrows_An_Exception()
    {
        // Rethrown rather than converted: AuditLogBehavior one frame out catches
        // it, audits the failure and rethrows through ExceptionDispatchInfo, and
        // the L1 IExceptionHandler turns it into Problem Details.
        var unitOfWork = new RecordingUnitOfWork();
        var behavior = Build(unitOfWork);

        var act = async () => await behavior.Handle(
            new DummyCommand(),
            () =>
            {
                unitOfWork.Calls.Add("handler");
                throw new InvalidOperationException("handler blew up");
            },
            default);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("handler blew up");
        unitOfWork.Calls.Should().Equal("begin", "set-tenant", "handler", "rollback");
    }

    [Fact]
    public async Task Passes_The_Ambient_Tenant_Context_Through()
    {
        // Not a context of its own. Whatever the resolution stage populated is
        // what reaches SET LOCAL — which, until Packet 7, is
        // UnresolvedTenantContext, and that is correct and fail-closed.
        var unitOfWork = new RecordingUnitOfWork();
        var behavior = Build(unitOfWork);

        await behavior.Handle(new DummyCommand(), () => Next(unitOfWork, Result.Ok("ok")), default);

        unitOfWork.TenantContext.Should().BeSameAs(UnresolvedTenantContext.Instance);
    }

    private static TransactionBehavior<DummyCommand, Result<string>> Build(IUnitOfWork unitOfWork) =>
        new(unitOfWork, UnresolvedTenantContext.Instance);

    private static Task<Result<string>> Next(RecordingUnitOfWork unitOfWork, Result<string> result)
    {
        unitOfWork.Calls.Add("handler");
        return Task.FromResult(result);
    }

    /// <summary>An <see cref="IUnitOfWork"/> that records the protocol.</summary>
    private sealed class RecordingUnitOfWork : IUnitOfWork, IUnitOfWorkScope
    {
        public List<string> Calls { get; } = [];

        public ITenantContext? TenantContext { get; private set; }

        public DbConnection Connection =>
            throw new NotSupportedException("the behavior must never reach for the connection");

        public DbTransaction? Transaction => null;

        public bool HasActiveTransaction { get; private set; }

        public bool IsOwner => true;

        public Task<IUnitOfWorkScope> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("begin");
            HasActiveTransaction = true;
            return Task.FromResult<IUnitOfWorkScope>(this);
        }

        public Task SetTenantContextAsync(
            ITenantContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add("set-tenant");
            TenantContext = context;
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("commit");
            HasActiveTransaction = false;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("rollback");
            HasActiveTransaction = false;
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken cancellationToken = default) =>
            CommitAsync(cancellationToken);

        public void MarkRollbackOnly() => Calls.Add("mark-rollback-only");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
