using System.Data.Common;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class GlobalIdentityBootstrapTests
{
    [Fact]
    public async Task Precontext_binding_lookup_does_not_pin_common_coordinators_before_tenant_resolution()
    {
        await using var dataSource = NpgsqlDataSource.Create(_database.AppConnectionString);
        var seed = new SeedRunner(
            context => SeedComposition.Build(dataSource, context, NullLoggerFactory.Instance),
            NullLogger<SeedRunner>.Instance);
        (await seed.RunAsync(CancellationToken.None, [SeedData.English with { Curriculum = null }]))
            .Should().Be(0);
        await using (var creation = BuildProvider(_database.AppConnectionString, Registration,
            new FixtureAdmission(Subject, Actor)))
        {
            await QualifyAsync(creation);
            (await AdoptAsync(creation, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.Created);
        }

        var accessor = new StaticTenantContextAccessor(null);
        await using var provider = BuildProvider(_database.AppConnectionString, Registration,
            new FixtureAdmission(Subject, Actor), services =>
            {
                services.Replace(ServiceDescriptor.Singleton<ITenantContextAccessor>(accessor));
                services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(
                    typeof(LearnStack.Modules.Tenancy.Application.AssemblyMarker).Assembly));
            });
        await QualifyAsync(provider);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var reader = services.GetRequiredService<IIdentityBindingReader>();
        (await reader.ResolveExistingAsync(Subject)).Outcome.Should().Be(IdentityBindingOutcome.Existing);

        accessor.Current = new ResolvedBindingLifecycleContext(SeedData.English.TenantId, Actor);
        var organization = OrganizationId.From(Guid.CreateVersion7());
        var result = await services.GetRequiredService<ISender>().Send(
            new CreateOrganizationCommand(organization, "binding-lifecycle-branch", "Binding lifecycle branch"));
        result.IsSuccess.Should().BeTrue("the real ordinary audit/transaction pipeline sees the newly resolved context");
        services.GetRequiredService<IUnitOfWork>().HasActiveTransaction.Should().BeFalse();
        services.GetRequiredService<IUnitOfWork>().IsRollbackOnly.Should().BeFalse();
        services.GetRequiredService<IAuditStateCapture>().Intents.Should().BeEmpty();

        (await reader.ResolveExistingAsync(Subject)).Outcome.Should().Be(IdentityBindingOutcome.AdmissionDenied);
        (await services.GetRequiredService<ISeedIdentityBindingAdopter>().AdoptAsync(Subject, Actor))
            .Outcome.Should().Be(IdentityBindingOutcome.AdmissionDenied);
        (await CountsAsync()).Should().Be((1L, 1L));

        await using var connection = await OpenConnectionAsync(_database.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, SeedData.English.TenantId.Value);
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        await using var read = new NpgsqlCommand("""
            SELECT tenant_id, actor_user_id, outcome, after_state::text
            FROM public.audit_log
            WHERE operation = 'tenancy.organization.create' AND entity_id = @target
            """, connection, transaction);
        read.Parameters.AddWithValue("target", organization.Value.ToString("D"));
        await using var rows = await read.ExecuteReaderAsync();
        (await rows.ReadAsync()).Should().BeTrue("the ordinary business write has its durable MUST audit");
        rows.GetGuid(0).Should().Be(SeedData.English.TenantId.Value);
        rows.GetGuid(1).Should().Be(Actor.Value);
        rows.GetString(2).Should().Be("success");
        rows.GetString(3).Should().Contain("Binding lifecycle branch");
        (await rows.ReadAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Lookup_failure_or_cancellation_survives_awaited_cleanup_failure(bool adoption, bool cancelled)
    {
        const string PrimaryCanary = "private-lookup-failure-canary";
        using var cancellation = new CancellationTokenSource();
        var control = new BindingCleanupFault();
        var logger = new BindingCleanupLogger();
        Exception primary = cancelled
            ? new OperationCanceledException(PrimaryCanary, cancellation.Token)
            : new NpgsqlException(PrimaryCanary);
        await using var provider = CleanupFaultProvider(control, logger, () =>
        {
            if (cancelled) cancellation.Cancel();
            throw primary;
        });
        await QualifyAsync(provider);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var operation = adoption
            ? services.GetRequiredService<ISeedIdentityBindingAdopter>().AdoptAsync(Subject, Actor, cancellation.Token)
            : services.GetRequiredService<IIdentityBindingReader>().ResolveExistingAsync(Subject, cancellationToken: cancellation.Token);
        await AssertCleanupIsJoinedAsync(operation, control, services);

        if (cancelled)
        {
            var observed = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            observed.Should().BeSameAs(primary);
            observed.CancellationToken.Should().Be(cancellation.Token);
        }
        else
        {
            var result = await operation;
            result.Outcome.Should().Be(IdentityBindingOutcome.StorageUnavailable);
            result.Actor.Should().BeNull();
        }
        AssertBoundedCleanupDiagnostic(logger, PrimaryCanary);
        services.GetRequiredService<IAuditStateCapture>().Intents.Should().BeEmpty();
        (await CountsAsync()).Should().Be((0L, 0L));
    }

    [Fact]
    public async Task Otherwise_normal_lookup_with_failed_cleanup_returns_a_bounded_storage_refusal()
    {
        var control = new BindingCleanupFault();
        var logger = new BindingCleanupLogger();
        await using var provider = CleanupFaultProvider(control, logger, () => null);
        await QualifyAsync(provider);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var operation = services.GetRequiredService<IIdentityBindingReader>().ResolveExistingAsync(Subject);
        await AssertCleanupIsJoinedAsync(operation, control, services);
        var result = await operation;
        result.Outcome.Should().Be(IdentityBindingOutcome.StorageUnavailable);
        result.Actor.Should().BeNull();
        AssertBoundedCleanupDiagnostic(logger, "unused-primary-canary");
    }

    private ServiceProvider CleanupFaultProvider(BindingCleanupFault control, BindingCleanupLogger logger,
        Func<User?> lookup) => BuildProvider(_database.AppConnectionString, Registration,
        new FixtureAdmission(Subject, Actor), services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IGlobalIdentityUnitOfWork>(provider =>
                new BindingCleanupUnit((IGlobalIdentityUnitOfWork)provider.GetRequiredService<IUnitOfWork>(), control)));
            services.Replace(ServiceDescriptor.Scoped<IUserBindingStore>(_ => new FailingBindingLookup(lookup)));
            services.AddSingleton<ILogger<GlobalIdentityBindingService>>(logger);
        });

    private static async Task AssertCleanupIsJoinedAsync(Task<IdentityBindingResult> operation,
        BindingCleanupFault control, IServiceProvider services)
    {
        var first = await Task.WhenAny(control.Entered.Task, operation).WaitAsync(TimeSpan.FromSeconds(10));
        if (first == operation)
        {
            await operation;
            throw new InvalidOperationException("The binding operation returned without awaiting its owner cleanup.");
        }
        try
        {
            operation.IsCompleted.Should().BeFalse("independent owner cleanup remains part of the binding operation");
            services.GetRequiredService<IUnitOfWork>().HasActiveTransaction.Should().BeFalse(
                "the decorated frame completed its real PostgreSQL cleanup before the injected cleanup fault");
        }
        finally { control.Release.TrySetResult(); }
    }

    private static void AssertBoundedCleanupDiagnostic(BindingCleanupLogger logger, string primaryCanary)
    {
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeNull();
        entry.Message.Should().Be("Global identity owner cleanup failed; its scope retains connection cleanup responsibility.")
            .And.NotContain(primaryCanary).And.NotContain(BindingCleanupFault.Canary);
    }

    private sealed class ResolvedBindingLifecycleContext(TenantId tenant, UserId actor) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => tenant;
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => actor;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public string? CorrelationId => null;
        public string? ModuleName => null;
    }

    private sealed class FailingBindingLookup(Func<User?> lookup) : IUserBindingStore
    {
        public Task<User?> FindExactAsync(GlobalIdentitySubject subject, CancellationToken cancellationToken = default) =>
            Task.FromResult(lookup());
        public Task AddAsync(User user, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Lookup failure must precede handler construction and mutation.");
    }

    private sealed class BindingCleanupFault
    {
        public const string Canary = "private-owner-cleanup-failure-canary";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BindingCleanupFrame(IUnitOfWorkScope inner, BindingCleanupFault control) : IUnitOfWorkScope
    {
        public bool IsOwner => inner.IsOwner;
        public Task CompleteAsync(CancellationToken cancellationToken = default) => inner.CompleteAsync(cancellationToken);
        public Task FailAsync(CancellationToken cancellationToken = default) => inner.FailAsync(cancellationToken);
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            control.Entered.TrySetResult();
            await control.Release.Task;
            throw new IOException(BindingCleanupFault.Canary);
        }
    }

    private sealed class BindingCleanupUnit(IGlobalIdentityUnitOfWork inner, BindingCleanupFault control)
        : IGlobalIdentityUnitOfWork
    {
        public DbConnection Connection => inner.Connection;
        public DbTransaction? Transaction => inner.Transaction;
        public bool HasActiveTransaction => inner.HasActiveTransaction;
        public TransactionMode? Mode => inner.Mode;
        public bool IsRollbackOnly => inner.IsRollbackOnly;
        public bool IsGlobalIdentityTransaction => inner.IsGlobalIdentityTransaction;
        public Task<IUnitOfWorkScope> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);
        public Task<IUnitOfWorkScope> BeginTransactionAsync(TransactionMode mode, CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(mode, cancellationToken);
        public async Task<IUnitOfWorkScope> BeginGlobalIdentityTransactionAsync(GlobalIdentityOwnerDescriptor descriptor,
            TransactionMode mode, CancellationToken cancellationToken = default) =>
            new BindingCleanupFrame(await inner.BeginGlobalIdentityTransactionAsync(descriptor, mode, cancellationToken), control);
        public Task AcquireGlobalIdentitySubjectLockAsync(GlobalIdentityOwnerDescriptor descriptor,
            CancellationToken cancellationToken = default) => inner.AcquireGlobalIdentitySubjectLockAsync(descriptor, cancellationToken);
        public GlobalIdentityContextAnnouncement? GetGlobalIdentityAnnouncement(DbTransaction? transaction) =>
            inner.GetGlobalIdentityAnnouncement(transaction);
        public void EnrollGlobalIdentityContext(object context, GlobalIdentityOwnerDescriptor descriptor) =>
            inner.EnrollGlobalIdentityContext(context, descriptor);
        public bool IsGlobalIdentityContextEnrolled(object context, DbTransaction? transaction) =>
            inner.IsGlobalIdentityContextEnrolled(context, transaction);
        public Task ValidateGlobalIdentityConstraintsAsync(GlobalIdentityOwnerDescriptor descriptor,
            CancellationToken cancellationToken = default) => inner.ValidateGlobalIdentityConstraintsAsync(descriptor, cancellationToken);
        public Task SetTenantContextAsync(ITenantContext context, CancellationToken cancellationToken = default) =>
            inner.SetTenantContextAsync(context, cancellationToken);
        public bool IsTenantContextIssuedOn(DbTransaction? transaction) => inner.IsTenantContextIssuedOn(transaction);
        public TenantContextAnnouncement? GetTenantContextAnnouncement(DbTransaction? transaction) =>
            inner.GetTenantContextAnnouncement(transaction);
        public Task SetProvisioningTenantContextAsync(TenantId tenantId, CancellationToken cancellationToken = default) =>
            inner.SetProvisioningTenantContextAsync(tenantId, cancellationToken);
        public Task CommitAsync(CancellationToken cancellationToken = default) => inner.CommitAsync(cancellationToken);
        public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
        public void MarkRollbackOnly() => inner.MarkRollbackOnly();
        // The original scoped IUnitOfWork registration owns the real connection.
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BindingCleanupLogger : ILogger<GlobalIdentityBindingService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
