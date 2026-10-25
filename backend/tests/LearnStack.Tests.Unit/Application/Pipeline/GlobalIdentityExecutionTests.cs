using System.Data.Common;
using FluentAssertions;
using LearnStack.Application.Audit;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Audit.Capture;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.Metrics;
using Npgsql;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Pipeline;

public sealed class GlobalIdentityExecutionTests
{
    [Fact]
    public async Task Ordinary_writers_refuse_global_enrollment_even_before_an_intent_without_opening_a_connection()
    {
        var fixture = new Fixture();
        await using var owner = await fixture.PrepareAsync();
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var source = new Lazy<NpgsqlDataSource>(() => throw new InvalidOperationException("No database access is permitted."));
        var store = new PostgresAuditStore(fixture.Capture, source, NullLogger<PostgresAuditStore>.Instance,
            services.GetRequiredService<IMeterFactory>(), new AuditHealth(), new FakeCatalog(),
            Substitute.For<ITenantContextAccessor>(), fixture.Enrollment);
        Func<Task> write = () => store.WritePendingAsync(fixture.Unit);
        await write.Should().ThrowAsync<ArgumentException>();
        var draft = AuditDraftComposer.InTransaction(new AuditIntent(
            AuditEntryId.From(Guid.Parse("30000000-0000-7000-8000-000000000001")),
            TenantId.From(Guid.Parse("40000000-0000-7000-8000-000000000001")), null, null, null,
            "tenancy", "tenancy.tenant.create", OperationType.Create, OperationClass.Must, null, DateTimeOffset.UnixEpoch), []);
        Func<Task> standalone = () => store.WriteStandaloneAsync(draft);
        Func<Task> bestEffort = () => store.WriteBestEffortAsync(draft);
        Func<Task> platform = () => store.WritePlatformScopeAsync(draft,
            Substitute.For<DbConnection>(), Substitute.For<DbTransaction>());
        await standalone.Should().ThrowAsync<ArgumentException>();
        await bestEffort.Should().ThrowAsync<ArgumentException>();
        await platform.Should().ThrowAsync<ArgumentException>();
        source.IsValueCreated.Should().BeFalse();
        fixture.Capture.State.Should().Be(AuditIntentState.None);
    }

    [Fact]
    public async Task Sentinel_creation_draft_cannot_enter_ordinary_or_platform_writers()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var source = new Lazy<NpgsqlDataSource>(() => throw new InvalidOperationException("No database access is permitted."));
        var capture = new AuditStateCapture();
        var store = new PostgresAuditStore(capture, source, NullLogger<PostgresAuditStore>.Instance,
            services.GetRequiredService<IMeterFactory>(), new AuditHealth(), new FakeCatalog(),
            Substitute.For<ITenantContextAccessor>(), new LearnStack.Infrastructure.Persistence.GlobalIdentityEnrollment());
        var intent = new AuditIntent(AuditEntryId.From(Guid.Parse("30000000-0000-7000-8000-000000000001")),
            TenantId.PlatformSentinel, null, null, null, "identity", GlobalIdentityCreationReference.Operation,
            OperationType.SecurityEvent, OperationClass.Must, typeof(User), DateTimeOffset.UnixEpoch);
        capture.DeclareIntent(intent);
        var draft = AuditDraftComposer.InTransaction(intent, []);
        Func<Task> pending = () => store.WritePendingAsync(Substitute.For<IUnitOfWork>());
        Func<Task> standalone = () => store.WriteStandaloneAsync(draft);
        Func<Task> bestEffort = () => store.WriteBestEffortAsync(draft);
        Func<Task> platform = () => store.WritePlatformScopeAsync(draft,
            Substitute.For<DbConnection>(), Substitute.For<DbTransaction>());
        await pending.Should().ThrowAsync<ArgumentException>();
        await standalone.Should().ThrowAsync<ArgumentException>();
        await bestEffort.Should().ThrowAsync<ArgumentException>();
        await platform.Should().ThrowAsync<ArgumentException>();
        source.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public async Task Preparation_locks_without_allocating_or_declaring_and_disposal_releases_the_original_owner()
    {
        var fixture = new Fixture();
        await using (await fixture.PrepareAsync())
        {
            fixture.Trace.Should().Equal("begin", "lock");
            fixture.Capture.Intents.Should().BeEmpty();
            fixture.Guids.DidNotReceive().NewUuidV7();
        }
        fixture.Trace.Should().Equal("begin", "lock", "dispose");
    }

