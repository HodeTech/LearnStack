using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Audit;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Successful-save production through the real interceptor, owner coordinator and
/// app-role outbox. Probe facts are raised on mapped roots; their notification
/// handler has only the production outbox and GUID factory.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DomainEventProductionTests(SchemaFixture schema)
{
    private const string Operation = "customization.content_type.domain_probe";
    private static readonly DateTimeOffset Occurred = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid HumanActor = new("12345678-1234-7123-8123-123456789abc");
    private static readonly Error Refusal = new(new LocalizedMessage("lockey_business_rule_violation"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_compositions_refuse_event_bearing_saves_without_an_enrolled_producer(bool api)
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var seed = SeedComposition.Build(dataSource, new ActingContext(), NullLoggerFactory.Instance);
        var observation = new DispatchObservation();
        await using var host = Host(schema.Postgres.AppConnectionString, observation);
        TenantContentType source;
        TenantContentType control;
        await using (var scope = (api ? host.Services : seed).CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var unit = services.GetRequiredService<IUnitOfWork>();
            await using var owner = await unit.BeginTransactionAsync();
            await unit.SetTenantContextAsync(new ActingContext());
            await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
            unit.GetTenantContextAnnouncement(unit.Transaction).Should().NotBeNull();
            services.GetRequiredService<ProducerExecution>().HasInvocation.Should().BeFalse();
            var db = services.GetRequiredService<CustomizationDbContext>();

            control = Source(services);
            db.Add(control);
            (await db.SaveChangesAsync()).Should().Be(1, "event-free saves do not require producer enrollment");
            (await CountSourceInUnitAsync(unit, control.Id.Value)).Should().Be(1);

            source = Source(services);
            Raise(source, Fact());
            db.Add(source);
            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*enrolled writable frame*");
            (await CountSourceInUnitAsync(unit, source.Id.Value)).Should().Be(0,
                "staging refuses the fact before its business INSERT");
            source.DomainEvents.Should().ContainSingle("an unsuccessful save cannot consume the fact");
            (await OutboxInUnitAsync(unit)).Should().BeEmpty();
            observation.Events.Should().BeEmpty();
        }

        source.DomainEvents.Should().BeEmpty("scoped cleanup discards the refused source fact");
        (await CountSourceAsync(schema.Postgres.AppConnectionString, source.Id.Value)).Should().Be(0);
        (await CountSourceAsync(schema.Postgres.AppConnectionString, control.Id.Value)).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_compositions_automatically_promote_a_successful_save_and_clear_on_refusal(bool api)
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var seed = SeedComposition.Build(dataSource, new ActingContext(), NullLoggerFactory.Instance);
        await using var host = Host(schema.Postgres.AppConnectionString, new DispatchObservation());
        await using var scope = (api ? host.Services : seed).CreateAsyncScope();
        var services = scope.ServiceProvider;
        TenantContentType? source = null;

        var result = await RunAsync(services, async () =>
        {
            var unit = services.GetRequiredService<IUnitOfWork>();
            await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
            var db = services.GetRequiredService<CustomizationDbContext>();
            source = Source(services);
            Raise(source, Fact());
            db.Add(source);
            CurrentInvocation(services).HasProducedWork.Should().BeFalse();
            (await db.SaveChangesAsync()).Should().Be(1);
            (await CountSourceInUnitAsync(unit, source.Id.Value)).Should().Be(1);
            CurrentInvocation(services).HasProducedWork.Should().BeTrue(
                "the registered SaveChanges interceptor records production without caller bookkeeping");
            source.DomainEvents.Should().ContainSingle("the owner has not reached its terminal boundary");
            (await OutboxInUnitAsync(unit)).Should().BeEmpty("a save collects; only successful owner completion dispatches");
            return Result<None>.Fail(Refusal);
        });

        result.Error.Should().BeSameAs(Refusal);
        source!.DomainEvents.Should().BeEmpty();
        (await CountSourceAsync(schema.Postgres.AppConnectionString, source.Id.Value)).Should().Be(0);
        AssertClean(services);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_successful_save_poison_receipt_reaches_every_absorbing_ancestor(int descendantDepth)
    {
        var observation = new DispatchObservation();
        await using var host = Host(schema.Postgres.AppConnectionString, observation);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var coordinator = services.GetRequiredService<TransactionExecutionCoordinator>();
        var unit = services.GetRequiredService<IUnitOfWork>();
        TenantContentType? source = null;
        var ancestors = new List<ProducerExecution.Invocation>();
        var observed = false;
        var absorbed = false;

        var run = () => RunAsync(services, async () =>
        {
            ancestors.Add(CurrentInvocation(services));
            var refused = await coordinator.ExecuteAsync(null, TransactionMode.ReadWrite, "refusing-ancestor", async () =>
            {
                ancestors.Add(CurrentInvocation(services));
                async Task<Result<None>> SaveDescendantAsync(int depth)
                {
                    if (depth != 0)
                    {
                        return await coordinator.ExecuteAsync(null, TransactionMode.ReadWrite, "successful-descendant", async () =>
                        {
                            ancestors.Add(CurrentInvocation(services));
                            return await SaveDescendantAsync(depth - 1);
                        });
                    }

                    source = Source(services);
                    Raise(source, Fact());
                    var db = services.GetRequiredService<CustomizationDbContext>();
                    db.Add(source);
                    await db.SaveChangesAsync();
                    (await CountSourceInUnitAsync(unit, source.Id.Value)).Should().Be(1);
                    observed = true;
                    ancestors.Should().OnlyContain(frame => frame.HasProducedWork,
                        "save promotion records a sticky receipt in all ancestor invocations");
                    return Result.Ok(None.Value);
                }

                (await SaveDescendantAsync(descendantDepth)).IsSuccess.Should().BeTrue();
                source!.DomainEvents.Should().ContainSingle("joiners neither dispatch nor clear the owner's sources");
                observation.Events.Should().BeEmpty();
                return Result<None>.Fail(Refusal);
            });

            refused.Error.Should().BeSameAs(Refusal);
            unit.IsRollbackOnly.Should().BeTrue("the refusal resolves only after its descendant receipt poisons the unit");
            absorbed = true;
            return Result.Ok(None.Value);
        });

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rollback-only*");
        observed.Should().BeTrue();
        absorbed.Should().BeTrue();
        observation.Events.Should().BeEmpty("rollback-only owners refuse before domain dispatch");
        source!.DomainEvents.Should().BeEmpty();
        (await CountSourceAsync(schema.Postgres.AppConnectionString, source.Id.Value)).Should().Be(0);
        AssertClean(services);
    }

    [Fact]
    public async Task The_owner_drains_FIFO_across_sources_saves_delete_and_detach_without_duplicates()
    {
        await using var database = await DatabaseAsync();
        var observation = new DispatchObservation();
        await using var host = Host(database.AppConnectionString, observation);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var facts = Enumerable.Range(0, 5).Select(_ => Fact()).ToArray();
        TenantContentType? first = null;
        TenantContentType? second = null;
        long returnedVersion = -1;

        var result = await RunAsync(services, async () =>
        {
            var db = services.GetRequiredService<CustomizationDbContext>();
            first = Source(services);
            second = Source(services);
            Raise(first, facts[0]);
            Raise(first, facts[1]);
            Raise(second, facts[2]);
            db.Add(first);
            db.Add(second);
            (await db.SaveChangesAsync()).Should().Be(2);
            (await db.SaveChangesAsync()).Should().Be(0);
            await services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                null, TransactionMode.ReadWrite, "event-free-joiner", () => Task.FromResult(Result.Ok(None.Value)));
            first.DomainEvents.Should().HaveCount(2);
            second.DomainEvents.Should().ContainSingle();
            observation.Events.Should().BeEmpty("the successful joiner cannot drain or clear the owner queue");

            Raise(first, facts[3]);
            Modify(first, services);
            (await db.SaveChangesAsync()).Should().Be(1);
            Modify(first, services);
            (await db.SaveChangesAsync()).Should().Be(1,
                "another real save of the retained source must not duplicate its original facts");
            returnedVersion = first.Version;
            Raise(second, facts[4]);
            db.Remove(second);
            (await db.SaveChangesAsync()).Should().Be(1);
            db.Entry(second).State.Should().Be(EntityState.Detached);
            second.DomainEvents.Should().HaveCount(2, "delete cannot erase a successfully collected source");
            db.Entry(first).State = EntityState.Detached;
            first.DomainEvents.Should().HaveCount(3);
            return Result.Ok(returnedVersion);
        });

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(returnedVersion);
        observation.Events.Select(e => e.SubjectId).Should().Equal(facts.Select(e => e.SubjectId));
        var rows = await OutboxAsync(database.AppConnectionString);
        rows.Select(row => row.CausationId).Should().BeEquivalentTo(facts.Select(e => (Guid?)e.EventId));
        rows.Should().HaveCount(5);
        first!.DomainEvents.Should().BeEmpty();
        second!.DomainEvents.Should().BeEmpty();
        first.Version.Should().Be(returnedVersion, "outbox-only reactions leave the returned version accurate");
        (await VersionAsync(database.AppConnectionString, first.Id.Value)).Should().Be(returnedVersion);
        (await CountSourceAsync(database.AppConnectionString, second.Id.Value)).Should().Be(0);
        AssertClean(services);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_detached_source_preserves_its_saved_fact_without_hitchhiking_a_later_unsaved_fact(bool reattach)
    {
        await using var database = await DatabaseAsync();
        var observation = new DispatchObservation();
        await using var host = Host(database.AppConnectionString, observation);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var saved = Fact();
        var detached = Fact();
        TenantContentType? source = null;

        (await RunAsync(services, async () =>
        {
            var db = services.GetRequiredService<CustomizationDbContext>();
            source = Source(services);
            Raise(source, saved);
            db.Add(source);
            await db.SaveChangesAsync();
            db.Entry(source).State = EntityState.Detached;
            Raise(source, detached);
            var other = Source(services);
            db.Add(other);
            (await db.SaveChangesAsync()).Should().Be(1);
            source.DomainEvents.Should().HaveCount(2, "pending facts stay on retained sources until owner completion");
            if (reattach)
            {
                db.Attach(source);
                Modify(source, services);
                (await db.SaveChangesAsync()).Should().Be(1);
            }
            return Result.Ok(None.Value);
        })).IsSuccess.Should().BeTrue();

        Guid[] expectedSubjects = reattach ? [saved.SubjectId, detached.SubjectId] : [saved.SubjectId];
        observation.Events.Select(e => e.SubjectId).Should().Equal(expectedSubjects);
        (await OutboxAsync(database.AppConnectionString)).Select(row => row.CausationId).Should().BeEquivalentTo(
            reattach ? new Guid?[] { saved.EventId, detached.EventId } : new Guid?[] { saved.EventId });
        source!.DomainEvents.Should().BeEmpty("cleanup retains detached sources even when their later fact was never promoted");
        AssertClean(services);
    }

    [Fact]
    public async Task An_unchanged_source_receives_nothing_from_a_zero_save_or_another_sources_save()
    {
        await using var database = await DatabaseAsync();
        var observation = new DispatchObservation();
        await using var host = Host(database.AppConnectionString, observation);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var fact = Fact();

        (await RunAsync(services, async () =>
        {
            var db = services.GetRequiredService<CustomizationDbContext>();
            var source = Source(services);
            db.Add(source);
            await db.SaveChangesAsync();
            Raise(source, fact);
            (await db.SaveChangesAsync()).Should().Be(0);
            CurrentInvocation(services).HasProducedWork.Should().BeFalse(
                "an unsaved raise is not a successful-save receipt");
            db.Add(Source(services));
            (await db.SaveChangesAsync()).Should().Be(1);
            CurrentInvocation(services).HasProducedWork.Should().BeFalse(
                "saving another source cannot promote events on an unchanged source");
            source.DomainEvents.Should().ContainSingle();
            Modify(source, services);
            (await db.SaveChangesAsync()).Should().Be(1);
            CurrentInvocation(services).HasProducedWork.Should().BeTrue();
            return Result.Ok(None.Value);
        })).IsSuccess.Should().BeTrue();

        observation.Events.Select(e => e.SubjectId).Should().Equal(fact.SubjectId);
        (await OutboxAsync(database.AppConnectionString)).Should().ContainSingle()
            .Which.CausationId.Should().Be(fact.EventId);
        AssertClean(services);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Failed_or_canceled_saves_promote_nothing_and_a_retry_belongs_to_its_saving_invocation(bool cancel, bool retry)
    {
        await using var database = await DatabaseAsync();
        var observation = new DispatchObservation();
        await using var host = Host(database.AppConnectionString, observation);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        var coordinator = services.GetRequiredService<TransactionExecutionCoordinator>();
        var fact = Fact();
        ProducerExecution.Invocation? failedInvocation = null;
        string? retryTrace = null;
        TenantContentType? source = null;

        (await RunAsync(services, async () =>
        {
            var db = services.GetRequiredService<CustomizationDbContext>();
            var incumbent = Source(services);
            db.Add(incumbent);
            await db.SaveChangesAsync();
            // Remove EF's alternate-key identity collision while keeping the real
            // conflicting row in PostgreSQL: the failure below must reach SQL.
            db.Entry(incumbent).State = EntityState.Detached;
            source = Source(services, incumbent.Key);
            Raise(source, fact);
            db.Add(source);
            var failed = await coordinator.ExecuteAsync(null, TransactionMode.ReadWrite, "failed-save", async () =>
            {
                failedInvocation = CurrentInvocation(services);
                if (cancel)
                {
                    using var cancellation = new CancellationTokenSource();
                    await cancellation.CancelAsync();
                    var save = () => db.SaveChangesAsync(cancellation.Token);
                    await save.Should().ThrowAsync<OperationCanceledException>();
                }
                else
                {
                    var save = () => db.SaveChangesAsync();
                    (await save.Should().ThrowAsync<DbUpdateException>()).Which.InnerException
                        .Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
                }
                failedInvocation.HasProducedWork.Should().BeFalse();
                return Result<None>.Fail(Refusal);
            });
            failed.Error.Should().BeSameAs(Refusal);
            unit.IsRollbackOnly.Should().BeFalse("a caught failed save with no promotion is an ordinary absorbable refusal");
            CurrentInvocation(services).HasProducedWork.Should().BeFalse();
            (await CountSourceInUnitAsync(unit, source.Id.Value)).Should().Be(0);
            source.DomainEvents.Should().ContainSingle("failed save does not erase the aggregate's pending fact");

            // A changed, unrelated source is a positive persistence control. The failed
            // source stays retained for cleanup but is ineligible while detached.
            db.Entry(source).State = EntityState.Detached;
            db.Add(Source(services));
            (await db.SaveChangesAsync()).Should().Be(1);
            CurrentInvocation(services).HasProducedWork.Should().BeFalse();
            if (retry)
            {
                using var activity = new Activity("successful-save-retry").SetIdFormat(ActivityIdFormat.W3C).Start();
                retryTrace = activity.Id;
                var successful = await coordinator.ExecuteAsync(null, TransactionMode.ReadWrite, "successful-retry", async () =>
                {
                    db.Add(source);
                    db.Entry(source).Property(entity => entity.Key).CurrentValue = Key(source.Id);
                    (await db.SaveChangesAsync()).Should().Be(1);
                    CurrentInvocation(services).HasProducedWork.Should().BeTrue();
                    return Result.Ok(None.Value);
                });
                successful.IsSuccess.Should().BeTrue();
                failedInvocation!.HasProducedWork.Should().BeFalse("attribution belongs to the successful saving invocation");
            }
            return Result.Ok(None.Value);
        })).IsSuccess.Should().BeTrue();

        source!.DomainEvents.Should().BeEmpty();
        var rows = await OutboxAsync(database.AppConnectionString);
        if (retry)
        {
            observation.Events.Select(e => e.SubjectId).Should().Equal(fact.SubjectId);
            rows.Should().ContainSingle().Which.CorrelationId.Should().Be(retryTrace);
            rows[0].CausationId.Should().Be(fact.EventId);
        }
        else
        {
            observation.Events.Should().BeEmpty();
            rows.Should().BeEmpty("the unsuccessful event is not dispatched by an unrelated successful save");
        }
        AssertClean(services);
    }

    [Fact]
    public async Task Exactly_256_successfully_saved_domain_events_can_complete_one_transaction()
    {
        await using var database = await DatabaseAsync();
        var observation = new DispatchObservation();
        await using var host = Host(database.AppConnectionString, observation);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var facts = Enumerable.Range(0, 256).Select(_ => Fact()).ToArray();

        (await RunAsync(services, async () =>
        {
            var source = Source(services);
            foreach (var fact in facts) Raise(source, fact);
            var db = services.GetRequiredService<CustomizationDbContext>();
            db.Add(source);
            (await db.SaveChangesAsync()).Should().Be(1);
            return Result.Ok(None.Value);
        })).IsSuccess.Should().BeTrue();

        observation.Events.Select(e => e.SubjectId).Should().Equal(facts.Select(e => e.SubjectId));
        (await OutboxAsync(database.AppConnectionString)).Should().HaveCount(256);
        AssertClean(services);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Event_257_rolls_back_the_whole_unit_even_when_its_overflow_is_absorbed(bool absorb)
    {
        var observation = new DispatchObservation();
        await using var host = Host(schema.Postgres.AppConnectionString, observation);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        TenantContentType? source = null;
        var observed = false;
        var caught = false;

        var run = () => RunAsync(services, async () =>
        {
            source = Source(services);
            var db = services.GetRequiredService<CustomizationDbContext>();
            foreach (var fact in Enumerable.Range(0, 256).Select(_ => Fact())) Raise(source, fact);
            db.Add(source);
            await db.SaveChangesAsync();
            (await CountSourceInUnitAsync(unit, source.Id.Value)).Should().Be(1);
            observed = true;

            async Task<Result<None>> OverflowAsync()
            {
                Raise(source, Fact());
                Modify(source, services);
                await db.SaveChangesAsync();
                return Result.Ok(None.Value);
            }

            if (absorb)
            {
                // Catch inside the joined invocation: a coordinator catch alone
                // cannot supply the overflow's rollback-only guarantee.
                (await services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                    null, TransactionMode.ReadWrite, "absorbed-overflow", async () =>
                    {
                        var overflow = OverflowAsync;
                        await overflow.Should().ThrowAsync<InvalidOperationException>();
                        caught = true;
                        unit.IsRollbackOnly.Should().BeTrue();
                        return Result.Ok(None.Value);
                    })).IsSuccess.Should().BeTrue();
                return Result.Ok(None.Value);
            }
            return await OverflowAsync();
        });

        await run.Should().ThrowAsync<InvalidOperationException>();
        observed.Should().BeTrue();
        caught.Should().Be(absorb);
        observation.Events.Should().BeEmpty();
        source!.DomainEvents.Should().BeEmpty();
        (await CountSourceAsync(schema.Postgres.AppConnectionString, source.Id.Value)).Should().Be(0);
        AssertClean(services);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_cancellation_or_a_dispatch_fault_clears_all_sources_and_refuses_poisoned_scope_reuse(bool cancel)
    {
        await using var database = await DatabaseAsync();
        var observation = new DispatchObservation();
        await using var host = Host(database.AppConnectionString, observation);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        using var cancellation = new CancellationTokenSource();
        var first = Fact();
        var fault = Fact(fail: true);
        var neverReached = Fact();
        TenantContentType? source = null;
        CustomizationDbContext? db = null;
        var observed = false;

        var run = () => RunAsync(services, async () =>
        {
            db = services.GetRequiredService<CustomizationDbContext>();
            source = Source(services);
            Raise(source, first);
            Raise(source, fault);
            Raise(source, neverReached);
            db.Add(source);
            await db.SaveChangesAsync();
            (await CountSourceInUnitAsync(unit, source.Id.Value)).Should().Be(1);
            observed = true;
            if (cancel) await cancellation.CancelAsync();
            return Result.Ok(None.Value);
        }, cancellationToken: cancellation.Token);

        if (cancel) await run.Should().ThrowAsync<OperationCanceledException>();
        else await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("Deliberate domain dispatch failure.");
        observed.Should().BeTrue();
        observation.Events.Should().HaveCount(cancel ? 0 : 1);
        source!.DomainEvents.Should().BeEmpty();
        (await CountSourceAsync(database.AppConnectionString, source.Id.Value)).Should().Be(0);
        (await OutboxAsync(database.AppConnectionString)).Should().BeEmpty();
        AssertClean(services);

        var reached = false;
        var reopen = () => RunAsync(services, () =>
        {
            reached = true;
            return Task.FromResult(Result.Ok(None.Value));
        });
        await reopen.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rollback-only*");
        reached.Should().BeFalse("ADR-0040 makes poison irreversible for this unit's lifetime");
        AssertClean(services);
    }

    [Fact]
    public async Task A_successful_owner_can_save_again_in_the_same_DI_scope_without_replaying_cleared_facts()
    {
        await using var database = await DatabaseAsync();
        var observation = new DispatchObservation();
        await using var host = Host(database.AppConnectionString, observation);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        var first = Fact();
        var later = Fact();
        TenantContentType? source = null;
        CustomizationDbContext? db = null;
        long generation = 0;

        (await RunAsync(services, async () =>
        {
            generation = unit.GetTenantContextAnnouncement(unit.Transaction)!.Generation;
            db = services.GetRequiredService<CustomizationDbContext>();
            source = Source(services);
            Raise(source, first);
            db.Add(source);
            await db.SaveChangesAsync();
            return Result.Ok(None.Value);
        })).IsSuccess.Should().BeTrue();
        source!.DomainEvents.Should().BeEmpty();
        AssertClean(services);

        (await RunAsync(services, async () =>
        {
            unit.GetTenantContextAnnouncement(unit.Transaction)!.Generation.Should().BeGreaterThan(generation);
            db!.Database.UseTransaction(unit.Transaction);
            Raise(source, later);
            Modify(source, services);
            (await db.SaveChangesAsync()).Should().Be(1);
            return Result.Ok(None.Value);
        })).IsSuccess.Should().BeTrue();

        observation.Events.Select(e => e.SubjectId).Should().Equal(first.SubjectId, later.SubjectId);
        (await OutboxAsync(database.AppConnectionString)).Select(row => row.CausationId)
            .Should().BeEquivalentTo(new Guid?[] { first.EventId, later.EventId });
        source.DomainEvents.Should().BeEmpty();
        AssertClean(services);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_domain_outbox_and_MUST_audit_commit_together_or_rollback_together(bool failAudit)
    {
        await using var database = await DatabaseAsync();
        if (failAudit)
        {
            await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
            await using var deny = new NpgsqlCommand(
                "ALTER TABLE audit_log ADD CONSTRAINT reject_domain_probe CHECK (operation <> 'customization.content_type.domain_probe')", owner);
            await deny.ExecuteNonQueryAsync();
        }
        var observation = new DispatchObservation();
        await using var host = Host(database.AppConnectionString, observation);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        TenantContentType? source = null;
        var observed = false;
        observation.AfterInsert = async () =>
        {
            (await CountSourceInUnitAsync(unit, source!.Id.Value)).Should().Be(1);
            (await OutboxInUnitAsync(unit)).Should().ContainSingle();
            (await ScalarInUnitAsync(unit, "SELECT count(*) FROM audit_log")).Should().Be(0L,
                "domain completion precedes MUST audit composition and INSERT");
            observed = true;
        };

        var run = () => RunAsync(services, async () =>
        {
            var db = services.GetRequiredService<CustomizationDbContext>();
            source = Source(services);
            Raise(source, Fact());
            db.Add(source);
            await db.SaveChangesAsync();
            return Result.Ok(None.Value);
        }, audited: true);

        if (failAudit)
        {
            (await run.Should().ThrowAsync<AuditWriteFailedException>()).Which.InnerException
                .Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        }
        else
        {
            (await run()).IsSuccess.Should().BeTrue();
        }
        observed.Should().BeTrue("both rows existed on the app-role business transaction before MUST audit INSERT");
        source!.DomainEvents.Should().BeEmpty();
        (await CountSourceAsync(database.AppConnectionString, source.Id.Value)).Should().Be(failAudit ? 0 : 1);
        (await OutboxAsync(database.AppConnectionString)).Should().HaveCount(failAudit ? 0 : 1);
        (await AuditCountAsync(database.AppConnectionString)).Should().Be(failAudit ? 0 : 1);
        AssertClean(services);
    }

    [Fact]
    public async Task A_disposed_child_activity_preserves_exact_save_origin_causation_and_causal_actor_until_commit()
    {
        await using var database = await DatabaseAsync();
        var observation = new DispatchObservation();
        var context = new ActingContext { UserId = UserId.SystemActor, CausalActorUserId = UserId.From(HumanActor) };
        await using var host = Host(database.AppConnectionString, observation, context);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var fact = Fact();
        string? originTrace = null;
        string? ownerTrace = null;
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            (await RunAsync(services, async () =>
            {
                ownerTrace = Activity.Current!.Id;
                using (var child = new Activity("domain-save-child").SetIdFormat(ActivityIdFormat.W3C).Start())
                {
                    originTrace = child.Id;
                    (await services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                        null, TransactionMode.ReadWrite, "child-producer", async () =>
                        {
                            var source = Source(services);
                            Raise(source, fact);
                            var db = services.GetRequiredService<CustomizationDbContext>();
                            db.Add(source);
                            await db.SaveChangesAsync();
                            return Result.Ok(None.Value);
                        })).IsSuccess.Should().BeTrue();
                }
                Activity.Current!.Id.Should().Be(ownerTrace);
                observation.Events.Should().BeEmpty();
                context.CausalActorUserId = UserId.From(Guid.CreateVersion7());
                return Result.Ok(None.Value);
            })).IsSuccess.Should().BeTrue();
            Activity.Current.Should().BeNull();
        }
        finally
        {
            Activity.Current = previous;
        }

        var row = (await OutboxAsync(database.AppConnectionString)).Should().ContainSingle().Subject;
        row.CorrelationId.Should().Be(originTrace).And.NotBe(ownerTrace);
        ActivityContext.TryParse(row.CorrelationId, null, out _).Should().BeTrue();
        row.CausationId.Should().Be(fact.EventId);
        row.ActorUserId.Should().Be(HumanActor, "the trusted entry captures the causal human while effective execution is SystemActor");
        row.EventId.Should().NotBe(fact.EventId, "translation allocates a distinct integration event identity");
        row.Type.Should().Be(typeof(ProbeIntegrationEventV1).AssemblyQualifiedName);
        row.PartitionKey.Should().Be(fact.SubjectId.ToString());
        row.MetadataIsSqlNull.Should().BeTrue();
        JsonSerializer.Deserialize<ProbeIntegrationEventV1>(row.Payload, IntegrationEventBase.PayloadJsonOptions)!
            .SubjectId.Should().Be(fact.SubjectId);
        AssertClean(services);
    }

    [Fact]
    public async Task Owner_drain_does_not_rescan_a_source_for_an_unsaved_recursive_fact()
    {
        await using var database = await DatabaseAsync();
        var observation = new DispatchObservation();
        await using var host = Host(database.AppConnectionString, observation);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var saved = Fact();
        var unsaved = Fact();
        TenantContentType? source = null;
        observation.AfterInsert = () =>
        {
            // The probe handler still only enqueues. The observing adapter injects
            // an unsaved fact to falsify a drain that repeatedly scans source lists.
            Raise(source!, unsaved);
            return Task.CompletedTask;
        };

        (await RunAsync(services, async () =>
        {
            source = Source(services);
            Raise(source, saved);
            var db = services.GetRequiredService<CustomizationDbContext>();
            db.Add(source);
            await db.SaveChangesAsync();
            return Result.Ok(None.Value);
        })).IsSuccess.Should().BeTrue();

        observation.Events.Select(e => e.SubjectId).Should().Equal(saved.SubjectId);
        (await OutboxAsync(database.AppConnectionString)).Should().ContainSingle()
            .Which.CausationId.Should().Be(saved.EventId);
        source!.DomainEvents.Should().BeEmpty();
        AssertClean(services);
    }

    [Fact]
    public async Task Disposing_an_unfinished_scope_discards_collected_and_uncollected_source_facts()
    {
        var observation = new DispatchObservation();
        await using var host = Host(schema.Postgres.AppConnectionString, observation);
        TenantContentType source;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var unit = services.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unit.BeginTransactionAsync();
            await unit.SetTenantContextAsync(new ActingContext());
            using var invocation = (ProducerExecution.Invocation)typeof(ProducerExecution)
                .GetMethod("BeginInvocation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(services.GetRequiredService<ProducerExecution>(), [true, null, null])!;
            source = Source(services);
            var db = services.GetRequiredService<CustomizationDbContext>();
            db.Add(source);
            Raise(source, Fact());
            await db.SaveChangesAsync();
            CurrentInvocation(services).HasProducedWork.Should().BeTrue();
            db.Entry(source).State = EntityState.Detached;
            Raise(source, Fact());
            source.DomainEvents.Should().HaveCount(2);
            (await CountSourceInUnitAsync(unit, source.Id.Value)).Should().Be(1);
        }

        source.DomainEvents.Should().BeEmpty("the scoped collector disposes retained sources even without coordinator completion");
        observation.Events.Should().BeEmpty();
        (await CountSourceAsync(schema.Postgres.AppConnectionString, source.Id.Value)).Should().Be(0);
    }

    private static WebApplicationFactory<Program> Host(
        string connectionString, DispatchObservation observation, ITenantContext? context = null) =>
        new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(context ?? new ActingContext()));
                services.AddScoped<INotificationHandler<ProbeDomainEvent>, ProbeNotificationHandler>();
                services.AddScoped<IOutbox>(provider => new ObservedOutbox(
                    new PostgresOutbox(provider.GetRequiredService<IUnitOfWork>(), provider.GetRequiredService<ProducerExecution>()),
                    observation));
            });
        });

    private static Task<TResponse> RunAsync<TResponse>(IServiceProvider services, Func<Task<TResponse>> body,
        bool audited = false, CancellationToken cancellationToken = default)
        where TResponse : IResultBase
    {
        var audit = new AuditExecutionCoordinator(new ProbeCatalog(services.GetRequiredService<IAuditCatalog>()),
            services.GetRequiredService<IAuditConfigService>(), services.GetRequiredService<IAuditStateCapture>(),
            services.GetRequiredService<IAuditStore>(), services.GetRequiredService<ITenantContext>(),
            services.GetRequiredService<IClock>(), services.GetRequiredService<IGuidFactory>(),
            NullLogger<AuditExecutionCoordinator>.Instance);
        return audit.ExecuteAsync(audited ? typeof(AuditedProbe) : typeof(SilentProbe), null,
            () => services.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(
                null, TransactionMode.ReadWrite, "domain-production-proof", body, cancellationToken), cancellationToken);
    }

    private static TenantContentType Source(IServiceProvider services, string? key = null)
    {
        var id = TenantContentTypeId.From(Guid.CreateVersion7());
        return TenantContentType.Create(id, TenantId.From(SchemaFixture.TenantA), key ?? Key(id), 1,
            LocalizedText.From(("en", "Domain producer probe")), """{"type":"object"}""", "default-card",
            services.GetRequiredService<IClock>(), UserId.From(SchemaFixture.Actor));
    }

    private static string Key(TenantContentTypeId id) => $"domain-{id.Value:N}"[..24];

    private static void Modify(TenantContentType source, IServiceProvider services) =>
        source.Rename(LocalizedText.From(("en", $"Revision {source.Version}")),
            services.GetRequiredService<IClock>(), UserId.From(SchemaFixture.Actor));

    private static ProbeDomainEvent Fact(bool fail = false) => new()
    {
        EventId = Guid.CreateVersion7(),
        OccurredAt = Occurred,
        SubjectId = Guid.CreateVersion7(),
        FailOnDispatch = fail,
    };

    private static void Raise(TenantContentType source, IDomainEvent fact) =>
        typeof(Entity<TenantContentTypeId>).GetMethod("RaiseDomainEvent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(source, [fact]);

    private static ProducerExecution.Invocation CurrentInvocation(IServiceProvider services) =>
        (ProducerExecution.Invocation)typeof(ProducerExecution).GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(services.GetRequiredService<ProducerExecution>())!;

    private static void AssertClean(IServiceProvider services)
    {
        var unit = services.GetRequiredService<IUnitOfWork>();
        unit.Transaction.Should().BeNull();
        unit.GetTenantContextAnnouncement(unit.Transaction).Should().BeNull();
        services.GetRequiredService<ProducerExecution>().HasInvocation.Should().BeFalse();
        var capture = services.GetRequiredService<IAuditStateCapture>();
        capture.HasOpenFrame.Should().BeFalse();
        capture.Intents.Should().BeEmpty();
        capture.Changes.Should().BeEmpty();
    }

    private async Task<DisposableSchemaDatabase> DatabaseAsync()
    {
        var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        try
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await connection.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
                VALUES (@id, 'domain-probe', 'Domain probe', 'Trial', now(), @actor, 0)
                """, connection, transaction);
            command.Parameters.AddWithValue("id", SchemaFixture.TenantA);
            command.Parameters.AddWithValue("actor", SchemaFixture.Actor);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task<object?> ScalarInUnitAsync(IUnitOfWork unit, string sql)
    {
        await using var command = unit.Connection.CreateCommand();
        command.Transaction = unit.Transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static Task<long> CountSourceInUnitAsync(IUnitOfWork unit, Guid id) =>
        SourceScalarAsync((NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction,
            "SELECT count(*) FROM tenant_content_types WHERE id = @id", id);

    private static Task<long> CountSourceAsync(string connectionString, Guid id) =>
        SourceScalarAsync(connectionString, "SELECT count(*) FROM tenant_content_types WHERE id = @id", id);

    private static Task<long> VersionAsync(string connectionString, Guid id) =>
        SourceScalarAsync(connectionString, "SELECT row_version FROM tenant_content_types WHERE id = @id", id);

    private static Task<long> AuditCountAsync(string connectionString) =>
        SourceScalarAsync(connectionString, "SELECT count(*) FROM audit_log WHERE operation = 'customization.content_type.domain_probe'", Guid.Empty);

    private static async Task<long> SourceScalarAsync(string connectionString, string sql, Guid id)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        return await SourceScalarAsync(connection, transaction, sql, id);
    }

    private static async Task<long> SourceScalarAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static Task<IReadOnlyList<OutboxRow>> OutboxInUnitAsync(IUnitOfWork unit) =>
        OutboxAsync((NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);

    private static async Task<IReadOnlyList<OutboxRow>> OutboxAsync(string connectionString)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);
        return await OutboxAsync(connection, transaction);
    }

    private static async Task<IReadOnlyList<OutboxRow>> OutboxAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, correlation_id, causation_id, actor_user_id, type, partition_key, payload::text, metadata IS NULL
            FROM outbox_messages WHERE type = @type
            """, connection, transaction);
        command.Parameters.AddWithValue("type", typeof(ProbeIntegrationEventV1).AssemblyQualifiedName!);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<OutboxRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRow(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetBoolean(7)));
        }
        return rows;
    }

    private sealed record ProbeDomainEvent : DomainEvent
    {
        public required Guid SubjectId { get; init; }
        public bool FailOnDispatch { get; init; }
    }

    [IntegrationEventPartition(nameof(SubjectId))]
    private sealed record ProbeIntegrationEventV1 : IntegrationEventBase
    {
        public required Guid SubjectId { get; init; }
        public override string Topic => "learnstack.customization.contenttype";
        public override string PartitionKey => SubjectId.ToString();
    }

    private sealed class ProbeNotificationHandler(IOutbox outbox, IGuidFactory guids) : INotificationHandler<ProbeDomainEvent>
    {
        public Task Handle(ProbeDomainEvent notification, CancellationToken cancellationToken)
        {
            if (notification.FailOnDispatch) throw new InvalidOperationException("Deliberate domain dispatch failure.");
            return outbox.EnqueueAsync(new ProbeIntegrationEventV1
            {
                EventId = guids.NewUuidV7(),
                TenantId = SchemaFixture.TenantA,
                OccurredAt = notification.OccurredAt,
                SubjectId = notification.SubjectId,
            }, cancellationToken);
        }
    }

    private sealed class DispatchObservation
    {
        public List<ProbeIntegrationEventV1> Events { get; } = [];
        public Func<Task>? AfterInsert { get; set; }
    }

    private sealed class ObservedOutbox(IOutbox actual, DispatchObservation observation) : IOutbox
    {
        public async Task EnqueueAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            await actual.EnqueueAsync(integrationEvent, cancellationToken);
            observation.Events.Add((ProbeIntegrationEventV1)integrationEvent);
            if (observation.AfterInsert is not null) await observation.AfterInsert();
        }
    }

    private sealed record OutboxRow(Guid EventId, string CorrelationId, Guid? CausationId, Guid? ActorUserId,
        string Type, string PartitionKey, string Payload, bool MetadataIsSqlNull);

    private sealed class ActingContext : ITenantContext
    {
        public bool IsResolved => true;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public TenantId TenantId => TenantId.From(SchemaFixture.TenantA);
        public OrganizationId? OrganizationId => null;
        public UserId? UserId { get; init; } = SharedKernel.Identifiers.UserId.From(HumanActor);
        public UserId? CausalActorUserId { get; set; }
        public string? CorrelationId => "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        public string? ModuleName => "customization";
    }

    private sealed class AuditedProbe;
    private sealed class SilentProbe;

    private sealed class ProbeCatalog(IAuditCatalog actual) : IAuditCatalog
    {
        public bool TryGet(Type requestType, out AuditRegistration registration)
        {
            if (requestType == typeof(AuditedProbe))
            {
                registration = new AuditRegistration(false,
                    [new AuditCatalogEntry("customization", Operation, OperationType.Create, OperationClass.Must, typeof(TenantContentType))]);
                return true;
            }
            if (requestType == typeof(SilentProbe))
            {
                registration = new AuditRegistration(true, []);
                return true;
            }
            return actual.TryGet(requestType, out registration);
        }

        public bool TryGetOffPath(string operation, out AuditCatalogEntry entry) => actual.TryGetOffPath(operation, out entry);
        public IReadOnlyCollection<AuditCatalogEntry> All => actual.All;
    }
}
