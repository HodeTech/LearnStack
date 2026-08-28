using System.Data.Common;
using FluentAssertions;
using LearnStack.Application.Pipeline;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Pipeline;

/// <summary>
/// <c>TransactionBehavior</c> — step 6. The commit boundary: what it calls, in
/// what order, which outcome resolves the transaction which way, and what
/// survives a terminal call that itself fails.
/// </summary>
/// <remarks>
/// <para>
/// Against a recording <c>IUnitOfWork</c> rather than a database, because the
/// question here is the protocol. Whether the protocol produces the right rows in
/// PostgreSQL is <c>UnitOfWorkTests</c>, which runs against a real one.
/// </para>
/// <para>
/// <b>The fake models nesting depth and can fail its terminal call.</b> Its first
/// version did neither, and could therefore not fail against either of the two
/// defects this class now covers: a commit-time exception replaced by the
/// rollback's own complaint, and an absorbed inner failure poisoning the outer
/// frame. A fake that cannot reach the failure is a test that cannot see it.
/// </para>
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
    public async Task Rolls_Back_A_Failure_Result_Without_Marking_The_Unit()
    {
        // A business-rule violation is a fail-Result, not an exception
        // (ADR-0032 § Sub-decision 4) — and it must still not commit: the handler
        // may have written before deciding it could not finish.
        //
        // It must ALSO not call MarkRollbackOnly. ADR-0040 § Nesting: an inner
        // Result.Fail that an outer handler deliberately absorbs is not a failure
        // of the unit. Marking it here would poison the outer frame, which is the
        // one case the ADR names and forbids.
        var unitOfWork = new RecordingUnitOfWork();
        var behavior = Build(unitOfWork);

        var failure = Result.FailFor<Result<string>>(
            new Error(new LocalizedMessage("lockey_business_rule_violation")));

        var result = await behavior.Handle(
            new DummyCommand(), () => Next(unitOfWork, failure), default);

        result.IsFailure.Should().BeTrue();
        unitOfWork.Calls.Should().Equal("begin", "set-tenant", "handler", "rollback");
        unitOfWork.Calls.Should().NotContain("mark-rollback-only");
    }

    [Fact]
    public async Task Rolls_Back_And_Rethrows_An_Exception_After_Marking_The_Unit()
    {
        // An exception IS a failure of the unit, so the mark comes first: an
        // outer frame that absorbs the exception must not then commit a partial
        // one. Rethrown rather than converted — AuditLogBehavior, three behaviors
        // out at step 3, audits the failure and rethrows through
        // ExceptionDispatchInfo, and the L1 IExceptionHandler turns it into
        // Problem Details.
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
        unitOfWork.Calls.Should().Equal(
            "begin", "set-tenant", "handler", "mark-rollback-only", "rollback");
    }

    [Fact]
    public async Task A_Failed_Commit_Reaches_The_Caller_Unchanged()
    {
        // The defect this case exists for: the cleanup path used to run after a
        // faulted COMMIT, and the rollback's own complaint replaced the database's
        // exception with no inner exception. A constraint violation deferred to
        // commit time — or a cancellation — arrived as a bookkeeping error.
        var unitOfWork = new RecordingUnitOfWork { CommitFailure = new DbTestException("23505") };
        var behavior = Build(unitOfWork);

        var act = async () => await behavior.Handle(
            new DummyCommand(), () => Next(unitOfWork, Result.Ok("ok")), default);

        (await act.Should().ThrowAsync<DbTestException>()).WithMessage("23505");
        unitOfWork.Calls.Should().Equal("begin", "set-tenant", "handler", "commit");
        unitOfWork.Calls.Should().NotContain("rollback",
            "a faulted COMMIT leaves the outcome unknown — ADR-0033's Indeterminate — "
            + "and rolling back on top of it is both wrong and what destroyed the exception");
    }

    [Fact]
    public async Task A_Cancelled_Commit_Stays_An_OperationCanceledException()
    {
        // Three ADR-0032 behaviours key on the exception TYPE: AuditLogBehavior
        // skips its catch for OperationCanceledException, HttpStatusMap answers
        // 499, and the error-tracking provider does not capture it. Replacing it
        // with an InvalidOperationException inverted all three at once.
        var unitOfWork = new RecordingUnitOfWork
        {
            CommitFailure = new OperationCanceledException("client went away"),
        };
        var behavior = Build(unitOfWork);

        var act = async () => await behavior.Handle(
            new DummyCommand(), () => Next(unitOfWork, Result.Ok("ok")), default);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task An_Absorbed_Inner_Failure_Leaves_The_Outer_Frame_Able_To_Commit()
    {
        // ADR-0040 § Nesting's worked example, end to end: an application contract
        // reaches a second handler through ISender, that handler returns a
        // fail-Result, and the outer handler absorbs it and reports success. The
        // outer handler's own work must still commit.
        var unitOfWork = new RecordingUnitOfWork();
        var outer = Build(unitOfWork);
        var inner = Build(unitOfWork);

        var failure = Result.FailFor<Result<string>>(
            new Error(new LocalizedMessage("lockey_business_rule_violation")));

        var result = await outer.Handle(
            new DummyCommand(),
            async () =>
            {
                unitOfWork.Calls.Add("outer-handler");

                var innerResult = await inner.Handle(
                    new DummyCommand(), () => Next(unitOfWork, failure), default);

                innerResult.IsFailure.Should().BeTrue();
                return Result.Ok("absorbed");
            },
            default);

        result.IsSuccess.Should().BeTrue();
        unitOfWork.Committed.Should().BeTrue(
            "the outer handler took responsibility for the inner failure, and its own work commits");
        unitOfWork.Calls.Should().Equal(
            "begin", "set-tenant", "outer-handler",
            "begin", "handler", "rollback",
            "commit");
    }

    [Fact]
    public async Task A_Nested_Exception_Makes_The_Outer_Commit_Impossible()
    {
        // The other half of § Nesting. The inner frame's exception marks the unit,
        // so even an outer handler that catches it cannot commit a partial one.
        var unitOfWork = new RecordingUnitOfWork();
        var outer = Build(unitOfWork);
        var inner = Build(unitOfWork);

        var act = async () => await outer.Handle(
            new DummyCommand(),
            async () =>
            {
                try
                {
                    return await inner.Handle(
                        new DummyCommand(),
                        () => throw new InvalidOperationException("inner blew up"),
                        default);
                }
                catch (InvalidOperationException)
                {
                    return Result.Ok("swallowed");
                }
            },
            default);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rollback-only*");
        unitOfWork.Committed.Should().BeFalse();
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

    [Fact]
    public async Task A_failing_rollback_does_not_replace_the_exception_it_is_cleaning_up_after()
    {
        // The measured shape: an exception breaks the connection, Npgsql disposes
        // the NpgsqlTransaction with it, and the rollback then throws
        // ObjectDisposedException. Unguarded, that is what the caller,
        // AuditLogBehavior and IErrorTrackingProvider all see — the handler's own
        // exception simply gone, not even as an inner.
        var unitOfWork = new RecordingUnitOfWork
        {
            RollbackFailure = new ObjectDisposedException("NpgsqlTransaction"),
        };

        var behavior = Build(unitOfWork);
        var original = new DbTestException("the handler's own");

        var act = async () => await behavior.Handle(
            new DummyCommand(),
            () => throw original,
            default);

        (await act.Should().ThrowAsync<DbTestException>()).Which.Should().BeSameAs(original);
        unitOfWork.Calls.Should().Contain("rollback", "the cleanup was still attempted");
        unitOfWork.Committed.Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_rollback_does_not_turn_a_cancellation_into_a_failure()
    {
        // Three ADR-0032 behaviours hang off the exception TYPE: AuditLogBehavior
        // does not audit an OperationCanceledException, LearnStackExceptionHandler
        // does not capture one, and HttpStatusMap answers 499 rather than 500. A
        // rollback that replaced it inverted all three at once.
        var unitOfWork = new RecordingUnitOfWork
        {
            RollbackFailure = new ObjectDisposedException("NpgsqlTransaction"),
        };

        var act = async () => await Build(unitOfWork).Handle(
            new DummyCommand(),
            () => throw new OperationCanceledException("Query was cancelled"),
            default);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static TransactionBehavior<DummyCommand, Result<string>> Build(IUnitOfWork unitOfWork) =>
        new(unitOfWork, UnresolvedTenantContext.Instance,
            NullLogger<TransactionBehavior<DummyCommand, Result<string>>>.Instance);

    private static Task<Result<string>> Next(RecordingUnitOfWork unitOfWork, Result<string> result)
    {
        unitOfWork.Calls.Add("handler");
        return Task.FromResult(result);
    }

    /// <summary>Stands in for a provider exception the behavior must not touch.</summary>
    public sealed class DbTestException(string message) : Exception(message);

    /// <summary>
    /// An <see cref="IUnitOfWork"/> that records the protocol and models the parts
    /// of it the behavior depends on: frame depth, the rollback-only mark, and a
    /// terminal call that can fail.
    /// </summary>
    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        private int _depth;
        private bool _rollbackOnly;

        public List<string> Calls { get; } = [];

        public ITenantContext? TenantContext { get; private set; }

        /// <summary>Thrown by the outermost commit, to model a faulted COMMIT.</summary>
        public Exception? CommitFailure { get; init; }

        /// <summary>
        /// Thrown by the rollback, to model the cleanup path failing on a
        /// connection the original exception already broke.
        /// </summary>
        public Exception? RollbackFailure { get; init; }

        public bool Committed { get; private set; }

        public DbConnection Connection =>
            throw new NotSupportedException("the behavior must never reach for the connection");

        public DbTransaction? Transaction => null;

        public bool HasActiveTransaction => _depth > 0;

        public Task<IUnitOfWorkScope> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("begin");
            return Task.FromResult<IUnitOfWorkScope>(new Frame(this, ++_depth));
        }

        public Task SetTenantContextAsync(
            ITenantContext context, CancellationToken cancellationToken = default)
        {
            if (_depth > 1)
            {
                // A joiner. The real seam does the same, so a test that recorded
                // it here would assert a call the behavior does not make.
                return Task.CompletedTask;
            }

            Calls.Add("set-tenant");
            TenantContext = context;
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("commit");

            if (--_depth > 0)
            {
                return Task.CompletedTask;
            }

            if (_rollbackOnly)
            {
                return Task.FromException(new InvalidOperationException(
                    "The ambient transaction is marked rollback-only and has been rolled back."));
            }

            if (CommitFailure is not null)
            {
                return Task.FromException(CommitFailure);
            }

            Committed = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (_depth == 0)
            {
                return Task.CompletedTask;
            }

            Calls.Add("rollback");
            --_depth;

            return RollbackFailure is null
                ? Task.CompletedTask
                : Task.FromException(RollbackFailure);
        }

        public void MarkRollbackOnly()
        {
            Calls.Add("mark-rollback-only");
            _rollbackOnly = true;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Frame(RecordingUnitOfWork unitOfWork, int depth) : IUnitOfWorkScope
        {
            public bool IsOwner => depth == 1;

            public Task CompleteAsync(CancellationToken cancellationToken = default) =>
                unitOfWork.CommitAsync(cancellationToken);

            public Task FailAsync(CancellationToken cancellationToken = default) =>
                unitOfWork.RollbackAsync(cancellationToken);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
