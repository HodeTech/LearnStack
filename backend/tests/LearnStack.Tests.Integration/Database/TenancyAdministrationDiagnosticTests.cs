using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
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
                Assert.Contains(canary, captured.Trace, StringComparison.Ordinal);
            }
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
