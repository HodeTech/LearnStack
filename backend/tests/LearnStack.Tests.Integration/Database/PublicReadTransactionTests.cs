using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>ADR-0052's physical write barrier, with independent writable controls.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class PublicReadTransactionTests(SchemaFixture schema)
{
    private static TenantContext Context => TenantContextFactory.Create(new TenantResolutionAttempt
    {
        HostTenantId = TenantId.From(SchemaFixture.TenantA),
    }).Value!;

    [PublicSurface]
    public sealed record PublicProbe : IRequest<Result<string>>;

    public sealed record WritableProbe : IRequest<Result<string>>;

    /// <summary>ADR-0040 Amendment 8 and ADR-0052; registered behavioural proof.</summary>
    [Fact]
    public async Task PublicSurface_Transactions_Refuse_Writes()
    {
        foreach (var useEf in new[] { false, true })
        {
            var id = TenantDomainId.From(Guid.CreateVersion7());
            await using var provider = BuildProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var capture = new AuditStateCapture();
                var behavior = new TransactionBehavior<PublicProbe, Result<string>>(
                    unit, Context, Store(scope.ServiceProvider, capture), capture,
                    NullLogger<TransactionBehavior<PublicProbe, Result<string>>>.Instance);
                var reached = false;
                var attempt = async () => await behavior.Handle(new PublicProbe(), async () =>
                {
                    reached = true;
                    unit.Mode.Should().Be(TransactionMode.ReadOnly);
                    (await ScalarAsync(unit, "SHOW transaction_read_only")).Should().Be("on");
                    await AssertRoleAsync(unit);
                    await WriteAsync(scope.ServiceProvider, unit, id, useEf);
                    return Result.Ok("unexpected write");
                }, default);

                var failure = await attempt.Should().ThrowAsync<Exception>();
                var databaseFailure = failure.Which as PostgresException
                    ?? failure.Which.InnerException as PostgresException;
                databaseFailure.Should().NotBeNull();
                databaseFailure!.SqlState.Should().Be(PostgresErrorCodes.ReadOnlySqlTransaction);
                reached.Should().BeTrue("refusal must occur at the actual write, not before dispatch");
                unit.HasActiveTransaction.Should().BeFalse();
                unit.Mode.Should().BeNull();
                unit.IsRollbackOnly.Should().BeTrue();
            }

            (await CountAsync(id)).Should().Be(0L);

            // The identical mutation succeeds on the ordinary role/frame. Roll it
            // back after observing the row so shared fixture counts stay unchanged.
            await using var writableScope = provider.CreateAsyncScope();
            var writable = writableScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var control = await writable.BeginTransactionAsync();
            await writable.SetTenantContextAsync(Context);
            await WriteAsync(writableScope.ServiceProvider, writable, id, useEf);
            (await ScalarAsync(writable, $"SELECT count(*) FROM tenant_domains WHERE id = '{id.Value:D}'"))
                .Should().Be(1L);
            await control.FailAsync();
            (await CountAsync(id)).Should().Be(0L);
        }
    }

    [Fact]
    public async Task Read_only_mode_is_active_at_begin_return_and_resets_after_success()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using (var first = await unit.BeginTransactionAsync(TransactionMode.ReadOnly))
        {
            unit.Mode.Should().Be(TransactionMode.ReadOnly);
            (await ScalarAsync(unit, "SHOW transaction_read_only")).Should().Be("on");
            await unit.SetTenantContextAsync(Context);
            await AssertRoleAsync(unit);
            await using (var joined = await unit.BeginTransactionAsync(TransactionMode.ReadOnly))
            {
                joined.IsOwner.Should().BeFalse();
                await joined.CompleteAsync();
            }
            await first.CompleteAsync();
        }
        unit.Mode.Should().BeNull();
        unit.IsRollbackOnly.Should().BeFalse();
        await using var next = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context);
        (await ScalarAsync(unit, "SHOW transaction_read_only")).Should().Be("off");
        unit.Mode.Should().Be(TransactionMode.ReadWrite);
        var id = TenantDomainId.From(Guid.CreateVersion7());
        await WriteAsync(scope.ServiceProvider, unit, id, useEf: false);
        await next.FailAsync();
        (await CountAsync(id)).Should().Be(0L);
    }

    [Theory]
    [InlineData(TransactionMode.ReadOnly, TransactionMode.ReadWrite)]
    [InlineData(TransactionMode.ReadWrite, TransactionMode.ReadOnly)]
    public async Task Mixed_mode_refusal_poisons_an_outer_owner_even_when_absorbed(
        TransactionMode outerMode, TransactionMode innerMode)
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var owner = await unit.BeginTransactionAsync(outerMode);
        await unit.SetTenantContextAsync(Context);
        var transaction = unit.Transaction;
        var handlerInvoked = false;
        RequestHandlerDelegate<Result<string>> next = () =>
        {
            handlerInvoked = true;
            return Task.FromResult(Result.Ok("must not dispatch"));
        };
        var capture = new AuditStateCapture();
        var store = Store(scope.ServiceProvider, capture);
        Func<Task> join = innerMode == TransactionMode.ReadOnly
            ? async () => await new TransactionBehavior<PublicProbe, Result<string>>(
                unit, Context, store, capture,
                NullLogger<TransactionBehavior<PublicProbe, Result<string>>>.Instance)
                .Handle(new PublicProbe(), next, default)
            : async () => await new TransactionBehavior<WritableProbe, Result<string>>(
                unit, Context, store, capture,
                NullLogger<TransactionBehavior<WritableProbe, Result<string>>>.Instance)
                .Handle(new WritableProbe(), next, default);
        await join.Should().ThrowAsync<InvalidOperationException>().WithMessage("*mixed-mode*");
        handlerInvoked.Should().BeFalse();
        unit.Transaction.Should().BeSameAs(transaction);
        unit.Mode.Should().Be(outerMode);
        unit.IsRollbackOnly.Should().BeTrue();
        var complete = async () => await owner.CompleteAsync();
        await complete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rollback-only*");
        unit.Mode.Should().BeNull();
        unit.HasActiveTransaction.Should().BeFalse();
        var reuse = async () => await unit.BeginTransactionAsync();
        await reuse.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rollback-only*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_read_only_setup_failure_cleans_up_before_begin_throws(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        IUnitOfWork? unit = null;
        var observed = false;
        var injected = new InvalidOperationException("injected mode-setup failure");
        using var logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace)
            .AddProvider(new SetupFailureLogger(() =>
            {
                observed = true;
                unit!.HasActiveTransaction.Should().BeTrue("inject after driver transaction allocation, before a frame exists");
                if (cancel)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                throw injected;
            })));
        await using var provider = BuildProvider(logs);
        await using var scope = provider.CreateAsyncScope();
        unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var begin = async () => await unit.BeginTransactionAsync(TransactionMode.ReadOnly, cancellation.Token);
        if (cancel) await begin.Should().ThrowAsync<OperationCanceledException>();
        else (await begin.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(injected);
        observed.Should().BeTrue("the test must reach the mode-control statement");
        unit.Transaction.Should().BeNull();
        unit.Mode.Should().BeNull();
        unit.IsRollbackOnly.Should().BeTrue();
        await AssertFreshTransactionIsWritableAsync(unit);
        var reuse = async () => await unit.BeginTransactionAsync(TransactionMode.ReadWrite);
        await reuse.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rollback-only*");
    }

    [Fact]
    public async Task Cancelled_handler_rolls_back_and_pooled_next_scope_is_writable()
    {
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var capture = new AuditStateCapture();
            var behavior = new TransactionBehavior<PublicProbe, Result<string>>(
                unit, Context, Store(scope.ServiceProvider, capture), capture,
                NullLogger<TransactionBehavior<PublicProbe, Result<string>>>.Instance);
            var attempt = async () => await behavior.Handle(new PublicProbe(), () =>
            {
                unit.Mode.Should().Be(TransactionMode.ReadOnly);
                throw new OperationCanceledException("cancelled public handler");
            }, default);
            await attempt.Should().ThrowAsync<OperationCanceledException>();
            unit.HasActiveTransaction.Should().BeFalse();
            unit.Mode.Should().BeNull();
            unit.IsRollbackOnly.Should().BeTrue();
        }
        await using var fresh = provider.CreateAsyncScope();
        var writable = fresh.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await writable.BeginTransactionAsync();
        await writable.SetTenantContextAsync(Context);
        (await ScalarAsync(writable, "SHOW transaction_read_only")).Should().Be("off");
        await WriteAsync(fresh.ServiceProvider, writable, TenantDomainId.From(Guid.CreateVersion7()), useEf: true);
        await frame.FailAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Setup_failure_after_server_mode_activation_cleans_up_before_begin_returns(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        IUnitOfWork? unit = null;
        var observed = false;
        var injected = new InvalidOperationException("injected after server mode activation");
        using var logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace)
            .AddProvider(new SetupFailureLogger(() =>
            {
                // Pinned Npgsql 10.0.0 logs completion before EndUserAction. Its
                // public ReaderClosed event runs after EndUserAction, but before
                // ExecuteNonQueryAsync (and therefore Begin) returns. Use the
                // cached reader only here; production gains no injection seam.
                var reader = SetupReader((NpgsqlConnection)unit!.Connection);
                EventHandler? afterClose = null;
                afterClose = (_, _) =>
                {
                    // Npgsql reuses this reader; detach before the observer query.
                    reader.ReaderClosed -= afterClose;
                    using var show = new NpgsqlCommand("SHOW transaction_read_only",
                        (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
                    show.ExecuteScalar().Should().Be("on", "PostgreSQL has executed the mode statement");
                    observed = true;
                    if (cancel)
                    {
                        cancellation.Cancel();
                        cancellation.Token.ThrowIfCancellationRequested();
                    }
                    throw injected;
                };
                reader.ReaderClosed += afterClose;
            }, afterExecution: true)));
        await using var provider = BuildProvider(logs);
        await using var scope = provider.CreateAsyncScope();
        unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var begin = async () => await unit.BeginTransactionAsync(TransactionMode.ReadOnly, cancellation.Token);
        if (cancel) await begin.Should().ThrowAsync<OperationCanceledException>();
        else (await begin.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(injected);
        observed.Should().BeTrue();
        unit.Transaction.Should().BeNull();
        unit.Mode.Should().BeNull();
        unit.IsRollbackOnly.Should().BeTrue();
        await AssertFreshTransactionIsWritableAsync(unit);
        var reuse = async () => await unit.BeginTransactionAsync();
        await reuse.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rollback-only*");
    }

    private static NpgsqlDataReader SetupReader(NpgsqlConnection connection)
    {
        const BindingFlags InternalInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var connector = typeof(NpgsqlConnection).GetProperty("Connector", InternalInstance)?.GetValue(connection);
        connector.Should().NotBeNull("the pinned driver must expose its active connector to this test");
        var reader = connector!.GetType().GetProperty("DataReader", InternalInstance)?.GetValue(connector);
        reader.Should().BeOfType<NpgsqlDataReader>("driver changes must fail the injection, not silently skip it");
        return (NpgsqlDataReader)reader!;
    }

    private ServiceProvider BuildProvider(ILoggerFactory? injectedLogs = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton(_ => ApplicationDataSource.Build(schema.Postgres.AppConnectionString, injectedLogs));
        services.AddSingleton<ITenantContext>(Context);
        services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(Context));
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddModuleDbContext<TenancyDbContext>();
        return services.BuildServiceProvider();
    }

    private static PostgresAuditStore Store(IServiceProvider services, AuditStateCapture capture) => new(
        capture, new Lazy<NpgsqlDataSource>(services.GetRequiredService<NpgsqlDataSource>),
        NullLogger<PostgresAuditStore>.Instance,
        services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(), new AuditHealth());

    private static async Task AssertRoleAsync(IUnitOfWork unit)
    {
        (await ScalarAsync(unit, "SELECT current_user")).Should().Be("learnstack_app");
        (await ScalarAsync(unit, "SELECT rolbypassrls OR rolsuper FROM pg_roles WHERE rolname = current_user"))
            .Should().Be(false);
    }

    private static async Task WriteAsync(IServiceProvider services, IUnitOfWork unit, TenantDomainId id, bool useEf)
    {
        if (useEf)
        {
            var db = services.GetRequiredService<TenancyDbContext>();
            db.Add(TenantDomain.CreateSubdomain(id, Context.TenantId, $"readonly-{id.Value:N}.example.com",
                new SystemClock(), UserId.SystemActor));
            await db.SaveChangesAsync();
            return;
        }
        await using var command = new NpgsqlCommand(
            "INSERT INTO tenant_domains (id, tenant_id, host, kind, status, created_at, created_by, row_version) "
            + "VALUES (@id, @tenant, @host, 'Subdomain', 'Verified', now(), @actor, 0)",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("tenant", Context.TenantId.Value);
        command.Parameters.AddWithValue("host", $"readonly-{id.Value:N}.example.com");
        command.Parameters.AddWithValue("actor", UserId.SystemActor.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(TenantDomainId id)
    {
        await using var dataSource = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(Context);
        var count = (long)(await ScalarAsync(unit, $"SELECT count(*) FROM tenant_domains WHERE id = '{id.Value:D}'"))!;
        await frame.CompleteAsync();
        return count;
    }

    private static async Task<object?> ScalarAsync(IUnitOfWork unit, string sql)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
        return await command.ExecuteScalarAsync();
    }

    private static async Task AssertFreshTransactionIsWritableAsync(IUnitOfWork unit)
    {
        // The poisoned unit cannot issue another frame. Probe the same physical
        // connection in a real transaction: an implicit SHOW would only observe
        // the session default, not prove cleanup permits a writable transaction.
        await using var transaction = await unit.Connection.BeginTransactionAsync();
        await using var command = unit.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SHOW transaction_read_only";
        (await command.ExecuteScalarAsync()).Should().Be("off",
            "cleanup must permit a fresh writable transaction on the same connection");
        await transaction.RollbackAsync();
    }

    /// <summary>Fault injection at the actual driver control statement, without a production seam.</summary>
    private sealed class SetupFailureLogger(Action onSetup, bool afterExecution = false) : ILoggerProvider, ILogger
    {
        private bool _fired;
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var setupReached = afterExecution
                ? message.StartsWith("Command execution completed", StringComparison.Ordinal)
                    && message.Contains("SET TRANSACTION READ ONLY", StringComparison.Ordinal)
                : message.StartsWith("Executing command: SET TRANSACTION READ ONLY", StringComparison.Ordinal);
            if (!_fired && setupReached)
            {
                _fired = true;
                onSetup();
            }
        }
    }
}
