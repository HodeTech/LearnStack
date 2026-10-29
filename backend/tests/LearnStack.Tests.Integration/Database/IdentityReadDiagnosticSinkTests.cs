using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using IdentityRoleId = LearnStack.Modules.Identity.Domain.RoleId;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Current contextual read ports preserve G19 without a public Identity route.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(IdentityDiagnosticSinkGroup.Name)]
public sealed class IdentityReadDiagnosticSinkTests(IdentityMembershipFixture fixture)
{
    private const string Neutral = "identity-read-neutral-827be";
    private const string Nested = "identity-read-nested-912ac";
    private const string Sensitive = "identity-read-sensitive-613fd";
    private static readonly string Payload = JsonSerializer.Serialize(new
    {
        value = Neutral,
        items = new[] { new { value = Nested, token = Sensitive } },
    });
    private static readonly string[] Canaries = [Neutral, Nested, Sensitive];

    [Theory]
    [InlineData("role-find", false)]
    [InlineData("role-history", true)]
    [InlineData("membership-find", false)]
    [InlineData("membership-find", true)]
    [InlineData("membership-history", false)]
    [InlineData("role-read", true)]
    [InlineData("seed-role", false)]
    [InlineData("seed-membership", true)]
    public async Task Actual_contextual_read_ports_drop_provider_payloads_before_the_existing_diagnostic_sinks(
        string port, bool transient)
    {
        // 55000 exercises the non-retrying EF wrapper without 53300's separate
        // connector-break/secondary-cleanup behavior.
        var sqlState = transient ? PostgresErrorCodes.ObjectNotInPrerequisiteState : "P0399";
        var table = port is "role-find" or "role-read" or "seed-role" ? "roles" : "memberships";
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        await StoreCanaryAsync(database.AppConnectionString);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        var originalPolicies = await PolicyCensusAsync(owner);
        using var sinks = new IdentityDiagnosticSinks();
        using var callers = new ActivitySource("learnstack.identity.storage-qualification");
        using var caller = callers.StartActivity("contextual-read-port");
        caller.Should().NotBeNull();
        await using var source = ApplicationDataSource.Build(database.AppConnectionString, sinks.Loggers);
        await AssertReadSucceedsAsync(source, sinks.Loggers, port);
        await ExecuteAsync(owner, null, $$"""
            CREATE FUNCTION public.identity_contextual_read_sink_fault() RETURNS boolean
                LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
            DECLARE payload text;
            BEGIN
                SELECT display_name INTO STRICT payload FROM public.membership_profiles
                WHERE membership_id='{{LearnerMembershipA:D}}'::uuid;
                RAISE EXCEPTION USING ERRCODE='{{sqlState}}', MESSAGE=payload;
            END $$;
            REVOKE ALL ON FUNCTION public.identity_contextual_read_sink_fault() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION public.identity_contextual_read_sink_fault() TO learnstack_app;
            CREATE POLICY identity_contextual_read_sink_fault ON public.{{table}}
                AS RESTRICTIVE FOR SELECT TO learnstack_app
                USING(public.identity_contextual_read_sink_fault());
            """);
        Exception? failure = null;
        try
        {
            (await PolicyCensusAsync(owner)).Should().NotBe(originalPolicies);
            var witness = new ProviderFailureWitness();
            await using var unit = new NpgsqlUnitOfWork(source, sinks.Loggers.CreateLogger<NpgsqlUnitOfWork>());
            await using var frame = await unit.BeginTransactionAsync();
            await unit.SetProvisioningTenantContextAsync(TenantId.From(SchemaFixture.TenantA));
            await SchemaQueries.AssertApplicationRoleAsync((NpgsqlConnection)unit.Connection);
            await using var db = CreateContext(unit, sinks.Loggers, witness);
            await db.Database.UseTransactionAsync(unit.Transaction);
            failure = await Record.ExceptionAsync(() => ReadPortAsync(db, new ContextualIdentityStorageControl(db, unit), unit, port));
            witness.Failure.Should().BeOfType<PostgresException>();
            var providerFailure = (PostgresException)witness.Failure!;
            providerFailure.SqlState.Should().Be(sqlState);
            providerFailure.MessageText.Should().Be(Payload,
                "the app-role policy must read the stored neutral, nested and sensitive values before failing");
            witness.Commands.Should().Be(1);
            await frame.FailAsync();
            failure.Should().BeOfType<IdentityStorageProviderException>();
            failure!.InnerException.Should().BeNull();
        }
        finally
        {
            await ExecuteAsync(owner, null, $"""
                DROP POLICY identity_contextual_read_sink_fault ON public.{table};
                DROP FUNCTION public.identity_contextual_read_sink_fault();
                """);
        }
        (await PolicyCensusAsync(owner)).Should().Be(originalPolicies);
        await AssertReadSucceedsAsync(source, sinks.Loggers, port);
        Activity.Current.Should().BeSameAs(caller);
        caller!.Stop();
        caller.Status.Should().Be(ActivityStatusCode.Unset);
        sinks.AssertCompleteEfTraceCoverage();
        IdentityDiagnosticSinks.AssertEfCallerAncestry(sinks.Traces, caller);
        sinks.Traces.Where(trace => trace.Source == IdentityDiagnosticSinks.EfSource
            && trace.Status == ActivityStatusCode.Error).Should().NotBeEmpty()
            .And.OnlyContain(trace => trace.Description == "Identity storage command failed.");
        // Reuse the established real sink vehicle; this is not an added HTTP route.
        await sinks.CaptureThroughL1Async(failure!);
        var captured = await sinks.ReadAsync();
        captured.AssertPositiveControls();
        captured.Log.Should().Contain("IdentityStorageReadFailed").And.NotContain("QueryIterationFailed");
        captured.AssertExcludes(Canaries);
    }

