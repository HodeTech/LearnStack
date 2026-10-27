using System.Diagnostics;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class GlobalIdentityBootstrapTests
{
    [Fact]
    public async Task A_waiting_creator_reads_the_committed_winner_after_the_real_subject_lock()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = BuildProvider(_database.AppConnectionString, Registration, new FixtureAdmission(Subject, Actor), services =>
            services.Replace(ServiceDescriptor.Scoped<IUserBindingStore>(provider =>
                new PausingStore(new UserBindingStore(provider.GetRequiredService<IdentityDbContext>(),
                    provider.GetRequiredService<IGlobalIdentityUnitOfWork>()), entered, release.Task))));
        await using var second = BuildProvider(_database.AppConnectionString, Registration, new FixtureAdmission(Subject, Actor));
        await QualifyAsync(first);
        await QualifyAsync(second);
        var creating = AdoptAsync(first, Subject, Actor);
        if (await Task.WhenAny(entered.Task, creating).WaitAsync(TimeSpan.FromSeconds(5)) == creating)
        {
            var early = await creating;
            throw new InvalidOperationException($"The first creator exited before its locked lookup ({early.Outcome}).");
        }
        var waiting = AdoptAsync(second, Subject, Actor);
        try
        {
            await WaitForSubjectWaiterAsync(waiting);
            waiting.IsCompleted.Should().BeFalse("PostgreSQL reports the second physical owner waiting for the held advisory lock");
        }
        finally { release.TrySetResult(); }
        var outcomes = await Task.WhenAll(creating, waiting).WaitAsync(TimeSpan.FromSeconds(10));
        outcomes.Select(result => result.Outcome).Should().Equal(IdentityBindingOutcome.Created, IdentityBindingOutcome.Existing);
        (await CountsAsync()).Should().Be((1L, 1L));
    }

    [Fact]
    public async Task MUST_failure_rolls_back_User_and_preserves_failed_attempt_then_clean_twin_succeeds()
    {
        await ExecuteDeploymentAsync("""
            CREATE FUNCTION public.proof_reject_global_success() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.operation = 'identity.user.create' AND NEW.outcome = 'success' THEN
                    RAISE EXCEPTION 'fixed audit proof refusal' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER proof_reject_global_success BEFORE INSERT ON public.audit_log
            FOR EACH ROW EXECUTE FUNCTION public.proof_reject_global_success();
            """);
        await using (var provider = BuildProvider(_database.AppConnectionString, Registration, new FixtureAdmission(Subject, Actor)))
        {
            await QualifyAsync(provider);
            var refused = await AdoptAsync(provider, Subject, Actor);
            refused.Outcome.Should().Be(IdentityBindingOutcome.StorageUnavailable);
            refused.Actor.Should().BeNull();
        }
        (await CountsAsync()).Should().Be((0L, 1L));
        (await AuditRowsAsync()).Should().ContainSingle(row => row.Outcome == "failed");
        await ExecuteDeploymentAsync("DROP TRIGGER proof_reject_global_success ON public.audit_log; DROP FUNCTION public.proof_reject_global_success();");
        await using var clean = BuildProvider(_database.AppConnectionString, Registration, new FixtureAdmission(Subject, Actor));
        await QualifyAsync(clean);
        (await AdoptAsync(clean, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.Created);
        (await CountsAsync()).Should().Be((1L, 2L));
    }

    [Fact]
    public async Task Reciprocal_guard_failure_is_known_precommit_failure_and_not_unknown_COMMIT()
    {
        await using (var provider = BuildProvider(_database.AppConnectionString, Registration, new FixtureAdmission(Subject, Actor), services =>
            services.Replace(ServiceDescriptor.Scoped<IUserBindingStore>(provider =>
                new WrongCreationReferenceStore(new UserBindingStore(provider.GetRequiredService<IdentityDbContext>(),
                    provider.GetRequiredService<IGlobalIdentityUnitOfWork>()))))))
        {
            await QualifyAsync(provider);
            (await AdoptAsync(provider, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.StorageUnavailable);
        }
        (await CountsAsync()).Should().Be((0L, 1L));
        (await AuditRowsAsync()).Should().ContainSingle(row => row.Outcome == "failed");
        await using var clean = BuildProvider(_database.AppConnectionString, Registration, new FixtureAdmission(Subject, Actor));
        await QualifyAsync(clean);
        (await AdoptAsync(clean, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.Created);
        (await CountsAsync()).Should().Be((1L, 2L));
    }

    [Fact]
    public async Task EF_constraint_failure_is_translated_without_retaining_the_provider_exception()
    {
        await using (var first = BuildProvider(_database.AppConnectionString, Registration, new FixtureAdmission(Subject, Actor)))
        {
            await QualifyAsync(first);
            (await AdoptAsync(first, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.Created);
        }
        var colliding = new GlobalIdentitySubject(Subject.AuthorityId, Subject.Issuer, "different-subject-same-reserved-actor");
        UserBindingStorageException? observed = null;
        await using var provider = BuildProvider(_database.AppConnectionString, Registration, new FixtureAdmission(colliding, Actor), services =>
            services.Replace(ServiceDescriptor.Scoped<IUserBindingStore>(serviceProvider =>
                new ObservingFailureStore(new UserBindingStore(serviceProvider.GetRequiredService<IdentityDbContext>(),
                    serviceProvider.GetRequiredService<IGlobalIdentityUnitOfWork>()),
                    failure => observed = failure))));
        await QualifyAsync(provider);
        (await AdoptAsync(provider, colliding, Actor)).Outcome.Should().Be(IdentityBindingOutcome.StorageUnavailable);
        observed.Should().NotBeNull("the real EF duplicate actor failure must cross the bounded adapter boundary");
        observed!.InnerException.Should().BeNull();
        observed.Message.Should().Be("Identity binding storage is unavailable.");
        (await CountsAsync()).Should().Be((1L, 1L));
        (await CountsAsync(colliding)).Should().Be((0L, 1L));
        (await AuditRowsAsync(colliding)).Should().ContainSingle(row => row.Outcome == "failed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_lost_ack_and_unsent_COMMIT_remain_uncertain_without_asserting_a_User_outcome(bool dropRequest)
    {
        await using var proxy = new CommitResponseDroppingProxy(_database.AppConnectionString,
            commitsToPass: 1, dropCommitRequest: dropRequest);
        await using (var provider = BuildProvider(proxy.ConnectionString, Registration, new FixtureAdmission(Subject, Actor)))
        {
            await QualifyAsync(provider); // The proxy passes this separate read-only qualification COMMIT.
            var result = await AdoptAsync(provider, Subject, Actor).WaitAsync(TimeSpan.FromSeconds(15));
            result.Outcome.Should().Be(IdentityBindingOutcome.Indeterminate);
            result.Actor.Should().BeNull();
        }
        if (dropRequest) proxy.DroppedCommitRequests.Should().Be(1);
        else proxy.DroppedCommitResponses.Should().Be(1);
        (await CountsAsync()).Should().Be(dropRequest ? (0L, 1L) : (1L, 2L));
        (await AuditRowsAsync()).Should().ContainSingle(row => row.Outcome == "indeterminate");
        await using var clean = BuildProvider(_database.AppConnectionString, Registration, new FixtureAdmission(Subject, Actor));
        await QualifyAsync(clean);
        (await AdoptAsync(clean, Subject, Actor)).Outcome.Should().Be(
            dropRequest ? IdentityBindingOutcome.Created : IdentityBindingOutcome.Existing);
        (await CountsAsync()).Should().Be((1L, 2L));
    }

    private async Task ExecuteDeploymentAsync(string sql)
    {
        await using var connection = await OpenConnectionAsync(_database.MigrationConnectionString);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task WaitForSubjectWaiterAsync(Task<IdentityBindingResult> waiting)
    {
        var deadline = Stopwatch.StartNew();
        await using var connection = await OpenConnectionAsync(_database.MigrationConnectionString);
        while (deadline.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            if (waiting.IsCompleted)
            {
                var early = await waiting;
                throw new InvalidOperationException($"The competing creator exited before waiting for its subject lock ({early.Outcome}).");
            }
            await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype='advisory' "
                + "AND database=(SELECT oid FROM pg_database WHERE datname=current_database()) AND NOT granted)", connection);
            if (await command.ExecuteScalarAsync() is true) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("The actual competing identity owner did not reach its subject-lock wait.");
    }

    private sealed class PausingStore(IUserBindingStore inner, TaskCompletionSource entered, Task release) : IUserBindingStore
    {
        public async Task<User?> FindExactAsync(GlobalIdentitySubject subject, CancellationToken cancellationToken = default)
        {
            var user = await inner.FindExactAsync(subject, cancellationToken);
            entered.TrySetResult();
            await release.WaitAsync(cancellationToken);
            return user;
        }
        public Task AddAsync(User user, CancellationToken cancellationToken = default) => inner.AddAsync(user, cancellationToken);
    }

    private sealed class WrongCreationReferenceStore(IUserBindingStore inner) : IUserBindingStore
    {
        public Task<User?> FindExactAsync(GlobalIdentitySubject subject, CancellationToken cancellationToken = default) =>
            inner.FindExactAsync(subject, cancellationToken);
        public Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            // A test-only mutation of the actual business row leaves the real database witness to refuse it.
            typeof(User).GetProperty(nameof(User.CreationAuditTimestamp))!.SetValue(user, user.CreationAuditTimestamp.AddTicks(10));
            return inner.AddAsync(user, cancellationToken);
        }
    }

    private sealed class ObservingFailureStore(IUserBindingStore inner, Action<UserBindingStorageException> observe) : IUserBindingStore
    {
        public Task<User?> FindExactAsync(GlobalIdentitySubject subject, CancellationToken cancellationToken = default) =>
            inner.FindExactAsync(subject, cancellationToken);
        public async Task AddAsync(User user, CancellationToken cancellationToken = default)
        {
            try { await inner.AddAsync(user, cancellationToken); }
            catch (UserBindingStorageException failure) { observe(failure); throw; }
        }
    }
}