    [Fact]
    public async Task Successful_creation_keeps_one_reference_and_validates_after_audit_before_commit()
    {
        var fixture = new Fixture();
        await using var owner = await fixture.PrepareAsync();
        owner.AdmitSerializedAbsence();
        GlobalIdentityCreationReference? creation = null;
        var result = await fixture.Audit.ExecuteGlobalIdentityAsync(owner, reference =>
            fixture.Transaction.ExecutePreparedGlobalIdentityAsync(owner, reference, () =>
            {
                creation = reference;
                fixture.Trace.Add("handler");
                fixture.Capture.Intents.Single().GlobalIdentity.Should().BeSameAs(reference);
                (reference.Timestamp.UtcTicks % 10).Should().Be(0);
                fixture.Capture.Add(new CapturedEntityChange(nameof(User), reference.TargetUserId.Value.ToString("D"), null, "{}", []));
                return Task.FromResult(Result.Ok(reference.TargetUserId));
            }));
        result.Value.Should().Be(creation!.TargetUserId);
        fixture.Trace.Should().Equal("begin", "lock", "handler", "audit", "constraints", "commit", "dispose");
        owner.RecordedOutcome.Should().Be(AuditIntentState.Committed);
        fixture.Capture.Intents.Should().BeEmpty();
        fixture.Reconciled.Should().BeEmpty();
        fixture.Pending.Single().Metadata.Should().Contain("\"identity_source\":\"identity-bootstrap\"");
        fixture.Pending.Single().ActorUserId.Should().BeNull();
    }

    [Theory]
    [InlineData(false, AuditOutcome.Failed, AuditIntentState.RolledBack)]
    [InlineData(true, AuditOutcome.Indeterminate, AuditIntentState.Indeterminate)]
    public async Task Constraint_refusal_is_known_rollback_but_physical_commit_failure_remains_uncertain(
        bool duringCommit, AuditOutcome expectedAudit, AuditIntentState expectedState)
    {
        var fixture = new Fixture { FailConstraints = !duringCommit, FailCommit = duringCommit };
        await using var owner = await fixture.PrepareAsync();
        owner.AdmitSerializedAbsence();
        Func<Task> execute = async () => await fixture.Audit.ExecuteGlobalIdentityAsync(owner, reference =>
            fixture.Transaction.ExecutePreparedGlobalIdentityAsync(owner, reference,
                () => Task.FromResult(Result.Ok(reference.TargetUserId))));
        await execute.Should().ThrowAsync<IOException>();
        owner.RecordedOutcome.Should().Be(expectedState);
        fixture.Reconciled.Should().ContainSingle().Which.Outcome.Should().Be(expectedAudit);
        fixture.Reconciled[0].Id.Should().Be(fixture.Pending.Single().Id);
        fixture.Reconciled[0].Timestamp.Should().Be(fixture.Pending[0].Timestamp.AddTicks(10));
        fixture.Trace.Contains("commit").Should().Be(duringCommit);
        fixture.Trace.Contains("rollback").Should().Be(!duringCommit);
        fixture.Capture.Intents.Should().BeEmpty();
    }

