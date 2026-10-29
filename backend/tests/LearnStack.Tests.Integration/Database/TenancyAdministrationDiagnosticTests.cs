using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(IdentityDiagnosticSinkGroup.Name)]
public sealed class TenancyAdministrationDiagnosticTests(TenancyAdministrationFixture fixture) : IClassFixture<TenancyAdministrationFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_provider_payload_has_dirty_and_protected_diagnostic_sink_twins(bool protectedReader)
    {
        var canaries = new[] { "neutral-63c19", "nested-8da21", "secret-379bc" };
        var payload = JsonSerializer.Serialize(new { value = canaries[0], items = new[] { new { value = canaries[1], token = canaries[2] } } });
        var create = await fixture.SendAsync(new CreateManagedOrganizationCommand("diagnostic-" + Guid.NewGuid().ToString("N"), payload), "tenancy-diagnostic-create");
        Assert.True(create.IsSuccess);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        var before = await ScalarAsync<string>(owner, null, "SELECT pg_get_expr(polqual,polrelid) FROM pg_policy WHERE polrelid='tenants'::regclass ORDER BY polname LIMIT 1");
        using var sinks = new IdentityDiagnosticSinks();
        using var callers = new ActivitySource("learnstack.identity.storage-qualification");
        using var caller = callers.StartActivity("tenancy-administration-read");
        Assert.NotNull(caller);
        await using var contextHarness = await fixture.CreateAsync("tenancy-diagnostic-context");
        var context = contextHarness.Services.GetRequiredService<ITenantContext>();
        await using var source = ApplicationDataSource.Build(fixture.AppConnectionString, sinks.Loggers);
        await using var unit = new NpgsqlUnitOfWork(source, sinks.Loggers.CreateLogger<NpgsqlUnitOfWork>());
        await using var frame = await unit.BeginTransactionAsync(TransactionMode.ReadOnly);
        await unit.SetTenantContextAsync(context);
        var witness = new FailureWitness();
        await using var db = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(unit.Connection)
            .UseLoggerFactory(sinks.Loggers).AddInterceptors(witness).Options, new StaticTenantContextAccessor(context));
        await db.Database.UseTransactionAsync(unit.Transaction);
        await ExecuteAsync(owner, null, $$"""
            CREATE FUNCTION tenancy_administration_sink_fault() RETURNS boolean
            LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
            DECLARE payload text;
            BEGIN
                SELECT display_name INTO STRICT payload FROM public.organizations WHERE id='{{create.Value!.OrganizationId.Value:D}}'::uuid;
                RAISE EXCEPTION USING ERRCODE='P0399',MESSAGE=payload;
            END $$;
            REVOKE ALL ON FUNCTION tenancy_administration_sink_fault() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION tenancy_administration_sink_fault() TO learnstack_app;
            CREATE POLICY tenancy_administration_sink_fault ON tenants AS RESTRICTIVE FOR SELECT TO learnstack_app
            USING(tenancy_administration_sink_fault());
            """);
        Exception failure;
        try
        {
            if (protectedReader)
            {
                var reader = new TenancyAdministrationReader(db, unit, context);
                var translated = await Assert.ThrowsAsync<TenancyAdministrationStorageException>(() => reader.ReadTenantAsync());
                Assert.Equal(TenancyAdministrationStorageFailure.Provider, translated.Failure);
                Assert.Null(translated.InnerException);
                failure = translated;
            }
            else
            {
                failure = await Assert.ThrowsAsync<PostgresException>(() => db.Tenants.AsNoTracking().ToArrayAsync());
            }
            Assert.Equal(payload, Assert.IsType<PostgresException>(witness.Failure).MessageText);
            await frame.FailAsync();
        }
        finally
        {
            await ExecuteAsync(owner, null, "DROP POLICY tenancy_administration_sink_fault ON tenants; DROP FUNCTION tenancy_administration_sink_fault()");
        }
        Assert.Equal(before, await ScalarAsync<string>(owner, null, "SELECT pg_get_expr(polqual,polrelid) FROM pg_policy WHERE polrelid='tenants'::regclass ORDER BY polname LIMIT 1"));
        caller!.Stop();
        sinks.AssertCompleteEfTraceCoverage();
        IdentityDiagnosticSinks.AssertEfCallerAncestry(sinks.Traces, caller);
        await sinks.CaptureThroughL1Async(failure, expectedStatus: 500);
        var captured = await sinks.ReadAsync();
        captured.AssertPositiveControls("internal_error");
        if (protectedReader) captured.AssertExcludes(canaries);
        else
        {
            foreach (var canary in canaries)
            {
                Assert.Contains(canary, captured.Log, StringComparison.Ordinal);
                Assert.Contains(canary, captured.Local, StringComparison.Ordinal);
                Assert.Contains(canary, captured.Sentry, StringComparison.Ordinal);
                Assert.Contains(canary, captured.Trace, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_write_provider_payload_has_dirty_and_human_protected_diagnostic_sink_twins(bool protectedWriter)
    {
        var canaries = new[] { "write-neutral-63c19", "write-nested-8da21", "write-secret-379bc" };
        var payload = JsonSerializer.Serialize(new { value = canaries[0], items = new[] { new { value = canaries[1], token = canaries[2] } } });
        Assert.InRange(payload.Length, 1, MappedLength.DisplayName);
        var create = await fixture.SendAsync(new CreateManagedOrganizationCommand(
            "write-diagnostic-" + Guid.NewGuid().ToString("N"), "Unchanged write diagnostic target"), "tenancy-write-diagnostic-create");
        Assert.True(create.IsSuccess);
        var id = create.Value!.OrganizationId;
        var before = await OrganizationSnapshotAsync(id);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        using var sinks = new IdentityDiagnosticSinks();
        using var callers = new ActivitySource("learnstack.identity.storage-qualification");
        using var caller = callers.StartActivity("tenancy-administration-write");
        Assert.NotNull(caller);
        var witness = new SaveFailureWitness();
        Exception failure;
        await ExecuteAsync(owner, null, $$"""
            CREATE FUNCTION tenancy_administration_write_sink_fault() RETURNS trigger
            LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
            BEGIN
                RAISE EXCEPTION USING ERRCODE='P0399',MESSAGE=NEW.display_name;
            END $$;
            REVOKE ALL ON FUNCTION tenancy_administration_write_sink_fault() FROM PUBLIC;
            CREATE TRIGGER tenancy_administration_write_sink_fault BEFORE UPDATE ON organizations
            FOR EACH ROW WHEN (OLD.id='{{id.Value:D}}'::uuid)
            EXECUTE FUNCTION tenancy_administration_write_sink_fault();
            """);
        try
        {
            if (protectedWriter)
            {
                SourceFailureStore? observed = null;
                await using var harness = await fixture.CreateAsync("tenancy-write-diagnostic-protected", configure: services =>
                {
                    services.AddSingleton<ILoggerFactory>(sinks.Loggers);
                    services.AddSingleton<ISaveChangesInterceptor>(witness);
                    services.AddScoped<IOrganizationWriteStore>(provider => observed = new SourceFailureStore(
                        ActivatorUtilities.CreateInstance<OrganizationWriteStore>(provider)));
                });
                var result = await harness.Sender.Send(new RenameOrganizationCommand(id, create.Value.Version, payload));
                Assert.Equal("dependency_unavailable", result.Error!.Code);
                Assert.NotNull(observed);
                var translated = Assert.IsType<TenancyAdministrationStorageException>(observed.Failure);
                Assert.Equal(TenancyAdministrationStorageFailure.Provider, translated.Failure);
                Assert.Null(translated.InnerException);
                failure = translated;
            }
            else
            {
                // The dirty twin intentionally bypasses the human store boundary,
                // while retaining actual tracked EF writes and an announced app owner.
                await using var contextHarness = await fixture.CreateAsync("tenancy-write-diagnostic-context");
                var context = contextHarness.Services.GetRequiredService<ITenantContext>();
                await using var source = ApplicationDataSource.Build(fixture.AppConnectionString, sinks.Loggers);
                await using var unit = new NpgsqlUnitOfWork(source, sinks.Loggers.CreateLogger<NpgsqlUnitOfWork>());
                await using var frame = await unit.BeginTransactionAsync(TransactionMode.ReadWrite);
                await unit.SetTenantContextAsync(context);
                await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction!);
                await new IdentityMutationLocks(unit).AcquireAsync(new([], context.TenantId, IdentityTenantLockMode.Shared));
                await using var db = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(unit.Connection)
                    .UseLoggerFactory(sinks.Loggers).AddInterceptors(witness).Options, new StaticTenantContextAccessor(context));
                await db.Database.UseTransactionAsync(unit.Transaction);
                var target = await db.Organizations.SingleAsync(row => row.Id == id);
                target.Rename(payload, new SystemClock(), context.UserId!.Value);
                failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                await frame.FailAsync();
            }
            Assert.Equal(1, witness.Failures);
            var provider = Assert.IsType<PostgresException>(Assert.IsType<DbUpdateException>(witness.Failure).InnerException);
            Assert.Equal("P0399", provider.SqlState);
            Assert.Equal(payload, provider.MessageText);
        }
        finally
        {
            await ExecuteAsync(owner, null, "DROP TRIGGER tenancy_administration_write_sink_fault ON organizations; DROP FUNCTION tenancy_administration_write_sink_fault()");
        }
        Assert.Equal(before, await OrganizationSnapshotAsync(id));
        caller!.Stop();
        sinks.AssertCompleteEfTraceCoverage();
        IdentityDiagnosticSinks.AssertEfCallerAncestry(sinks.Traces, caller);
        var errors = sinks.Traces.Where(trace => trace.Source == IdentityDiagnosticSinks.EfSource
            && trace.Status == ActivityStatusCode.Error).ToArray();
        Assert.NotEmpty(errors);
        // The human handler normally returns a fixed Result. Project its actual
        // intercepted source exception through L1 to qualify those serialized sinks;
        // this does not claim that the normal Result path invokes L1 or error tracking.
        await sinks.CaptureThroughL1Async(failure, expectedStatus: 500);
        var captured = await sinks.ReadAsync();
        captured.AssertPositiveControls("internal_error");
        if (protectedWriter)
        {
            Assert.Contains("TenancyAdministrationStorageFailed", captured.Log, StringComparison.Ordinal);
            Assert.Contains("3292", captured.Log, StringComparison.Ordinal);
            Assert.All(errors, trace => Assert.Equal("Tenancy administration storage failed.", trace.Description));
            captured.AssertExcludes(canaries);
        }
        else
        {
            foreach (var canary in canaries)
            {
                Assert.Contains(canary, captured.Log, StringComparison.Ordinal);
                Assert.Contains(canary, captured.Sentry, StringComparison.Ordinal);
                Assert.Contains(canary, captured.Trace, StringComparison.Ordinal);
            }
            // LocalFileErrorTracker serializes only the outer DbUpdateException
            // message and the inner type. The raw read twin above independently
            // proves that this sink detects provider values when they reach it.
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_read_boundary_preserves_cancellation_and_programming_failures(bool cancellation)
    {
        await using var harness = await fixture.CreateAsync("tenancy-programming-fault");
        var context = harness.Services.GetRequiredService<ITenantContext>();
        await using var frame = await harness.Unit.BeginTransactionAsync(TransactionMode.ReadOnly);
        await harness.Unit.SetTenantContextAsync(context);
        Exception expected = cancellation ? new OperationCanceledException("fixed cancellation") : new InvalidOperationException("fixed programming failure");
        await using var db = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(harness.Unit.Connection)
            .AddInterceptors(new ThrowRead(expected)).Options, new StaticTenantContextAccessor(context));
        await db.Database.UseTransactionAsync(harness.Unit.Transaction);
        var reader = new TenancyAdministrationReader(db, harness.Unit, context);
        var actual = await Record.ExceptionAsync(() => reader.ReadTenantAsync());
        Assert.Same(expected, actual);
        await frame.FailAsync();
    }

    private async Task<string> OrganizationSnapshotAsync(OrganizationId id)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        return await ScalarAsync<string>(app, transaction, "SELECT to_jsonb(o)::text FROM organizations o WHERE id=@id", ("id", id.Value));
    }

    private sealed class SaveFailureWitness : SaveChangesInterceptor
    {
        public Exception? Failure { get; private set; }
        public int Failures { get; private set; }
        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is TenancyDbContext)
            {
                Failure = eventData.Exception;
                Failures++;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class SourceFailureStore(IOrganizationWriteStore inner) : IOrganizationWriteStore
    {
        public TenancyAdministrationStorageException? Failure { get; private set; }
        public Task<Organization?> FindForUpdateAsync(OrganizationId id, CancellationToken cancellationToken = default) =>
            inner.FindForUpdateAsync(id, cancellationToken);
        public Task AddAsync(Organization aggregate, CancellationToken cancellationToken = default) => inner.AddAsync(aggregate, cancellationToken);
        public async Task UpdateAsync(Organization aggregate, CancellationToken cancellationToken = default)
        {
            try { await inner.UpdateAsync(aggregate, cancellationToken); }
            catch (TenancyAdministrationStorageException failure)
            {
                Failure = failure;
                throw;
            }
        }
    }

    private sealed class FailureWitness : DbCommandInterceptor
    {
        public Exception? Failure { get; private set; }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Failure = eventData.Exception;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowRead(Exception failure) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) => throw failure;
    }
}
