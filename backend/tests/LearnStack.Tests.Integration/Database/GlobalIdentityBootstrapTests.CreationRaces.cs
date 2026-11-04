using System.Diagnostics;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class GlobalIdentityBootstrapTests
{
    [Fact]
    public async Task Competing_creation_debits_wait_then_freshly_observe_one_remaining_shared_slot()
    {
        await using var provider = SelfProvider();
        await QualifyAsync(provider);
        await using var holder = await OpenConnectionAsync(_database.MigrationConnectionString);
        await using var transaction = await holder.BeginTransactionAsync();
        await using (var prepare = new NpgsqlCommand("""
            UPDATE public.identity_creation_capacity SET minute_attempts=9,day_attempts=9,
                minute_window=(date_trunc('minute',clock_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC') + interval '2 minutes'
            """, holder, transaction))
            await prepare.ExecuteNonQueryAsync();

        var other = new GlobalIdentitySubject(AuthorityId, Registration.Issuer, "competing-creation-subject-canary");
        var first = BootstrapAsync(provider, Subject);
        var second = BootstrapAsync(provider, other);
        IdentityBootstrapResult[] outcomes;
        try
        {
            await WaitForCreationDebitWaitersAsync();
            await transaction.CommitAsync();
            outcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            // Release the real blocker even if the waiter observation fails, and
            // join both actual operations before the disposable database closes.
            await transaction.DisposeAsync();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        }

        outcomes.Should().ContainSingle(result => result.Binding.Outcome == IdentityBindingOutcome.Created);
        outcomes.Should().ContainSingle(result => result.Refusal == IdentityCreationRefusal.WindowExhausted);
        (await CreationCountersAsync()).Should().Be((10, 10, 1L));
        var a = await CountsAsync(Subject);
        var b = await CountsAsync(other);
        (a.Users + b.Users, a.Audits + b.Audits).Should().Be((1L, 1L));
    }

    private async Task WaitForCreationDebitWaitersAsync()
    {
        // pg_stat_activity reveals statement text only for the caller's role.
        // Observe the two real app owners as that same non-bypass role.
        await using var observer = await OpenConnectionAsync(_database.AppConnectionString);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(1500))
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM pg_catalog.pg_stat_activity
                WHERE datname=current_database() AND wait_event_type='Lock'
                    AND query LIKE '%FROM public.fn_identity_creation_attempt_admission()%'
                """, observer);
            if (await command.ExecuteScalarAsync() is long count && count >= 2) return;
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
        throw new InvalidOperationException("Both actual creation debit owners must wait on the held capacity row.");
    }
}