    [Fact]
    public async Task Failure_before_consumption_rolls_back_before_reconciliation_and_keeps_the_original_exception()
    {
        var fixture = new Fixture();
        await using var owner = await fixture.PrepareAsync();
        owner.AdmitSerializedAbsence();
        var failure = new IOException("deferred entry failed");
        Func<Task> execute = () => fixture.Audit.ExecuteGlobalIdentityAsync<Result<UserId>>(owner, _ => throw failure);
        (await execute.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        fixture.Trace.Should().Equal("begin", "lock", "rollback", "dispose", "reconcile");
        fixture.Pending.Should().BeEmpty();
        fixture.Reconciled.Should().ContainSingle().Which.Outcome.Should().Be(AuditOutcome.Failed);
        owner.RecordedOutcome.Should().Be(AuditIntentState.RolledBack);
    }

    [Fact]
    public async Task Global_entry_refuses_unadmitted_absence_and_ordinary_entry_before_a_frame_or_allocation()
    {
        var fixture = new Fixture();
        await using var owner = await fixture.PrepareAsync();
        Func<Task> global = () => fixture.Audit.ExecuteGlobalIdentityAsync(owner, _ => Task.FromResult(Result.Ok("unexpected")));
        Func<Task> ordinary = () => fixture.Audit.ExecuteAsync(typeof(User), null,
            () => Task.FromResult(Result.Ok("unexpected")), default);
        Func<Task> transaction = () => fixture.Transaction.ExecuteAsync(null, TransactionMode.ReadWrite,
            "ordinary", () => Task.FromResult(Result.Ok("unexpected")));
        await global.Should().ThrowAsync<InvalidOperationException>();
        await ordinary.Should().ThrowAsync<InvalidOperationException>();
        await transaction.Should().ThrowAsync<InvalidOperationException>();
        fixture.Capture.HasOpenFrame.Should().BeFalse();
        fixture.Capture.Intents.Should().BeEmpty();
        fixture.Guids.DidNotReceive().NewUuidV7();
    }

    [Fact]
    public async Task Preparation_refuses_inherited_tenant_context_before_opening_or_allocating()
    {
        var fixture = new Fixture();
        Func<Task> prepare = () => fixture.PrepareAsync(new HarnessTenantContext());
        await prepare.Should().ThrowAsync<InvalidOperationException>();
        fixture.Trace.Should().BeEmpty();
        fixture.Guids.DidNotReceive().NewUuidV7();
        fixture.Capture.HasOpenFrame.Should().BeFalse();
    }

    [Fact]
    public async Task Foreign_subject_intent_owner_and_unenrolled_snapshot_are_refused_without_buffer_mutation()
    {
        var fixture = new Fixture();
        await using var owner = await fixture.PrepareAsync();
        owner.AdmitSerializedAbsence();
        await fixture.Audit.ExecuteGlobalIdentityAsync(owner, reference =>
            fixture.Transaction.ExecutePreparedGlobalIdentityAsync(owner, reference, () =>
            {
                var intent = fixture.Capture.Intents.Single();
                FluentActions.Invoking(() => reference.RequireLive(Substitute.For<IUnitOfWork>()))
                    .Should().Throw<InvalidOperationException>();
                FluentActions.Invoking(() => reference.RequireSubject(reference.AuthorityId,
                    IdentitySubjectKey.Compute(reference.AuthorityId, "another-subject")))
                    .Should().Throw<InvalidOperationException>();
                FluentActions.Invoking(() => reference.RequireSubject(new GlobalIdentitySubject(reference.AuthorityId,
                    "https://another-issuer.example/realm", reference.Descriptor.Subject.Subject)))
                    .Should().Throw<InvalidOperationException>();
                FluentActions.Invoking(() => AuditDraftComposer.InTransaction(intent with { SubjectId = "another" }, []))
                    .Should().Throw<ArgumentException>();
                using var context = new ProbeContext();
                FluentActions.Invoking(() => new AuditChangeTrackerInterceptor(fixture.Capture, fixture.Unit).Capture(context))
                    .Should().Throw<InvalidOperationException>().WithMessage("Global identity capture requires*");
                fixture.Capture.Changes.Should().BeEmpty();
                return Task.FromResult(Result.Ok(reference.TargetUserId));
            }));
    }

    [Fact]
    public async Task Commit_acknowledgement_survives_cleanup_failure_without_reconciliation()
    {
        var fixture = new Fixture { FailDisposal = true };
        await using var owner = await fixture.PrepareAsync();
        owner.AdmitSerializedAbsence();
        Func<Task> execute = () => fixture.Audit.ExecuteGlobalIdentityAsync(owner, reference =>
            fixture.Transaction.ExecutePreparedGlobalIdentityAsync(owner, reference,
                () => Task.FromResult(Result.Ok(reference.TargetUserId))));
        await execute.Should().ThrowAsync<IOException>();
        owner.RecordedOutcome.Should().Be(AuditIntentState.Committed);
        fixture.Reconciled.Should().BeEmpty();
    }

    private sealed class Fixture
    {
        private bool _active;
        private bool _rollbackOnly;
        private readonly GlobalIdentityOwnerDescriptor _descriptor;
        public IGlobalIdentityUnitOfWork Unit { get; } = Substitute.For<IGlobalIdentityUnitOfWork>();
        public IGlobalIdentityEnrollment Enrollment { get; } = Substitute.For<IGlobalIdentityEnrollment>();
        public IGuidFactory Guids { get; } = Substitute.For<IGuidFactory>();
        public AuditStateCapture Capture { get; } = new();
        public List<string> Trace { get; } = [];
        public List<AuditEntryDraft> Pending { get; } = [];
        public List<AuditEntryDraft> Reconciled { get; } = [];
        public bool FailConstraints { get; init; }
        public bool FailCommit { get; init; }
        public bool FailDisposal { get; init; }
        public AuditExecutionCoordinator Audit { get; }
        public TransactionExecutionCoordinator Transaction { get; }

        public Fixture()
        {
            _descriptor = DescriptorSource.Create(new GlobalIdentitySubject(
                IdentityAuthorityId.From(Guid.Parse("10000000-0000-7000-8000-000000000001")),
                "https://issuer.example/realm", "opaque-subject"));
            var connection = Substitute.For<DbConnection>();
            var transaction = Substitute.For<DbTransaction>();
            var frame = Substitute.For<IUnitOfWorkScope>();
            frame.IsOwner.Returns(true);
            var announcement = new GlobalIdentityContextAnnouncement(1, _descriptor);
            Unit.Connection.Returns(connection);
            Unit.Transaction.Returns(_ => _active ? transaction : null);
            Unit.Mode.Returns(_ => _active ? TransactionMode.ReadWrite : null);
            Unit.HasActiveTransaction.Returns(_ => _active);
            Unit.IsGlobalIdentityTransaction.Returns(_ => _active);
            Enrollment.IsActive.Returns(_ => _active);
            Unit.IsRollbackOnly.Returns(_ => _rollbackOnly);
            Unit.When(unit => unit.MarkRollbackOnly()).Do(_ => _rollbackOnly = true);
            Unit.GetGlobalIdentityAnnouncement(Arg.Any<DbTransaction?>()).Returns(_ => _active ? announcement : null);
            Unit.BeginGlobalIdentityTransactionAsync(_descriptor, TransactionMode.ReadWrite, Arg.Any<CancellationToken>())
                .Returns(_ => { _active = true; Trace.Add("begin"); return frame; });
            Unit.AcquireGlobalIdentitySubjectLockAsync(_descriptor, Arg.Any<CancellationToken>())
                .Returns(_ => { Trace.Add("lock"); return Task.CompletedTask; });
            Unit.ValidateGlobalIdentityConstraintsAsync(_descriptor, Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Trace.Add("constraints");
                if (FailConstraints) throw new IOException("constraint refusal");
                return Task.CompletedTask;
            });
            frame.CompleteAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Trace.Add("commit"); _active = false;
                if (FailCommit) throw new IOException("commit acknowledgement lost");
                return Task.CompletedTask;
            });
            frame.FailAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            { Trace.Add("rollback"); _active = false; return Task.CompletedTask; });
