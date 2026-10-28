using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class GlobalIdentityBootstrapTests
{
    [Fact]
    public async Task Self_bootstrap_uses_the_production_common_owner_and_repeat_consumes_no_creation_budget()
    {
        await using var provider = SelfProvider();
        await QualifyAsync(provider);

        var created = await BootstrapAsync(provider, Subject);
        created.Refusal.Should().BeNull();
        created.Binding.Outcome.Should().Be(IdentityBindingOutcome.Created);
        var actor = created.Binding.Actor!.Value;
        actor.Value.Version.Should().Be(7);
        (await BootstrapAsync(provider, Subject)).Binding.Outcome.Should().Be(IdentityBindingOutcome.Existing);
        (await ReadAsync(provider, Subject, actor)).Outcome.Should().Be(IdentityBindingOutcome.Existing);
        (await CreationCountersAsync()).Should().Be((1, 1, 1L));
        (await CountsAsync()).Should().Be((1L, 1L));
        var row = (await AuditRowsAsync()).Should().ContainSingle().Which;
        row.Metadata.Should().Contain("identity-bootstrap").And.NotContain(Subject.Subject);
        row.Outcome.Should().Be("success");
    }

    [Fact]
    public async Task Unadmitted_self_bootstrap_is_denied_before_any_creation_owner()
    {
        await using var provider = BuildProvider(_database.AppConnectionString, Registration);
        await QualifyAsync(provider);
        var result = await BootstrapAsync(provider, Subject);
        result.Binding.Outcome.Should().Be(IdentityBindingOutcome.AdmissionDenied);
        result.Binding.Actor.Should().BeNull();
        (await CreationCountersAsync()).Should().Be((0, 0, 0L));
        (await CountsAsync()).Should().Be((0L, 0L));
    }

    [Fact]
    public async Task Disabled_creation_preserves_existing_seed_binding_without_an_HTTP_attempt()
    {
        await using var provider = SelfProvider(enabled: false, admitSeed: true);
        await QualifyAsync(provider);
        (await BootstrapAsync(provider, Subject)).Refusal.Should().Be(IdentityCreationRefusal.Disabled);
        (await CreationCountersAsync()).Should().Be((0, 0, 0L));
        (await AdoptAsync(provider, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.Created);
        var existing = await BootstrapAsync(provider, Subject);
        existing.Refusal.Should().BeNull();
        existing.Binding.Outcome.Should().Be(IdentityBindingOutcome.Existing);
        existing.Binding.Actor.Should().Be(Actor);
        (await CreationCountersAsync()).Should().Be((0, 0, 1L));
        (await CountsAsync()).Should().Be((1L, 1L));
    }

    [Fact]
    public async Task Unknown_subject_with_a_claimed_actor_never_uses_HTTP_as_seed_adoption()
    {
        await using var provider = SelfProvider();
        await QualifyAsync(provider);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IIdentitySelfBootstrapper>().BootstrapAsync(Subject, Actor);
        result.Binding.Outcome.Should().Be(IdentityBindingOutcome.Conflict);
        (await CreationCountersAsync()).Should().Be((0, 0, 0L));
        (await CountsAsync()).Should().Be((0L, 0L));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Spent_window_and_clock_regression_survive_a_new_provider_without_creating_an_intent(bool day)
    {
        // A future recorded window is the dirty clock-regression control. It is
        // fixture-only deployment input; the app role cannot edit this relation.
        await ExecuteDeploymentAsync(day ? """
            UPDATE public.identity_creation_capacity SET day_attempts=100,
                day_window=date_trunc('day',clock_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' + interval '2 days'
            """ : """
            UPDATE public.identity_creation_capacity SET minute_attempts=10,
                minute_window=date_trunc('minute',clock_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' + interval '2 minutes'
            """);
        for (var restart = 0; restart < 2; restart++)
        {
            await using var provider = SelfProvider();
            await QualifyAsync(provider);
            var result = await BootstrapAsync(provider, Subject);
            result.Refusal.Should().Be(IdentityCreationRefusal.WindowExhausted);
            result.RetryAfterSeconds.Should().BeInRange(1, 86400);
            result.Binding.Actor.Should().BeNull();
        }
        (await CreationCountersAsync()).Should().Be(day ? (0, 100, 0L) : (10, 0, 0L));
        (await CountsAsync()).Should().Be((0L, 0L));
    }

    [Fact]
    public async Task Simulated_retained_capacity_boundary_is_enforced_by_actual_User_INSERT_and_rolls_back_its_increment()
    {
        // Simulate pre-existing allocation at the numeric boundary, rather than
        // claiming this fixture contains 9,999 people. Migration/installer census
        // and ordinary insert controls separately prove count initialization.
        await ExecuteDeploymentAsync("UPDATE public.identity_creation_capacity SET allocated_users=9999;");
        await using var provider = SelfProvider();
        await QualifyAsync(provider);
        (await BootstrapAsync(provider, Subject)).Binding.Outcome.Should().Be(IdentityBindingOutcome.Created);
        (await CreationCountersAsync()).Allocated.Should().Be(10000);
        var other = new GlobalIdentitySubject(AuthorityId, Registration.Issuer, "capacity-other-subject-canary");

        var refused = await BootstrapAsync(provider, other);

        refused.Binding.Outcome.Should().Be(IdentityBindingOutcome.StorageUnavailable);
        refused.Binding.Actor.Should().BeNull();
        (await CreationCountersAsync()).Should().Be((2, 2, 10000L));
        (await CountsAsync(other)).Should().Be((0L, 1L));
        (await AuditRowsAsync(other)).Should().ContainSingle().Which.Outcome.Should().Be("failed");
    }

    [Fact]
    public async Task Audit_failure_keeps_attempt_debit_but_rolls_back_allocation_then_a_clean_retry_converges()
    {
        await ExecuteDeploymentAsync("""
            CREATE FUNCTION public.proof_reject_entry_success() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.operation='identity.user.create' AND NEW.outcome='success' THEN
                    RAISE EXCEPTION 'fixed entry audit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER proof_reject_entry_success BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.proof_reject_entry_success();
            """);
        await using var provider = SelfProvider();
        await QualifyAsync(provider);
        (await BootstrapAsync(provider, Subject)).Binding.Outcome.Should().Be(IdentityBindingOutcome.StorageUnavailable);
        (await CreationCountersAsync()).Should().Be((1, 1, 0L));
        (await CountsAsync()).Should().Be((0L, 1L));
        await ExecuteDeploymentAsync("DROP TRIGGER proof_reject_entry_success ON public.audit_log; DROP FUNCTION public.proof_reject_entry_success();");

        (await BootstrapAsync(provider, Subject)).Binding.Outcome.Should().Be(IdentityBindingOutcome.Created);

        (await CreationCountersAsync()).Should().Be((2, 2, 1L));
        (await CountsAsync()).Should().Be((1L, 2L));
    }

    [Fact]
    public async Task Deployment_repeat_qualifies_actual_retained_count_without_resetting_attempts()
    {
        await using var provider = SelfProvider();
        await QualifyAsync(provider);
        (await BootstrapAsync(provider, Subject)).Binding.Outcome.Should().Be(IdentityBindingOutcome.Created);

        await InstallAsync(_database.MigrationConnectionString, Registration);

        (await CreationCountersAsync()).Should().Be((1, 1, 1L));
        await using var app = await OpenConnectionAsync(_database.AppConnectionString);
        await using var read = new NpgsqlCommand("SELECT allocated_users FROM public.identity_creation_capacity", app);
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => read.ExecuteScalarAsync());
        refusal.SqlState.Should().Be("42501");
        await using var direct = new NpgsqlCommand("SELECT * FROM public.fn_identity_creation_attempt_admission()", app);
        (await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteScalarAsync())).SqlState.Should().Be("23514");
    }

    private ServiceProvider SelfProvider(bool enabled = true, bool admitSeed = false) =>
        BuildProvider(_database.AppConnectionString, Registration, admitSeed ? new FixtureAdmission(Subject, Actor) : null,
            services => services.Replace(ServiceDescriptor.Singleton<IIdentitySelfEntryAdmission>(new FixtureSelfAdmission(enabled))));

    [Fact]
    public async Task Duplicate_app_role_INSERT_cannot_consume_retained_User_capacity()
    {
        await using var provider = SelfProvider();
        await QualifyAsync(provider);
        (await BootstrapAsync(provider, Subject)).Binding.Outcome.Should().Be(IdentityBindingOutcome.Created);
        await using (var app = await OpenConnectionAsync(_database.AppConnectionString))
        await using (var transaction = await AnnounceEvidenceAsync(app, Subject))
        {
            await using var duplicate = new NpgsqlCommand("""
                INSERT INTO public.users (id,authority_id,subject_key,provider_subject,status,
                    creation_audit_id,creation_audit_timestamp,created_at,created_by,row_version)
                SELECT id,authority_id,subject_key,provider_subject,status,
                    creation_audit_id,creation_audit_timestamp,created_at,created_by,row_version
                FROM public.users ON CONFLICT DO NOTHING
                """, app, transaction);
            (await duplicate.ExecuteNonQueryAsync()).Should().Be(0);
            await transaction.CommitAsync();
        }
        (await CreationCountersAsync()).Should().Be((1, 1, 1L));
        (await CountsAsync()).Should().Be((1L, 1L));
    }

    private static async Task<IdentityBootstrapResult> BootstrapAsync(ServiceProvider provider, GlobalIdentitySubject subject)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IIdentitySelfBootstrapper>().BootstrapAsync(subject);
    }

    private async Task<(int Minute, int Day, long Allocated)> CreationCountersAsync()
    {
        await using var owner = await OpenConnectionAsync(_database.MigrationConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT minute_attempts,day_attempts,allocated_users FROM public.identity_creation_capacity", owner);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2));
    }

    private sealed class FixtureSelfAdmission(bool enabled) : IIdentitySelfEntryAdmission
    {
        public bool CreationEnabled => enabled;
        public bool Allows(GlobalIdentitySubject subject, IdentitySelfOperation operation, UserId? claimedActor) =>
            operation == IdentitySelfOperation.Bootstrap && subject.AuthorityId == AuthorityId
            && subject.Issuer == Registration.Issuer;
    }
}
