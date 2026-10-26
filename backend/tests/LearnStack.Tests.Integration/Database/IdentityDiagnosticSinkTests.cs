using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Pipeline;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IdentityDiagnosticSinkGroup : ICollectionFixture<IdentityMembershipFixture>
{
    public const string Name = "Identity diagnostic sinks";
}

/// <summary>G19 value-bearing failures through actual storage and serialized output sinks.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(IdentityDiagnosticSinkGroup.Name)]
public sealed class IdentityDiagnosticSinkTests(IdentityMembershipFixture fixture, ITestOutputHelper output)
{
    private const string Neutral = "identity-neutral-472bc";
    private const string Nested = "identity-nested-845ad";
    private const string Sensitive = "identity-sensitive-763ef";
    private static readonly string Payload = JsonSerializer.Serialize(new
    {
        value = Neutral,
        items = new[] { new { value = Nested, token = Sensitive } },
    });
    private static readonly string[] Canaries = [Neutral, Nested, Sensitive];

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Overlapping_compositions_protect_every_Identity_span_without_changing_actual_callers(
        bool asynchronous, bool nestedInSiblingCommand)
    {
        const string siblingValue = "ordinary-sibling-provider-control-938c";
        const string callerDescription = "ordinary-caller-diagnostic-281d";
        using var sinks = new IdentityDiagnosticSinks();
        using var otherTracing = new IdentityDiagnosticSinks.TraceComposition();
        using var observed = new IdentityDiagnosticSinks.EfActivityObserver();
        using var callers = new ActivitySource("learnstack.identity.storage-qualification");
        using var caller = callers.StartActivity("overlapping-compositions-caller");
        caller.Should().NotBeNull();
        caller!.SetStatus(ActivityStatusCode.Error, callerDescription);
        await using var identityConnection = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(identityConnection);
        await using var identityTransaction = await identityConnection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(identityConnection, identityTransaction, SchemaFixture.TenantA);
        var tenant = new StaticTenantContextAccessor(new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null));
        var witness = new ProviderFailureWitness();
        await using var identity = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(identityConnection).UseLoggerFactory(sinks.Loggers).AddInterceptors(witness).Options,
            tenant, EmptyIdentityScope.Instance);
        await identity.Database.UseTransactionAsync(identityTransaction);
        var identityQuery = identity.Database.SqlQuery<int>($"SELECT CAST({Payload} AS integer) AS \"Value\"");
        PostgresException? identityFailure = null;
        IdentityDiagnosticSinks.ExportedTrace[][] identityCompleted = [];

        void CaptureIdentityCompletion()
        {
            identityFailure.Should().NotBeNull();
            identityFailure!.SqlState.Should().Be("22P02");
            foreach (var canary in Canaries) identityFailure.MessageText.Should().Contain(canary);
            witness.Failure.Should().BeSameAs(identityFailure);
            witness.Commands.Should().Be(1, "one actual provider command generated all the nested spans");
            identityCompleted = [sinks.Traces.ToArray(), otherTracing.Traces.ToArray()];
        }

        void ReadIdentity()
        {
            identityFailure = Record.Exception(() => identityQuery.Single()) as PostgresException;
            CaptureIdentityCompletion();
        }

        async Task ReadIdentityAsync()
        {
            identityFailure = await Record.ExceptionAsync(() => identityQuery.SingleAsync()) as PostgresException;
            CaptureIdentityCompletion();
        }

        PostgresException? siblingFailure = null;
        if (nestedInSiblingCommand)
        {
            // The actual sibling EF command remains current while the separate
            // Identity connection fails. Completion snapshots distinguish the
            // operations without using the runtime marker or its ancestry walk.
            var nested = new NestedIdentityRead(ReadIdentity, ReadIdentityAsync);
            await using var siblingConnection = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(siblingConnection);
            await using var siblingTransaction = await siblingConnection.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(siblingConnection, siblingTransaction, SchemaFixture.TenantA);
            await using var sibling = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>()
                .UseNpgsql(siblingConnection).UseLoggerFactory(sinks.Loggers).AddInterceptors(nested).Options, tenant);
            await sibling.Database.UseTransactionAsync(siblingTransaction);
            var query = sibling.Database.SqlQuery<int>($"SELECT CAST({siblingValue} AS integer) AS \"Value\"");
            siblingFailure = (asynchronous
                ? await Record.ExceptionAsync(() => query.SingleAsync())
                : Record.Exception(() => query.Single())) as PostgresException;
            siblingFailure.Should().NotBeNull();
            siblingFailure!.MessageText.Should().Contain(siblingValue);
            nested.Calls.Should().Be(1);
            nested.Caller.Should().NotBeNull();
            nested.Caller!.Source.Name.Should().Be(IdentityDiagnosticSinks.EfSource);
            nested.Caller.StatusDescription.Should().Be(siblingFailure.Message);
        }
        else if (asynchronous) await ReadIdentityAsync();
        else ReadIdentity();

        Activity.Current.Should().BeSameAs(caller);
        caller.Stop();
        caller.Status.Should().Be(ActivityStatusCode.Error);
        caller.StatusDescription.Should().Be(callerDescription);
        var completed = new[] { sinks.Traces, otherTracing.Traces };
        output.WriteLine(JsonSerializer.Serialize(new
        {
            asynchronous,
            nestedInSiblingCommand,
            ProviderCommandFailures = witness.Commands,
            Compositions = completed.Select(traces => new
            {
                ErrorSpans = traces.Where(trace => trace.Source == IdentityDiagnosticSinks.EfSource
                    && trace.Status == ActivityStatusCode.Error).Select(trace => new { trace.Id, trace.ParentId, trace.Description }),
                ContainsIdentityCanary = Canaries.Any(canary => traces.Any(trace => trace.Json.Contains(canary, StringComparison.Ordinal))),
            }),
        }));
        for (var index = 0; index < completed.Length; index++)
        {
            observed.AssertCompleteCoverage(completed[index]);
            IdentityDiagnosticSinks.AssertEfCallerAncestry(completed[index], caller);
            var identityErrors = identityCompleted[index].Where(trace => trace.Source == IdentityDiagnosticSinks.EfSource
                && trace.Status == ActivityStatusCode.Error).ToArray();
            identityErrors.Length.Should().BeGreaterThanOrEqualTo(2, "both actual compositions create a command span");
            identityErrors.Should().OnlyContain(trace => trace.Description == "Identity storage command failed.");
            foreach (var canary in Canaries)
                string.Join('\n', completed[index].Select(trace => trace.Json)).Should().NotContain(canary);
            completed[index].Single(trace => trace.Id == caller.Id).Description.Should().Be(callerDescription);
            if (nestedInSiblingCommand)
            {
                var siblingErrors = completed[index].Skip(identityCompleted[index].Length)
                    .Where(trace => trace.Source == IdentityDiagnosticSinks.EfSource && trace.Status == ActivityStatusCode.Error).ToArray();
                siblingErrors.Length.Should().BeGreaterThanOrEqualTo(2);
                siblingErrors.Should().OnlyContain(trace => trace.Description == siblingFailure!.Message);
            }
        }
        sinks.LogText.Should().Contain("IdentityStorageReadFailed");
        foreach (var canary in Canaries) sinks.LogText.Should().NotContain(canary);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Actual_profile_provider_failure_reaches_source_and_never_reaches_serialized_diagnostic_sinks(bool deferred, bool transient)
    {
        var sqlState = transient ? PostgresErrorCodes.ObjectNotInPrerequisiteState : "P0399";
        using var sinks = new IdentityDiagnosticSinks();
        var witness = new ProviderFailureWitness();
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.MigrationConnectionString);
        // The owned database alone receives this trigger. PostgreSQL's MESSAGE is
        // value-bearing even with Include Error Detail disabled (the shipped default).
        await ExecuteAsync(owner, null, $"""
            CREATE SEQUENCE public.identity_sink_source;
            GRANT USAGE, SELECT ON public.identity_sink_source TO learnstack_app;
            CREATE FUNCTION public.identity_sink_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW.display_name::jsonb->>'value'='{Neutral}'
                 AND NEW.display_name::jsonb->'items'->0->>'value'='{Nested}'
                 AND NEW.display_name::jsonb->'items'->0->>'token'='{Sensitive}' THEN
                PERFORM nextval('public.identity_sink_source');
              END IF;
              RAISE EXCEPTION USING ERRCODE='{sqlState}', MESSAGE=NEW.display_name;
            END $$;
            """);
        await ExecuteAsync(owner, null, deferred ? """
            CREATE CONSTRAINT TRIGGER identity_sink_failure AFTER UPDATE ON public.membership_profiles
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.identity_sink_failure();
            """ : """
            CREATE TRIGGER identity_sink_failure BEFORE UPDATE ON public.membership_profiles
            FOR EACH ROW EXECUTE FUNCTION public.identity_sink_failure();
            """);
        try
        {
            using var parentSource = new ActivitySource("learnstack.identity.storage-qualification");
            using var parent = parentSource.StartActivity("identity-storage-parent");
            parent.Should().NotBeNull();
            await using var source = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString, sinks.Loggers);
            await using var unit = new NpgsqlUnitOfWork(source, sinks.Loggers.CreateLogger<NpgsqlUnitOfWork>());
            await using var frame = await unit.BeginTransactionAsync();
            var tenant = TenantId.From(SchemaFixture.TenantA);
            await unit.SetProvisioningTenantContextAsync(tenant);
            await new IdentityMutationLocks(unit).AcquireAsync(new([], tenant, IdentityTenantLockMode.Shared));
            var scope = new StaticTenantContextAccessor(new SeedTenantContext(tenant, null));
            await using var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(unit.Connection).UseLoggerFactory(sinks.Loggers).AddInterceptors(witness).Options,
                scope, EmptyIdentityScope.Instance);
            await db.Database.UseTransactionAsync(unit.Transaction);
            var store = new MembershipWriteStore(db, new ContextualIdentityStorageControl(db, unit));
            var member = await store.FindAsync(MembershipId.From(LearnerMembershipA));
            member.Should().NotBeNull();
            member!.SetProfile(Payload, "en", DateTimeOffset.UtcNow, UserId.SystemActor).IsSuccess.Should().BeTrue();
            member.Profile.DisplayName.Should().Be(Payload, "the real tracked personal field carries all three values");
            var failure = await Record.ExceptionAsync(async () =>
            {
                await store.UpdateAsync(member);
                if (deferred) await store.ValidateGraphAsync();
            });
            failure.Should().NotBeNull();
            if (!deferred)
            {
                witness.Failure.Should().BeOfType<PostgresException>();
                ((PostgresException)witness.Failure!).SqlState.Should().Be(sqlState);
                witness.Failure!.Message.Should().Contain(Payload, "the real provider must receive and return the planted values");
            }
            unit.IsRollbackOnly.Should().BeTrue();
            await frame.FailAsync();
            await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(observer);
            (await ScalarAsync<bool>(observer, null, "SELECT is_called FROM public.identity_sink_source")).Should().BeTrue(
                "the provider itself confirms all three exact values reached its failing trigger, including the deferred path");

            Activity.Current.Should().BeSameAs(parent, "stopping the actual EF span restores its existing caller context");
            sinks.AssertCompleteEfTraceCoverage();
            IdentityDiagnosticSinks.AssertEfCallerAncestry(sinks.Traces, parent!);
            parent!.Stop();
            parent.Status.Should().Be(ActivityStatusCode.Unset);
            await sinks.CaptureThroughL1Async(failure!);
            var captured = await sinks.ReadAsync();
            output.WriteLine(JsonSerializer.Serialize(new
            {
                Deferred = deferred,
                SourceReached = true,
                EscapedType = failure!.GetType().Name,
                LogContainsCanary = Canaries.Any(captured.Log.Contains),
                LocalContainsCanary = Canaries.Any(captured.Local.Contains),
                SentryContainsCanary = Canaries.Any(captured.Sentry.Contains),
                ResponseContainsCanary = Canaries.Any(captured.Response.Contains),
                TraceContainsCanary = Canaries.Any(captured.Trace.Contains),
                ExportedSpans = sinks.Traces.Count,
                ErrorSpans = sinks.Traces.Count(trace => trace.Status == System.Diagnostics.ActivityStatusCode.Error),
            }));
            captured.AssertPositiveControls();
            failure.Should().BeOfType<IdentityStorageProviderException>();
            if (!deferred) captured.Log.Should().Contain("IdentityStorageSaveFailed").And.Contain("3191");
            var efErrors = sinks.Traces.Where(trace => trace.Source == "OpenTelemetry.Instrumentation.EntityFrameworkCore"
                && trace.Status == System.Diagnostics.ActivityStatusCode.Error).ToArray();
            if (deferred) efErrors.Should().BeEmpty();
            else
            {
                witness.Commands.Should().Be(1);
                efErrors.Should().NotBeEmpty().And.OnlyContain(trace => trace.Description == "Identity storage command failed.");
            }
            sinks.Traces.Where(trace => trace.Source == "learnstack.identity.sink-qualification")
                .Should().HaveCount(2).And.OnlyContain(trace => trace.Status == System.Diagnostics.ActivityStatusCode.Error
                    && trace.Description == nameof(IdentityStorageProviderException));
            captured.AssertExcludes(Canaries);
            failure.InnerException.Should().BeNull("the adapter must not retain an original provider payload");
        }
        finally
        {
            await ExecuteAsync(owner, null, """
                DROP TRIGGER identity_sink_failure ON public.membership_profiles;
                DROP FUNCTION public.identity_sink_failure();
                DROP SEQUENCE public.identity_sink_source;
                """);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_global_binding_save_keeps_its_fixed_boundary_and_replaces_pre_adapter_EF_failure_output(bool transient)
    {
        var sqlState = transient ? PostgresErrorCodes.LockNotAvailable : "P0399";
        using var sinks = new IdentityDiagnosticSinks();
        var witness = new ProviderFailureWitness();
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.MigrationConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE FUNCTION public.identity_binding_sink_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION USING ERRCODE='P0399', MESSAGE=NEW.provider_subject; END $$;
            CREATE TRIGGER identity_binding_sink_failure BEFORE INSERT ON public.users
            FOR EACH ROW EXECUTE FUNCTION public.identity_binding_sink_failure();
            """.Replace("P0399", sqlState, StringComparison.Ordinal));
        try
        {
            using var parentSource = new ActivitySource("learnstack.identity.storage-qualification");
            using var parent = parentSource.StartActivity("identity-storage-parent");
            parent.Should().NotBeNull();
            await using var source = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString, sinks.Loggers);
            await using var unit = new NpgsqlUnitOfWork(source, sinks.Loggers.CreateLogger<NpgsqlUnitOfWork>());
            var subject = new GlobalIdentitySubject(Authority.AuthorityId, Authority.Issuer, Payload);
            var descriptor = ProofOwner.Create(subject);
            await using var frame = await unit.BeginGlobalIdentityTransactionAsync(descriptor, TransactionMode.ReadWrite);
            await unit.AcquireGlobalIdentitySubjectLockAsync(descriptor);
            await using var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(unit.Connection).UseLoggerFactory(sinks.Loggers).AddInterceptors(witness).Options,
                StaticTenantContextAccessor.Unresolved, new GlobalIdentityScopeAccessor(unit));
            var store = new UserBindingStore(db, unit);
            (await store.FindExactAsync(subject)).Should().BeNull();
            var creation = ProofCreation.Create(unit);
            var user = User.Create(subject, creation, creation.Timestamp, UserId.SystemActor);
            user.ProviderSubject.Should().Be(Payload);
            var failure = await Record.ExceptionAsync(() => store.AddAsync(user));
            failure.Should().BeOfType<UserBindingStorageException>();
            witness.Failure.Should().BeOfType<PostgresException>();
            ((PostgresException)witness.Failure!).SqlState.Should().Be(sqlState);
            witness.Failure!.Message.Should().Contain(Payload);
            await frame.FailAsync();
            Activity.Current.Should().BeSameAs(parent, "stopping the actual EF span restores its existing caller context");
            sinks.AssertCompleteEfTraceCoverage();
            IdentityDiagnosticSinks.AssertEfCallerAncestry(sinks.Traces, parent!);
            parent!.Stop();
            parent.Status.Should().Be(ActivityStatusCode.Unset);
            await sinks.CaptureThroughL1Async(failure!);
            var captured = await sinks.ReadAsync();
            captured.AssertPositiveControls();
            captured.Log.Should().Contain("IdentityStorageSaveFailed").And.Contain("3191");
            witness.Commands.Should().Be(1);
            sinks.Traces.Where(trace => trace.Source == "OpenTelemetry.Instrumentation.EntityFrameworkCore"
                && trace.Status == System.Diagnostics.ActivityStatusCode.Error)
                .Should().NotBeEmpty().And.OnlyContain(trace => trace.Description == "Identity storage command failed.");
            captured.AssertExcludes(Canaries);
            failure!.InnerException.Should().BeNull();
        }
        finally
        {
            await ExecuteAsync(owner, null, """
                DROP TRIGGER identity_binding_sink_failure ON public.users;
                DROP FUNCTION public.identity_binding_sink_failure();
                """);
        }
    }

    [Fact]
    public async Task Another_modules_EF_save_failure_event_remains_enabled()
    {
        using var sinks = new IdentityDiagnosticSinks();
        await using var source = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString, sinks.Loggers);
        await using var unit = new NpgsqlUnitOfWork(source, sinks.Loggers.CreateLogger<NpgsqlUnitOfWork>());
        await using var frame = await unit.BeginTransactionAsync();
        var tenant = TenantId.From(SchemaFixture.TenantA);
        await unit.SetProvisioningTenantContextAsync(tenant);
        await using var db = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(unit.Connection).UseLoggerFactory(sinks.Loggers).Options,
            new StaticTenantContextAccessor(new SeedTenantContext(tenant, null)));
        await db.Database.UseTransactionAsync(unit.Transaction);
        // An ordinary RLS refusal from a sibling module must still retain EF's
        // own event. No personal canary is deliberately sent to that boundary.
        db.Tenants.Add(Tenant.Create(TenantId.From(Guid.CreateVersion7()), "sink-control", "Sink control",
            new SystemClock(), UserId.SystemActor));
        var save = () => db.SaveChangesAsync();
        var failure = (await save.Should().ThrowAsync<DbUpdateException>()).Which;
        failure.InnerException.Should().BeOfType<PostgresException>();
        sinks.AssertCompleteEfTraceCoverage();
        sinks.Traces.Where(trace => trace.Source == "OpenTelemetry.Instrumentation.EntityFrameworkCore"
            && trace.Status == System.Diagnostics.ActivityStatusCode.Error)
            .Should().NotBeEmpty().And.OnlyContain(trace => trace.Description == failure.InnerException!.Message,
                "the Identity boundary must leave the sibling module's actual error status description intact");
        sinks.LogText.Should().Contain("Microsoft.EntityFrameworkCore.Update.SaveChangesFailed")
            .And.NotContain("IdentityStorageSaveFailed");
        await frame.FailAsync();
    }

    [Fact]
    public async Task Actual_profile_validation_drops_attempted_values_and_emits_fixed_nonempty_response()
    {
        var request = new SetSeedMembershipProfileCommand(LearnerMembershipA, UserId.From(PersonA),
            RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant), PermissionScope.Tenant,
            Payload + new string('x', MembershipProfile.MaximumDisplayNameLength), "en", 1, new string('a', 64));
        var validator = new SetSeedMembershipProfileCommandValidator();
        var source = await validator.ValidateAsync(request);
        source.IsValid.Should().BeFalse();
        source.Errors.Should().Contain(failure => ReferenceEquals(failure.AttemptedValue, request),
            "the real object-level rule receives the entire value-bearing Identity command");
        var raw = JsonSerializer.Serialize(source.Errors.Select(failure => failure.AttemptedValue));
        foreach (var canary in Canaries) raw.Should().Contain(canary);
        var behavior = new ValidationBehavior<SetSeedMembershipProfileCommand, Result<None>>([validator]);
        var result = await behavior.Handle(request, () => throw new InvalidOperationException("Invalid input reached handler."), CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        var diagnostics = JsonSerializer.Serialize(result.Error);
        var response = JsonSerializer.Serialize(LearnStack.Api.Common.ProblemDetailsFactory.For(result.Error!));
        diagnostics.Should().Contain("lockey_validation_failed");
        response.Should().Contain("validation_failed").And.Contain("errors");
        foreach (var canary in Canaries)
        {
            diagnostics.Should().NotContain(canary);
            response.Should().NotContain(canary);
        }
    }

    private sealed class ProofOwner : GlobalIdentityOwnerDescriptor.MintingSource
    {
        public static GlobalIdentityOwnerDescriptor Create(GlobalIdentitySubject subject) =>
            ForBootstrap(subject, typeof(IdentityDbContext), typeof(User));
    }

    private sealed class ProofCreation : GlobalIdentityCreationReference.MintingSource
    {
        public static GlobalIdentityCreationReference Create(NpgsqlUnitOfWork unit) => Mint(unit,
            unit.GetGlobalIdentityAnnouncement(unit.Transaction)!, UserId.From(Guid.CreateVersion7()),
            AuditEntryId.From(Guid.CreateVersion7()), DateTimeOffset.UtcNow);
    }

    private sealed class ProviderFailureWitness : DbCommandInterceptor
    {
        public Exception? Failure { get; private set; }
        public int Commands { get; private set; }
        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        {
            Failure = eventData.Exception;
            Commands++;
        }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            CommandFailed(command, eventData);
            return Task.CompletedTask;
        }
    }

    private sealed class NestedIdentityRead(Action read, Func<Task> readAsync) : DbCommandInterceptor
    {
        public int Calls { get; private set; }
        public Activity? Caller { get; private set; }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Calls++;
            Caller = Activity.Current;
            read();
            Activity.Current.Should().BeSameAs(Caller);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Calls++;
            Caller = Activity.Current;
            await readAsync();
            Activity.Current.Should().BeSameAs(Caller);
            return result;
        }
    }
}
