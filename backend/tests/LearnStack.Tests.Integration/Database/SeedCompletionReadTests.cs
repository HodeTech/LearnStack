using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using LearnStack.Application.Messaging;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Application-role completion-owner proofs; fixture sources confer no production origin authority.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class SeedCompletionReadTests(SchemaFixture schema)
{
    private static readonly string[] ScopedWriteStatements = ["UPDATE tenant_settings SET value = value", "DELETE FROM tenant_settings"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_same_RW_owner_restores_scope_before_releasing_either_verification_result(bool admitted)
    {
        TenantContextAnnouncement? announced = null;
        DbTransaction? transaction = null;
        await using var provider = Provider(async (services, witness, ct) =>
        {
            var unit = services.GetRequiredService<IUnitOfWork>();
            var db = services.GetRequiredService<TenancyDbContext>();
            unit.Mode.Should().Be(TransactionMode.ReadWrite);
            unit.Transaction.Should().BeSameAs(transaction);
            unit.GetTenantContextAnnouncement(transaction).Should().BeSameAs(announced);
            ((IOperationReadScopeView)unit).IsTenantWideRead.Should().BeTrue();
            db.CurrentTenantWideRead.Should().BeTrue();
            (await ScalarAsync(unit, "SELECT current_user", ct)).Should().Be("learnstack_app");
            (await ScalarAsync(unit, "SELECT current_setting('app.scope', true)", ct)).Should().Be("tenant");
            (await ScalarAsync(unit, "SELECT current_setting('transaction_read_only')", ct)).Should().Be("off");
            var rows = await db.TenantSettings.AsNoTracking().Select(row => new { row.TenantId, row.OrganizationId }).ToListAsync(ct);
            rows.Should().HaveCount(3);
            rows.Should().OnlyContain(row => row.TenantId == witness.TenantId);
            rows.Should().Contain(row => row.OrganizationId == OrganizationId.From(SchemaFixture.OrgA2));
            witness.RequireCurrent();
            return admitted;
        });
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context());
        transaction = unit.Transaction;
        announced = unit.GetTenantContextAnnouncement(transaction);
        using var admission = await AdmitAsync(unit);

        (await VerifyAsync(unit, admission.Witness)).Should().Be(admitted);
        unit.Transaction.Should().BeSameAs(transaction);
        unit.GetTenantContextAnnouncement(transaction).Should().BeSameAs(announced);
        ((IOperationReadScopeView)unit).IsTenantWideRead.Should().BeFalse();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        db.CurrentTenantWideRead.Should().BeFalse();
        (await ScalarAsync(unit, "SELECT current_setting('app.scope', true)")).Should().Be(string.Empty);
        (await db.TenantSettings.AsNoTracking().CountAsync()).Should().Be(1);
        // A successful read scope has ended; the same physical owner can now flush.
        (await db.SaveChangesAsync()).Should().Be(0);
        await frame.CompleteAsync();
    }

    [Theory]
    [InlineData("save_async")]
    [InlineData("save_sync")]
    [InlineData("tracked_add")]
    [InlineData("tracked_edit")]
    [InlineData("dispose_context")]
    [InlineData("dirty_dispose")]
    [InlineData("ef_sql")]
    [InlineData("pending_audit")]
    [InlineData("outbox")]
    [InlineData("commit")]
    [InlineData("nested_begin")]
    [InlineData("tenant_setter")]
    [InlineData("provisioning_setter")]
    public async Task Attempted_flush_mutation_owned_raw_write_or_owner_transition_poison_and_rollback(string attempted)
    {
        var capture = new SaveObserver();
        TenantSetting? retained = null;
        await using var provider = Provider(async (services, _, ct) =>
        {
            var unit = services.GetRequiredService<IUnitOfWork>();
            var db = services.GetRequiredService<TenancyDbContext>();
            switch (attempted)
            {
                case "save_async": await db.SaveChangesAsync(ct); break;
#pragma warning disable xUnit1031 // Synchronous SaveChanges is an independently guarded entry point.
                case "save_sync": db.SaveChanges(); break;
#pragma warning restore xUnit1031
                case "tracked_add": db.TenantSettings.Add(NewSetting()); break;
                case "tracked_edit": retained!.SetValue("{\"changed\":true}", new SystemClock(), UserId.SystemActor); break;
                case "dispose_context": await db.DisposeAsync(); break;
                case "dirty_dispose":
                    retained!.SetValue("{\"changed\":true}", new SystemClock(), UserId.SystemActor);
                    await db.DisposeAsync();
                    break;
                case "ef_sql": await db.Database.ExecuteSqlRawAsync("UPDATE tenant_settings SET value = value WHERE false", ct); break;
                case "pending_audit": await services.GetRequiredService<IAuditStore>().WritePendingAsync(unit, ct); break;
                case "outbox": await new PostgresOutbox(unit, new ProducerExecution(unit)).EnqueueAsync(new UnreadableEvent(), ct); break;
                case "commit": await unit.CommitAsync(ct); break;
                case "nested_begin": await unit.BeginTransactionAsync(ct); break;
                case "tenant_setter": await unit.SetTenantContextAsync(Context(), ct); break;
                case "provisioning_setter": await unit.SetProvisioningTenantContextAsync(Context().TenantId, ct); break;
                default: throw new InvalidOperationException("The barrier fixture is invalid.");
            }
            return true;
        }, capture: capture);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context());
        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        retained = await context.TenantSettings.FirstAsync();
        using var admission = await AdmitAsync(unit);

        await FluentActions.Awaiting(() => VerifyAsync(unit, admission.Witness)).Should().ThrowAsync<InvalidOperationException>();
        unit.IsRollbackOnly.Should().BeTrue();
        unit.HasActiveTransaction.Should().BeFalse();
        ((IOperationReadScopeView)unit).IsTenantWideRead.Should().BeFalse();
        capture.Saves.Should().Be(0, "the common flush barrier runs before capture, even for empty saves");
        await FluentActions.Awaiting(() => unit.BeginTransactionAsync()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Dirty_enrolled_context_refuses_before_scope_enable_or_projection()
    {
        var entered = false;
        await using var provider = Provider((_, _, _) => { entered = true; return Task.FromResult(true); });
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context());
        using var admission = await AdmitAsync(unit);
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        db.TenantSettings.Add(NewSetting());

        await FluentActions.Awaiting(() => VerifyAsync(unit, admission.Witness)).Should().ThrowAsync<InvalidOperationException>();
        entered.Should().BeFalse();
        unit.IsRollbackOnly.Should().BeTrue();
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_expiry_or_invocation_cancellation_after_an_await_refuses_and_cleans_the_owner(bool cancel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        await using var provider = Provider(async (_, _, _) => { entered.SetResult(); await release.Task; return true; });
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context());
        using var admission = await AdmitAsync(unit, cancellation.Token);

        var verify = VerifyAsync(unit, admission.Witness);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel) await cancellation.CancelAsync();
        else admission.Dispose();
        ((IOperationReadScopeView)unit).IsTenantWideRead.Should().BeFalse();
        release.SetResult();
        await FluentActions.Awaiting(() => verify).Should().ThrowAsync<InvalidOperationException>();
        unit.HasActiveTransaction.Should().BeFalse();
        unit.IsRollbackOnly.Should().BeTrue();
    }

    [Fact]
    public async Task A_different_but_equal_plan_cannot_stand_in_for_the_actual_held_plan()
    {
        var entered = false;
        await using var provider = Provider((_, _, _) => { entered = true; return Task.FromResult(true); });
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context());
        var locks = new IdentityMutationLocks(unit);
        await locks.AcquireAsync(Plan());
        using var admission = new Admission(Context(), unit, locks, Plan());

        await FluentActions.Awaiting(() => VerifyAsync(unit, admission.Witness)).Should().ThrowAsync<IdentityMutationLockException>();
        entered.Should().BeFalse();
        unit.IsRollbackOnly.Should().BeTrue();
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task A_consumed_witness_cannot_enable_a_second_check_in_the_same_owner()
    {
        var calls = 0;
        await using var provider = Provider((_, _, _) => { calls++; return Task.FromResult(true); });
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context());
        using var admission = await AdmitAsync(unit);
        (await VerifyAsync(unit, admission.Witness)).Should().BeTrue();

        await FluentActions.Awaiting(() => VerifyAsync(unit, admission.Witness)).Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(1);
        unit.IsRollbackOnly.Should().BeTrue();
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pristine_and_completion_checks_cannot_share_or_replace_one_owners_consumed_purpose(bool completionFirst)
    {
        var completionCalls = 0;
        await using var provider = Provider((_, _, _) => { completionCalls++; return Task.FromResult(true); });
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context());
        using var admission = await AdmitAsync(unit);
        using var pristineSource = new PrerequisiteMinter(admission.Witness);
        var pristine = pristineSource.Mint(admission.Locks, unit);
        var owner = (IFixedSeedPrerequisiteUnitOfWork)unit;
        if (completionFirst) (await owner.VerifySeedCompletionAsync(admission.Witness)).Should().BeTrue();
        else (await owner.VerifySeedPrerequisitesAsync(pristine)).Should().BeTrue();

        await FluentActions.Awaiting(() => completionFirst
            ? owner.VerifySeedPrerequisitesAsync(pristine)
            : owner.VerifySeedCompletionAsync(admission.Witness)).Should().ThrowAsync<InvalidOperationException>();
        completionCalls.Should().Be(completionFirst ? 1 : 0);
        unit.IsRollbackOnly.Should().BeTrue();
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task A_preexisting_nested_frame_cannot_enter_completion_verification()
    {
        var entered = false;
        await using var provider = Provider((_, _, _) => { entered = true; return Task.FromResult(true); });
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context());
        using var admission = await AdmitAsync(unit);
        await using var nested = await unit.BeginTransactionAsync();

        await FluentActions.Awaiting(() => VerifyAsync(unit, admission.Witness)).Should().ThrowAsync<InvalidOperationException>();
        entered.Should().BeFalse();
        unit.IsRollbackOnly.Should().BeTrue();
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task Captured_owner_and_generation_pins_reject_foreign_binding_without_consuming_the_witness()
    {
        await using var provider = Provider((_, _, _) => Task.FromResult(true));
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context());
        using var admission = await AdmitAsync(unit);
        var transaction = unit.Transaction!;
        var generation = unit.GetTenantContextAnnouncement(transaction)!.Generation;
        await using var foreignScope = provider.CreateAsyncScope();
        var foreign = foreignScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        admission.Witness.IsExpectedOn(unit, transaction, generation).Should().BeTrue();
        admission.Witness.IsExpectedOn(unit, transaction, generation).Should().BeTrue("narrow checks do not consume authority");
        admission.Witness.IsExpectedOn(foreign, transaction, generation).Should().BeFalse();
        admission.Witness.IsExpectedOn(unit, transaction, generation + 1).Should().BeFalse();
        admission.Witness.IsBoundTo(unit, transaction, generation).Should().BeFalse();
        admission.Witness.TryBindTo(foreign, transaction, generation).Should().BeFalse();
        admission.Witness.TryBindTo(unit, transaction, generation + 1).Should().BeFalse();
        (await VerifyAsync(unit, admission.Witness)).Should().BeTrue();
        admission.Witness.TryBindTo(unit, transaction, generation).Should().BeFalse();
    }

    [Theory]
    [InlineData("completion-null")]
    [InlineData("prerequisite-null")]
    [InlineData("completion-missing-resolver")]
    public async Task Missing_typed_witness_or_resolver_still_poison_and_clean_the_owner(string mismatch)
    {
        var entered = false;
        await using var provider = Provider((_, _, _) => { entered = true; return Task.FromResult(true); },
            registerResolver: mismatch != "completion-missing-resolver");
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context());
        using var admission = await AdmitAsync(unit);
        var owner = (IFixedSeedPrerequisiteUnitOfWork)unit;

        Func<Task<bool>> attempt = mismatch switch
        {
            "completion-null" => () => owner.VerifySeedCompletionAsync(null!),
            "prerequisite-null" => () => owner.VerifySeedPrerequisitesAsync(null!),
            _ => () => owner.VerifySeedCompletionAsync(admission.Witness),
        };
        await FluentActions.Awaiting(attempt).Should().ThrowAsync<InvalidOperationException>();
        entered.Should().BeFalse();
        unit.IsRollbackOnly.Should().BeTrue();
        unit.HasActiveTransaction.Should().BeFalse();
        ((IOperationReadScopeView)unit).IsTenantWideRead.Should().BeFalse();
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("null")]
    [InlineData("throw")]
    public async Task Resolver_failures_and_new_dirty_contexts_refuse_before_either_scope_statement(string behavior)
    {
        await using var proxy = new ScopeResponseDroppingProxy(schema.Postgres.AppConnectionString, clear: false);
        var entered = false;
        await using var provider = Provider((_, _, _) => { entered = true; return Task.FromResult(true); },
            proxy.ConnectionString, resolverBehavior: behavior);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context());
        using var admission = await AdmitAsync(unit);

        await FluentActions.Awaiting(() => VerifyAsync(unit, admission.Witness)).Should().ThrowAsync<InvalidOperationException>();
        entered.Should().BeFalse();
        proxy.DroppedResponses.Should().Be(0, "resolver construction remains narrow and clean before the enable SQL");
        unit.IsRollbackOnly.Should().BeTrue();
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task A_canceled_caller_preserves_its_cancellation_and_waits_for_actual_owner_rollback()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        await using var provider = Provider(async (_, _, _) => { entered.SetResult(); await release.Task; return true; });
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context());
        using var admission = await AdmitAsync(unit, cancellation.Token);

        var verify = ((IFixedSeedPrerequisiteUnitOfWork)unit).VerifySeedCompletionAsync(admission.Witness, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        verify.IsCompleted.Should().BeFalse("cancellation alone cannot release the retained operation or owner");
        release.SetResult();
        var failure = await FluentActions.Awaiting(() => verify).Should().ThrowAsync<OperationCanceledException>();
        failure.Which.CancellationToken.Should().Be(cancellation.Token);
        unit.HasActiveTransaction.Should().BeFalse();
        unit.IsRollbackOnly.Should().BeTrue();
    }

    [Fact]
    public async Task The_active_read_hatch_preserves_cross_organization_SQL_write_guards()
    {
        await using var provider = Provider(async (services, _, ct) =>
        {
            var unit = services.GetRequiredService<IUnitOfWork>();
            foreach (var verb in ScopedWriteStatements)
            {
                await using var command = new NpgsqlCommand(verb + " WHERE organization_id = @organization",
                    (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
                command.Parameters.AddWithValue("organization", SchemaFixture.OrgA2);
                (await command.ExecuteNonQueryAsync(ct)).Should().Be(0);
            }
            // Physical ReadWrite remains an explicit boundary: only common owned
            // writers are barred. RLS still admits an otherwise-valid tenant-wide write.
            await using var own = new NpgsqlCommand("UPDATE tenant_settings SET value = value WHERE organization_id IS NULL",
                (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
            (await own.ExecuteNonQueryAsync(ct)).Should().Be(1);
            return true;
        });
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context());
        using var admission = await AdmitAsync(unit);
        (await VerifyAsync(unit, admission.Witness)).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_lost_scope_statement_acknowledgement_returns_no_evidence_and_poison_cleans_the_owner(bool clear)
    {
        await using var proxy = new ScopeResponseDroppingProxy(schema.Postgres.AppConnectionString, clear);
        var entered = false;
        await using var provider = Provider((_, _, _) => { entered = true; return Task.FromResult(true); }, proxy.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context());
        using var admission = await AdmitAsync(unit);

        await FluentActions.Awaiting(() => VerifyAsync(unit, admission.Witness)).Should().ThrowAsync<Exception>();
        proxy.DroppedResponses.Should().Be(1, "the real server completed the exact enable/clear statement before its acknowledgement was dropped");
        entered.Should().Be(clear);
        unit.IsRollbackOnly.Should().BeTrue();
        unit.HasActiveTransaction.Should().BeFalse();
        ((IOperationReadScopeView)unit).IsTenantWideRead.Should().BeFalse();
    }

    [Fact]
    public async Task Successful_restoration_does_not_leave_the_hatch_on_an_unreset_reused_connector()
    {
        var settings = new NpgsqlConnectionStringBuilder(schema.Postgres.AppConnectionString) { MaxPoolSize = 1, NoResetOnClose = true };
        await using var provider = Provider((_, _, _) => Task.FromResult(true), settings.ConnectionString);
        int backend;
        await using (var firstScope = provider.CreateAsyncScope())
        {
            var unit = firstScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var frame = await unit.BeginTransactionAsync();
            await unit.SetTenantContextAsync(Context());
            using var admission = await AdmitAsync(unit);
            backend = ((NpgsqlConnection)unit.Connection).ProcessID;
            (await VerifyAsync(unit, admission.Witness)).Should().BeTrue();
            await frame.CompleteAsync();
        }
        await using var secondScope = provider.CreateAsyncScope();
        var next = secondScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var nextFrame = await next.BeginTransactionAsync();
        ((NpgsqlConnection)next.Connection).ProcessID.Should().Be(backend);
        (await ScalarAsync(next, "SELECT NULLIF(current_setting('app.scope', true), '')")).Should().Be(DBNull.Value);
        await next.SetTenantContextAsync(Context());
        (await secondScope.ServiceProvider.GetRequiredService<TenancyDbContext>().TenantSettings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Fault_cleanup_rolls_back_real_work_before_the_unreset_connector_returns_to_the_pool()
    {
        var settings = new NpgsqlConnectionStringBuilder(schema.Postgres.AppConnectionString) { MaxPoolSize = 1, NoResetOnClose = true };
        await using var provider = Provider((_, _, _) => throw new InvalidOperationException("The completion fixture refused."), settings.ConnectionString);
        int backend;
        TenantSettingId pending;
        await using (var firstScope = provider.CreateAsyncScope())
        {
            var unit = firstScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var frame = await unit.BeginTransactionAsync();
            await unit.SetTenantContextAsync(Context());
            var db = firstScope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var setting = NewSetting();
            pending = setting.Id;
            db.TenantSettings.Add(setting);
            await db.SaveChangesAsync();
            using var admission = await AdmitAsync(unit);
            backend = ((NpgsqlConnection)unit.Connection).ProcessID;

            var failure = await FluentActions.Awaiting(() => VerifyAsync(unit, admission.Witness))
                .Should().ThrowAsync<InvalidOperationException>();
            failure.Which.Message.Should().Be("The completion fixture refused.");
        }

        // Scope disposal is awaited before reuse: cleared local bookkeeping alone
        // would prove neither rollback of the row nor a clean physical connector.
        await using var secondScope = provider.CreateAsyncScope();
        var next = secondScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var nextFrame = await next.BeginTransactionAsync();
        ((NpgsqlConnection)next.Connection).ProcessID.Should().Be(backend);
        (await ScalarAsync(next, "SELECT NULLIF(current_setting('app.scope', true), '')")).Should().Be(DBNull.Value);
        await next.SetTenantContextAsync(Context());
        var nextDb = secondScope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        (await nextDb.TenantSettings.AsNoTracking().AnyAsync(setting => setting.Id == pending)).Should().BeFalse();
        (await nextDb.TenantSettings.AsNoTracking().CountAsync()).Should().Be(1);
    }

    private ServiceProvider Provider(Func<IServiceProvider, SeedCompletionReadWitness, CancellationToken, Task<bool>> operation,
        string? connectionString = null, SaveObserver? capture = null, bool registerResolver = true, string? resolverBehavior = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => ApplicationDataSource.Build(connectionString ?? schema.Postgres.AppConnectionString));
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(Context()));
        services.AddScoped<GlobalIdentityEnrollment>();
        services.AddScoped<IGlobalIdentityEnrollment>(provider => provider.GetRequiredService<GlobalIdentityEnrollment>());
        services.AddScoped<ISeedPrerequisiteReadOperationResolver, PrerequisiteResolver>();
        if (registerResolver)
            services.AddScoped<ISeedCompletionReadOperationResolver>(provider => new Resolver(provider, operation, resolverBehavior));
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddModuleDbContext<TenancyDbContext>();
        services.AddScoped<IAuditStateCapture, AuditStateCapture>();
        services.AddSingleton<IAuditHealth, AuditHealth>();
        services.AddSingleton<IAuditCatalog>(new AuditCatalog([]));
        services.AddSingleton(provider => new Lazy<NpgsqlDataSource>(provider.GetRequiredService<NpgsqlDataSource>));
        services.AddScoped<IAuditStore, PostgresAuditStore>();
        if (capture is not null) services.AddSingleton<ISaveChangesInterceptor>(capture);
        return services.BuildServiceProvider();
    }

    private static Task<bool> VerifyAsync(IUnitOfWork unit, SeedCompletionReadWitness witness) =>
        ((IFixedSeedPrerequisiteUnitOfWork)unit).VerifySeedCompletionAsync(witness);

    private static async Task<Admission> AdmitAsync(IUnitOfWork unit, CancellationToken cancellationToken = default)
    {
        var plan = Plan();
        var locks = new IdentityMutationLocks(unit);
        await locks.AcquireAsync(plan, cancellationToken);
        return new Admission(Context(), unit, locks, plan, cancellationToken);
    }

    private static IdentityMutationLockPlan Plan() => new([], Context().TenantId, IdentityTenantLockMode.Exclusive);

    private static async Task<object?> ScalarAsync(IUnitOfWork unit, string sql, CancellationToken cancellationToken = default)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static TenantSetting NewSetting() => TenantSetting.Create(TenantSettingId.From(Guid.CreateVersion7()),
        Context().TenantId, Context().OrganizationId, "fixed-completion-proof." + Guid.CreateVersion7().ToString("N"),
        "{}", new SystemClock(), UserId.SystemActor);

    private static ScopeContext Context() => new(TenantId.From(SchemaFixture.TenantA), null);

    private sealed record ScopeContext(TenantId TenantId, OrganizationId? OrganizationId) : ITenantContext
    {
        public bool IsResolved => true;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public string? CorrelationId => null;
        public string? ModuleName => null;
    }

    private sealed record Declaration(TenantId TenantId, OrganizationId? OrganizationId);
    private sealed record PreparedTarget(TenantId TenantId);
    private sealed class Probe;

    private sealed class Admission : IDisposable
    {
        private readonly InvocationMinter _invocations = new();
        private readonly ReadMinter _source;
        internal SeedCompletionReadWitness Witness { get; }
        internal IIdentityMutationLocks Locks { get; }
        internal Admission(ITenantContext context, IUnitOfWork unit, IIdentityMutationLocks locks, IdentityMutationLockPlan plan,
            CancellationToken cancellationToken = default)
        {
            Locks = locks;
            _source = new(_invocations, new(context.TenantId, context.OrganizationId), new(context.TenantId));
            try
            {
                var invocation = _invocations.Mint(context, cancellationToken);
                Witness = _source.Mint(invocation, plan, locks, unit);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _source.Dispose();
            _invocations.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class ReadMinter(InvocationMinter invocations, Declaration declaration, PreparedTarget target) : SeedCompletionReadWitness.MintingSource
    {
        internal SeedCompletionReadWitness Mint(OperationInvocation invocation, IdentityMutationLockPlan plan,
            IIdentityMutationLocks locks, IUnitOfWork unit) => ForCompletionRead(invocation, plan, locks,
                ImmutableDictionary<Type, object>.Empty.Add(typeof(Declaration), declaration), target,
                unit, unit.Transaction!, unit.GetTenantContextAnnouncement(unit.Transaction)!.Generation);

        protected override bool IsCurrent(SeedCompletionReadWitness witness) =>
            ReferenceEquals(witness.Invocation, invocations.Current)
            && ReferenceEquals(witness.GetDeclaration<Declaration>(), declaration)
            && ReferenceEquals(witness.GetPreparedTarget<PreparedTarget>(), target)
            && witness.TenantId == declaration.TenantId && witness.OrganizationId == declaration.OrganizationId;
    }

    private sealed class PrerequisiteMinter(SeedCompletionReadWitness completion) : SeedPrerequisiteReadWitness.MintingSource
    {
        internal SeedPrerequisiteReadWitness Mint(IIdentityMutationLocks locks, IUnitOfWork unit) =>
            ForPrerequisiteRead(completion.Invocation, completion.Plan, locks, completion,
                unit, unit.Transaction!, unit.GetTenantContextAnnouncement(unit.Transaction)!.Generation);

        protected override bool IsCurrent(SeedPrerequisiteReadWitness witness) =>
            completion.IsActive && ReferenceEquals(witness.Invocation, completion.Invocation)
            && ReferenceEquals(witness.Plan, completion.Plan);
    }

    private sealed class InvocationMinter : OperationInvocation.MintingSource, IDisposable
    {
        private readonly TrustedMinter _trusted = new();
        internal OperationInvocation? Current { get; private set; }
        internal OperationInvocation Mint(ITenantContext context, CancellationToken cancellationToken) =>
            Current = ForInvocation(new Probe(), OperationPolicy.ForTrusted<Probe>("seed.completion.proof", AuthorizationSourceKind.Seed),
                context, null, _trusted.Mint(context), cancellationToken);
        protected override bool IsCurrent(OperationInvocation invocation) => ReferenceEquals(Current, invocation);

        public void Dispose()
        {
            Current = null;
            _trusted.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class TrustedMinter : TrustedOperationDescriptor.MintingSource
    {
        internal TrustedOperationDescriptor Mint(ITenantContext context) => ForOperation(AuthorizationSourceKind.Seed, typeof(Probe), context);
        protected override bool IsEvidenceCurrent(object? evidence) => evidence is null;
    }

    private sealed class Resolver(IServiceProvider services,
        Func<IServiceProvider, SeedCompletionReadWitness, CancellationToken, Task<bool>> operation,
        string? behavior) : ISeedCompletionReadOperationResolver
    {
        public ISeedCompletionReadOperation Resolve()
        {
            var db = services.GetRequiredService<TenancyDbContext>();
            if (behavior == "dirty") db.TenantSettings.Add(NewSetting());
            if (behavior == "null") return null!;
            if (behavior == "throw") throw new InvalidOperationException("The completion resolver refused.");
            return new Operation(services, operation);
        }
    }

    private sealed class Operation(IServiceProvider services,
        Func<IServiceProvider, SeedCompletionReadWitness, CancellationToken, Task<bool>> execute) : ISeedCompletionReadOperation
    {
        public Task<bool> ExecuteAsync(SeedCompletionReadWitness witness, CancellationToken cancellationToken = default) =>
            execute(services, witness, cancellationToken);
    }

    private sealed class PrerequisiteResolver : ISeedPrerequisiteReadOperationResolver
    {
        public ISeedPrerequisiteReadOperation Resolve() => new PrerequisiteOperation();
    }

    private sealed class PrerequisiteOperation : ISeedPrerequisiteReadOperation
    {
        public Task<bool> ExecuteAsync(SeedPrerequisiteReadWitness witness, CancellationToken cancellationToken = default)
        {
            witness.RequireCurrent();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    private sealed class SaveObserver : SaveChangesInterceptor
    {
        internal int Saves { get; private set; }
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        { Saves++; return result; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Saves++; return ValueTask.FromResult(result); }
    }

    private sealed class UnreadableEvent : IIntegrationEvent
    {
        public Guid EventId => throw new InvalidOperationException("The event must not be inspected.");
        public Guid TenantId => throw new InvalidOperationException("The event must not be inspected.");
        public DateTimeOffset OccurredAt => throw new InvalidOperationException("The event must not be inspected.");
        public string Topic => throw new InvalidOperationException("The event must not be inspected.");
        public string PartitionKey => throw new InvalidOperationException("The event must not be inspected.");
    }

    /// <summary>Drops only the completed exact scope statement's response from a real app-role session.</summary>
    private sealed class ScopeResponseDroppingProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new(TimeSpan.FromSeconds(30));
        private readonly List<Task> _sessions = [];
        private readonly string _targetHost;
        private readonly int _targetPort;
        private readonly string _targetSql;
        private readonly Task _accepting;
        private int _droppedResponses;

        internal ScopeResponseDroppingProxy(string connectionString, bool clear)
        {
            var target = new NpgsqlConnectionStringBuilder(connectionString);
            _targetHost = target.Host!;
            _targetPort = target.Port;
            _targetSql = clear ? "SELECT set_config('app.scope', '', true)" : "SELECT set_config('app.scope', 'tenant', true)";
            _listener.Start();
            ConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Host = IPAddress.Loopback.ToString(),
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
                SslMode = SslMode.Disable,
                GssEncryptionMode = GssEncryptionMode.Disable,
                Pooling = false,
                Timeout = 5,
            }.ConnectionString;
            _accepting = AcceptAsync();
        }

        internal string ConnectionString { get; }
        internal int DroppedResponses => Volatile.Read(ref _droppedResponses);

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            try
            {
                await _accepting.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAll(_sessions).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { _stopping.Dispose(); }
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                    _sessions.Add(ForwardAsync(await _listener.AcceptTcpClientAsync(_stopping.Token)));
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (SocketException) when (_stopping.IsCancellationRequested) { }
        }

        private async Task ForwardAsync(TcpClient client)
        {
            using (client)
            using (var server = new TcpClient())
            using (var stopping = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
            {
                try
                {
                    await server.ConnectAsync(_targetHost, _targetPort, stopping.Token);
                    await using var incoming = client.GetStream();
                    await using var outgoing = server.GetStream();
                    var statement = new ObservedStatement(_targetSql);
                    var requests = RequestsAsync(incoming, outgoing, statement, stopping.Token);
                    var responses = ResponsesAsync(outgoing, incoming, statement, stopping.Token);
                    try
                    {
                        var completed = await Task.WhenAny(requests, responses);
                        await completed;
                    }
                    finally
                    {
                        await stopping.CancelAsync();
                        client.Close();
                        server.Close();
                        await ObserveStoppedAsync(requests, stopping.Token);
                        await ObserveStoppedAsync(responses, stopping.Token);
                    }
                }
                catch (OperationCanceledException failure) when (IsOwnedCancellation(failure, stopping.Token))
                {
                    // Disposal can cancel the first completed pump, whose original
                    // await otherwise rethrows after both cleanup observations.
                    // Only this session's cancellation is an expected shutdown.
                }
            }
        }

        private static async Task RequestsAsync(NetworkStream client, NetworkStream server, ObservedStatement statement, CancellationToken ct)
        {
            var length = new byte[4];
            await client.ReadExactlyAsync(length, ct);
            var startupLength = BinaryPrimitives.ReadInt32BigEndian(length);
            if (startupLength is < 8 or > 1_048_576) throw new InvalidDataException("The fixture startup frame is invalid.");
            var startup = new byte[startupLength - 4];
            await client.ReadExactlyAsync(startup, ct);
            await server.WriteAsync(length, ct);
            await server.WriteAsync(startup, ct);
            var header = new byte[5];
            while (await client.ReadAsync(header.AsMemory(0, 1), ct) != 0)
            {
                await client.ReadExactlyAsync(header.AsMemory(1, 4), ct);
                var size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                if (size is < 4 or > 1_048_576) throw new InvalidDataException("The fixture request frame is invalid.");
                var body = new byte[size - 4];
                await client.ReadExactlyAsync(body, ct);
                statement.Observe(header[0], body);
                await server.WriteAsync(header, ct);
                await server.WriteAsync(body, ct);
            }
        }

        private async Task ResponsesAsync(NetworkStream server, NetworkStream client, ObservedStatement statement, CancellationToken ct)
        {
            var header = new byte[5];
            while (await server.ReadAsync(header.AsMemory(0, 1), ct) != 0)
            {
                await server.ReadExactlyAsync(header.AsMemory(1, 4), ct);
                var size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                if (size is < 4 or > 1_048_576) throw new InvalidDataException("The fixture response frame is invalid.");
                var body = new byte[size - 4];
                await server.ReadExactlyAsync(body, ct);
                if (header[0] == (byte)'C' && body.AsSpan().SequenceEqual("SELECT 1\0"u8) && statement.Matches)
                {
                    Interlocked.Increment(ref _droppedResponses);
                    return;
                }
                await client.WriteAsync(header, ct);
                await client.WriteAsync(body, ct);
            }
        }

        private sealed class ObservedStatement(string target)
        {
            private int _matches;
            internal bool Matches => Volatile.Read(ref _matches) != 0;
            internal void Observe(byte kind, ReadOnlySpan<byte> body)
            {
                if (kind == (byte)'P')
                {
                    var nameEnd = body.IndexOf((byte)0);
                    if (nameEnd < 0) return;
                    body = body[(nameEnd + 1)..];
                }
                else if (kind != (byte)'Q') return;
                var end = body.IndexOf((byte)0);
                Interlocked.Exchange(ref _matches, end >= 0 && Encoding.UTF8.GetString(body[..end]) == target ? 1 : 0);
            }
        }

        private static async Task ObserveStoppedAsync(Task task, CancellationToken stopping)
        {
            try { await task; }
            catch (OperationCanceledException failure) when (IsOwnedCancellation(failure, stopping)) { }
            catch (IOException) when (stopping.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { }
        }

        private static bool IsOwnedCancellation(OperationCanceledException failure, CancellationToken stopping) =>
            stopping.IsCancellationRequested && failure.CancellationToken == stopping;
    }
}
