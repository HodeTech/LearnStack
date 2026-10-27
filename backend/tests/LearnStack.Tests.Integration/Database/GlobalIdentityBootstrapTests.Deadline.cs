using System.Diagnostics;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class GlobalIdentityBootstrapTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stalled_global_audit_INSERT_honors_its_deadline_or_caller_cancellation_and_reconciles(bool cancelCaller)
    {
        await ExecuteDeploymentAsync("""
            CREATE FUNCTION public.proof_stall_global_success() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.operation = 'identity.user.create' AND NEW.outcome = 'success' THEN
                    PERFORM pg_sleep(30);
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER proof_stall_global_success BEFORE INSERT ON public.audit_log
            FOR EACH ROW EXECUTE FUNCTION public.proof_stall_global_success();
            """);
        var appConnection = new NpgsqlConnectionStringBuilder(_database.AppConnectionString)
        { CommandTimeout = 0, CancellationTimeout = 1000 }.ConnectionString;
        await using var provider = BuildProvider(appConnection, Registration, new FixtureAdmission(Subject, Actor));
        await QualifyAsync(provider);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        using (var inherited = unit.Connection.CreateCommand())
            inherited.CommandTimeout.Should().Be(0, "the physical application connection has an unlimited default");
        await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
        var running = services.GetRequiredService<ISeedIdentityBindingAdopter>().AdoptAsync(Subject, Actor, caller.Token);
        try
        {
            await WaitForGlobalAuditSleepAsync(appConnection, running);
            if (cancelCaller)
            {
                await caller.CancelAsync();
                await FluentActions.Awaiting(() => running).Should().ThrowAsync<OperationCanceledException>();
            }
            else
            {
                var result = await running;
                result.Outcome.Should().Be(IdentityBindingOutcome.StorageUnavailable,
                    "an INSERT deadline before COMMIT is a known failure, not an indeterminate commit");
                result.Actor.Should().BeNull();
                caller.IsCancellationRequested.Should().BeFalse();
            }
            watchdog.IsCancellationRequested.Should().BeFalse("the independent watchdog did not terminate the stalled INSERT");
            unit.HasActiveTransaction.Should().BeFalse();
            unit.IsRollbackOnly.Should().BeTrue();
            services.GetRequiredService<IAuditStateCapture>().Intents.Should().BeEmpty();
            provider.GetRequiredService<IAuditHealth>().IsHealthy.Should().BeTrue(
                "failed-attempt reconciliation retains its own budget after the caller has stopped");
            (await CountsAsync()).Should().Be((0L, 1L));
            (await AuditRowsAsync()).Should().ContainSingle().Which.Outcome.Should().Be("failed");
        }
        finally
        {
            await caller.CancelAsync();
            try { await running; }
            catch (Exception) when (running.IsCompleted)
            {
                // Join the real owner; its primary failure or cancellation was
                // already asserted, and cleanup must not replace that assertion.
            }
            await ExecuteDeploymentAsync("""
                DROP TRIGGER proof_stall_global_success ON public.audit_log;
                DROP FUNCTION public.proof_stall_global_success();
                """);
        }

        (await AdoptAsync(provider, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.Created,
            "a clean new owner succeeds once the stalled INSERT trigger is removed");
        (await CountsAsync()).Should().Be((1L, 2L));
        (await AuditRowsAsync()).Should().ContainSingle(row => row.Outcome == "success");
    }

    private static async Task WaitForGlobalAuditSleepAsync(string connectionString, Task<IdentityBindingResult> running)
    {
        await using var connection = await OpenConnectionAsync(connectionString);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (running.IsCompleted)
            {
                await running;
                throw new InvalidOperationException("The identity owner completed before its audit INSERT was observed sleeping.");
            }
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                    WHERE datname = current_database() AND usename = 'learnstack_app'
                      AND wait_event = 'PgSleep' AND query LIKE '%INSERT INTO audit_log%')
                """, connection) { CommandTimeout = 1 };
            if (await command.ExecuteScalarAsync() is true) return;
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
        throw new TimeoutException("The real global audit INSERT did not reach its test-owned PostgreSQL sleep.");
    }
}