    [Fact]
    public async Task Current_read_ports_preserve_cancellation_and_programming_failures()
    {
        await using var source = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetProvisioningTenantContextAsync(TenantId.From(SchemaFixture.TenantA));
        var witness = new ProviderFailureWitness();
        await using var db = CreateContext(unit, NullLoggerFactory.Instance, witness);
        await db.Database.UseTransactionAsync(unit.Transaction);
        var control = new ContextualIdentityStorageControl(db, unit);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReadPortAsync(db, control, unit, "role-read", cancellation.Token));
        cancelled.CancellationToken.Should().Be(cancellation.Token);
        witness.Commands.Should().Be(0);
        await db.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ReadPortAsync(db, control, unit, "role-read"));
        witness.Commands.Should().Be(0);
    }

    private static IdentityDbContext CreateContext(NpgsqlUnitOfWork unit, ILoggerFactory loggers,
        params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(unit.Connection).UseLoggerFactory(loggers).AddInterceptors(interceptors).Options,
        new StaticTenantContextAccessor(new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null)),
        EmptyIdentityScope.Instance);

    private static async Task<object?> ReadPortAsync(IdentityDbContext db, ContextualIdentityStorageControl control, NpgsqlUnitOfWork unit,
        string port, CancellationToken cancellationToken = default)
    {
        var role = IdentityRoleId.From(RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant));
        var member = MembershipId.From(LearnerMembershipA);
        return port switch
        {
            "role-find" => await new RoleWriteStore(db, control).FindAsync(role, cancellationToken),
            "role-history" => await new RoleWriteStore(db, control).HasMembershipHistoryAsync(cancellationToken),
            "membership-find" => await new MembershipWriteStore(db, control).FindAsync(member, cancellationToken),
            "membership-history" => await new MembershipWriteStore(db, control).HasMembershipHistoryAsync(cancellationToken),
            "role-read" => await new RoleReadStore(db, unit, control).ReadAsync(role, cancellationToken),
            "seed-role" => await new SeedStateReader(db, unit).ReadRoleAsync(role, cancellationToken),
            "seed-membership" => await new SeedStateReader(db, unit).ReadMembershipAsync(member, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(port)),
        };
    }

    private static async Task AssertReadSucceedsAsync(NpgsqlDataSource source, ILoggerFactory loggers, string port)
    {
        await using var unit = new NpgsqlUnitOfWork(source, loggers.CreateLogger<NpgsqlUnitOfWork>());
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetProvisioningTenantContextAsync(TenantId.From(SchemaFixture.TenantA));
        await using var db = CreateContext(unit, loggers);
        await db.Database.UseTransactionAsync(unit.Transaction);
        var value = await ReadPortAsync(db, new ContextualIdentityStorageControl(db, unit), unit, port);
        if (port.EndsWith("history", StringComparison.Ordinal)) value.Should().Be(true);
        else value.Should().NotBeNull();
        await frame.CompleteAsync();
    }

    private static async Task StoreCanaryAsync(string connectionString)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
        await ExecuteAsync(app, transaction, """
            UPDATE public.membership_profiles SET display_name=@payload WHERE membership_id=@member;
            SET CONSTRAINTS ALL IMMEDIATE;
            """, ("payload", Payload), ("member", LearnerMembershipA));
        await transaction.CommitAsync();
    }

    private static Task<string> PolicyCensusAsync(NpgsqlConnection owner) => ScalarAsync<string>(owner, null, """
        SELECT jsonb_build_object(
            'policies',(SELECT jsonb_agg(jsonb_build_array(polrelid,polname,polpermissive,polcmd,polroles::text,
                pg_get_expr(polqual,polrelid),pg_get_expr(polwithcheck,polrelid)) ORDER BY polrelid,polname)
                FROM pg_policy WHERE polrelid IN ('public.roles'::regclass,'public.memberships'::regclass)),
            'function_exists',to_regprocedure('public.identity_contextual_read_sink_fault()') IS NOT NULL)::text
        """);

    private sealed class ProviderFailureWitness : DbCommandInterceptor
    {
        public Exception? Failure { get; private set; }
        public int Commands { get; private set; }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Failure = eventData.Exception;
            Commands++;
            return Task.CompletedTask;
        }
    }
}
