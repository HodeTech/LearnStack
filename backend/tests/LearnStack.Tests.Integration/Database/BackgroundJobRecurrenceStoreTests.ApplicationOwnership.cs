using System.Collections;
using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Fact]
    public async Task Four_actual_recipient_owners_remain_bounded_and_excluded_after_expiry_while_other_recipients_progress()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
            await SeedLifecycleTenantAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA1, "Active", false, true);
        var today = await TodayAsync(database);
        var registry = Fixture.Recurrences(Enumerable.Range(0, 6).Select(index => Fixture.Manifest(
            today.AddDays(-1), today.AddDays(-1), key: "jobsrecurrencefixture.batch" + index,
            period: TimeSpan.FromDays(30))).ToArray());
        var bounds = new BackgroundJobRuntimeOptions
        {
            SqlTimeout = TimeSpan.FromSeconds(1),
            LeaseDuration = TimeSpan.FromSeconds(15),
            RenewalInterval = TimeSpan.FromSeconds(1),
            ProgressTimeout = TimeSpan.FromSeconds(30),
            ControlPoll = TimeSpan.FromMilliseconds(50),
            SnapshotInterval = TimeSpan.FromSeconds(1),
        };
        bounds.Validate();
        var barrier = new RecipientRuntimeBatch();
        await using var host = RecipientHost(database, new(), configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton<LearnStack.SharedKernel.Audit.IAuditCatalogSource>(new RecurrenceFixtureAuditSource());
            services.AddSingleton(barrier);
            services.AddScoped<SubmitBackgroundJobRecipientCommandHandler>();
            services.RemoveAll<IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>>();
            services.AddScoped<IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>, HeldRecipientRuntimeSubmission>();
        });
        await using var source = Source(database);
        using var globals = new RecurrenceGlobalFilters();
        var runtime = RecurrenceRuntime(host.Services, source, registry, bounds);
        var recurrence = NativeHoldField<BackgroundJobRecurrenceControl>(
            NativeHoldField<BackgroundJobControl>(runtime, "_jobs"), "_recurrence");
        BackgroundJobRecipientOperation[] originals = [];
        try
        {
            await runtime.StartAsync();
            await RecurrenceEventuallyAsync(() => Task.FromResult(barrier.Entries.Length >= 4));
            var first = barrier.Entries.Take(4).ToArray();
            first.Select(entry => entry.Command.Claim.RecipientId).Should().OnlyHaveUniqueItems();
            first.Select(entry => entry.Unit).Should().OnlyHaveUniqueItems();
            first.Select(entry => entry.Command.SourceOwner).Should().OnlyHaveUniqueItems();
            originals = first.Select(entry => ReadActualRecipient(recurrence, entry.Command.Claim.RecipientId.Value)).ToArray();
            originals.Should().OnlyHaveUniqueItems();
            foreach (var entry in first)
            {
                entry.Unit.HasActiveTransaction.Should().BeTrue();
                entry.Command.SourceOwner.CleanupConfirmed.Should().BeFalse();
            }
            await RecurrenceEventuallyAsync(async () =>
            {
                await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                return await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_job_recipients") == 6
                    && recurrence.Snapshot.RetainedApplications == 4;
            });
            var ids = first.Select(entry => entry.Command.Claim.RecipientId.Value).ToArray();
            // Observe real database-clock expiry of all four original leases.
            // No row is locked by the held decorator and no lease is rewritten.
            await RecurrenceEventuallyAsync(async () =>
            {
                await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                return await ReadAsync<long>(dispatcher, """
                    SELECT count(*) FROM background_job_recipients
                    WHERE id=ANY(@ids) AND ownership_until<=clock_timestamp()
                    """, ("ids", ids)) == 4;
            });
            barrier.Entries.Should().HaveCount(4, "the accepted ceiling is four actual application owners, including retained work");
            recurrence.Snapshot.ApplicationOwners.Should().Be(4);
            recurrence.Snapshot.SqlHealthy.Should().BeTrue();
            foreach (var operation in originals)
            {
                var actual = await NativeHoldField<TaskCompletionSource<Task<JobRecipientSubmissionResult>>>(operation, "_published")
                    .Task.WaitAsync(TimeSpan.FromSeconds(30));
                actual.IsCompleted.Should().BeFalse();
            }

            // The exact production selector, with only the exclusion removed,
            // must rank an original held row first. Full capacity prevents a
            // concurrent acquisition from racing this independent premise.
            var retainedId = await FirstOtherwiseEligibleRecipientAsync(database);
            first.Select(entry => entry.Command.Claim.RecipientId.Value).Should().Contain(retainedId);
            var held = first.Single(entry => entry.Command.Claim.RecipientId.Value == retainedId);
            var heldOperation = originals.Single(operation => operation.Claim.RecipientId.Value == retainedId);
            foreach (var entry in first.Where(entry => !ReferenceEquals(entry, held))) entry.Release.TrySetResult();
            var released = originals.Where(operation => !ReferenceEquals(operation, heldOperation)).ToArray();
            await Task.WhenAll(released.Select(operation => operation.Completion)).WaitAsync(TimeSpan.FromSeconds(30));
            await RecurrenceEventuallyAsync(() => Task.FromResult(released.All(operation =>
                !IsActualRecipientRetained(recurrence, operation))));
            await RecurrenceEventuallyAsync(async () =>
            {
                await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                return await ReadAsync<long>(dispatcher, """
                    SELECT count(*) FROM background_job_recipients r JOIN background_jobs j
                        ON j.id=r.accepted_job_id AND j.tenant_id=r.tenant_id AND j.recipient_id=r.id
                    WHERE r.id<>@retained AND r.accepted_at IS NOT NULL AND j.phase='completed'
                    """, ("retained", retainedId)) > 0;
            });
            await using (var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString))
                (await ReadAsync<bool>(dispatcher, """
                    SELECT ownership_token=@token AND ownership_until<=clock_timestamp()
                        AND accepted_job_id IS NULL AND skipped_at IS NULL
                    FROM background_job_recipients WHERE id=@id
                    """, ("id", retainedId), ("token", held.Command.Claim.Token))).Should().BeTrue(
                    "a free application slot must not reacquire the otherwise-first locally retained recipient");
            ReadActualRecipient(recurrence, retainedId).Should().BeSameAs(heldOperation);
            barrier.Entries.Count(entry => entry.Command.Claim.RecipientId.Value == retainedId).Should().Be(1);
            heldOperation.Completion.IsCompleted.Should().BeFalse();
            held.Release.TrySetResult();
            await heldOperation.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            heldOperation.Result!.Command!.Gate.CommitAcknowledged.Should().BeFalse(
                "late return of the original closed attempt cannot acquire a later child's ACK");
            heldOperation.Result.SourceOwner.CleanupConfirmed.Should().BeTrue();
            await RecurrenceEventuallyAsync(async () =>
            {
                await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
                return await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs WHERE phase='completed'") == 6;
            });
        }
        finally
        {
            barrier.ReleaseAll();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await runtime.StopAsync(cleanup.Token); await runtime.Completion.WaitAsync(cleanup.Token); }
            finally { await Task.WhenAll(originals.Select(operation => operation.Completion)).WaitAsync(TimeSpan.FromSeconds(30)); }
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
    }

    private static BackgroundJobRecipientOperation ReadActualRecipient(BackgroundJobRecurrenceControl recurrence, Guid id)
    {
        lock (NativeHoldField<object>(recurrence, "_sync"))
        {
            var pending = NativeHoldField<IDictionary>(recurrence, "_recipients")[id]
                ?? throw new InvalidOperationException("The actual recipient owner was not retained.");
            return (BackgroundJobRecipientOperation)pending.GetType()
                .GetProperty("Operation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pending)!;
        }
    }

    private static bool IsActualRecipientRetained(BackgroundJobRecurrenceControl recurrence, BackgroundJobRecipientOperation operation)
    {
        lock (NativeHoldField<object>(recurrence, "_sync"))
        {
            var pending = NativeHoldField<IDictionary>(recurrence, "_recipients")[operation.Claim.RecipientId.Value];
            return pending is not null && ReferenceEquals(operation, pending.GetType()
                .GetProperty("Operation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pending));
        }
    }

    private static async Task<Guid> FirstOtherwiseEligibleRecipientAsync(DisposableSchemaDatabase database)
    {
        var sql = (string)typeof(PostgresBackgroundJobControlStore)
            .GetField("RecipientClaimSelectionSql", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        sql = sql.Replace("AND r.id<>ALL(@retained)", "", StringComparison.Ordinal)
            .Replace(" FOR UPDATE OF r SKIP LOCKED", "", StringComparison.Ordinal);
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)dispatcher);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return reader.GetGuid(10);
    }

    private sealed class RecipientRuntimeBatch
    {
        private readonly object _sync = new();
        private readonly List<RecipientRuntimeEntry> _entries = [];
        private bool _released;
        internal RecipientRuntimeEntry[] Entries { get { lock (_sync) return _entries.ToArray(); } }
        internal RecipientRuntimeEntry Enter(SubmitBackgroundJobRecipientCommand command, IUnitOfWork unit)
        {
            lock (_sync)
            {
                var entry = new RecipientRuntimeEntry(command, unit);
                if (_released || _entries.Count >= 4) entry.Release.TrySetResult();
                _entries.Add(entry);
                return entry;
            }
        }
        internal void ReleaseAll()
        {
            lock (_sync)
            {
                _released = true;
                foreach (var entry in _entries) entry.Release.TrySetResult();
            }
        }
    }

    private sealed record RecipientRuntimeEntry(SubmitBackgroundJobRecipientCommand Command, IUnitOfWork Unit)
    {
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class HeldRecipientRuntimeSubmission(SubmitBackgroundJobRecipientCommandHandler actual,
        IUnitOfWork unit, RecipientRuntimeBatch barrier) : IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>
    {
        public async Task<Result<BackgroundJobId>> Handle(SubmitBackgroundJobRecipientCommand request, CancellationToken cancellationToken)
        {
            var entry = barrier.Enter(request, unit);
            // A real application task remains inside its original MediatR
            // transaction. Only this test barrier deliberately ignores stop.
            await entry.Release.Task.WaitAsync(TimeSpan.FromMinutes(2), CancellationToken.None);
            return await actual.Handle(request, cancellationToken);
        }
    }
}