#pragma warning disable CA2012 // NSubstitute configures the call; each execution returns a fresh ValueTask below.
            frame.DisposeAsync().Returns(_ =>
            {
                Trace.Add("dispose"); _active = false;
                if (FailDisposal) throw new IOException("cleanup failed");
                return ValueTask.CompletedTask;
            });
#pragma warning restore CA2012
            var store = Substitute.For<IAuditStore>();
            store.WriteGlobalIdentityPendingAsync(Unit, Arg.Any<GlobalIdentityCreationReference>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Trace.Add("audit");
                    var intent = Capture.Intents.Single();
                    Pending.Add(AuditDraftComposer.InTransaction(intent, Capture.ChangesOf(intent)));
                    Capture.MarkWrittenInTransaction();
                    return Task.CompletedTask;
                });
            store.ReconcileGlobalIdentityAsync(Arg.Any<AuditIntent>(), Arg.Any<IReadOnlyList<CapturedEntityChange>>(),
                Arg.Any<AuditIntentState>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(call =>
                {
                    Trace.Add("reconcile");
                    call.ArgAt<CancellationToken>(4).IsCancellationRequested.Should().BeFalse();
                    Reconciled.Add(AuditDraftComposer.Reconciled(call.ArgAt<AuditIntent>(0),
                        call.ArgAt<IReadOnlyList<CapturedEntityChange>>(1), call.ArgAt<AuditIntentState>(2), call.ArgAt<DateTimeOffset>(3)));
                    return Task.CompletedTask;
                });
            Guids.NewUuidV7().Returns(Guid.Parse("20000000-0000-7000-8000-000000000001"),
                Guid.Parse("30000000-0000-7000-8000-000000000001"));
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(DateTimeOffset.UnixEpoch.AddTicks(123));
            var catalog = new FakeCatalog
            {
                OffPath = new Dictionary<string, AuditCatalogEntry>
                {
                    [GlobalIdentityCreationReference.Operation] = new("identity", GlobalIdentityCreationReference.Operation,
                    OperationType.SecurityEvent, OperationClass.Must, typeof(User)),
                }
            };
            Audit = new AuditExecutionCoordinator(catalog, new FakeClassifier(), Capture, store,
                UnresolvedTenantContext.Instance, clock, Guids, NullLogger<AuditExecutionCoordinator>.Instance, Unit);
            var producer = new ProducerExecution(Unit);
            Transaction = new TransactionExecutionCoordinator(Unit, UnresolvedTenantContext.Instance, store, Capture,
                producer, new DomainEventCollector(producer, Substitute.For<IPublisher>()),
                NullLogger<TransactionExecutionCoordinator>.Instance);
        }

        public Task<TransactionExecutionCoordinator.PreparedGlobalIdentityOwner> PrepareAsync(ITenantContext? context = null) =>
            TransactionExecutionCoordinator.PrepareGlobalIdentityOwnerAsync(Unit, Capture,
                context ?? UnresolvedTenantContext.Instance, _descriptor);
    }

    private sealed class DescriptorSource : GlobalIdentityOwnerDescriptor.MintingSource
    {
        internal static GlobalIdentityOwnerDescriptor Create(GlobalIdentitySubject subject) =>
            ForBootstrap(subject, typeof(ProbeContext), typeof(User));
    }

    private sealed class User;
    private sealed class ProbeContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            throw new InvalidOperationException("The capture admission must refuse before model/value inspection.");
    }
}
