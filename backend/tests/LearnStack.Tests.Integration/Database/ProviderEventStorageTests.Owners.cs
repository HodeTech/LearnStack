using FluentAssertions;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class ProviderEventStorageTests
{
    [Theory]
    [InlineData("counterfeit")]
    [InlineData("read-only")]
    [InlineData("nested")]
    public async Task Truthy_origin_read_only_or_nested_owner_cannot_announce_a_provider_target(string defect)
    {
        await using var source = await QualifiedSourceAsync();
        await source.AcceptAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(-1), "LOGIN",
            IdentitySubjectKey.FromBytes(SubjectKey(IdentityMembershipFixture.PersonA)));
        var claim = await source.ClaimAsync(Guid.CreateVersion7());
        claim.Should().NotBeNull();
        await using var pool = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(pool, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var outer = await unit.BeginTransactionAsync(defect == "read-only" ? TransactionMode.ReadOnly : TransactionMode.ReadWrite);
        await using var inner = defect == "nested" ? await unit.BeginTransactionAsync() : null;
        ITenantContext context = defect == "counterfeit" ? new CounterfeitRecipient(claim!) : claim!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.SetTenantContextAsync(context));
        unit.IsRollbackOnly.Should().BeTrue();
        unit.GetTenantContextAnnouncement(unit.Transaction).Should().BeNull();
    }

    private sealed class CounterfeitRecipient(ProviderEventRecipient claim) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => claim.TenantId;
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public TenantContextOrigin? Origin => TenantContextOrigin.ProviderEventRecipient;
        public string? CorrelationId => null;
        public string? ModuleName => "identity";
    }

    [Fact]
    public async Task Two_actual_waiting_operations_fill_the_source_without_an_admission_queue()
    {
        await using var source = await QualifiedSourceAsync();
        await using var blocker = await OpenAsync(fixture.Postgres.AppConnectionString);
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(public.fn_identity_contextual_lock_key('user',@user))", blocker, transaction))
        {
            command.Parameters.AddWithValue("user", IdentityMembershipFixture.PersonA);
            await command.ExecuteNonQueryAsync();
        }
        var key = IdentitySubjectKey.FromBytes(SubjectKey(IdentityMembershipFixture.PersonA));
        var first = source.AcceptAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(-1), "LOGIN", key);
        var second = source.AcceptAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(-1), "LOGIN_ERROR", key);
        try
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => source.ClaimAsync(Guid.CreateVersion7()));
            refused.Message.Should().Be("Provider intake capacity is occupied.");
            first.IsCompleted.Should().BeFalse();
            second.IsCompleted.Should().BeFalse();
        }
        finally { await transaction.RollbackAsync(); }
        (await Task.WhenAll(first, second)).Should().OnlyContain(result => result.Accepted);
        _ = await source.RetireAsync();
    }

    [Fact]
    public async Task Failed_qualification_can_retry_after_cleanup_without_retargeting()
    {
        await using var source = new ProviderEventIntakeSource(fixture.Postgres.IdentityIngressConnectionString);
        await using var business = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        await using var proof = await DatabaseQualificationProof.OpenAsync(business);
        var profile = new ProviderEventIntakeProfile(IdentityMembershipFixture.Authority.AuthorityId, "learnstack", 604800);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.QualifyAsync(profile, proof, cancelled.Token));
        source.IsQualified.Should().BeFalse();
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.QualifyAsync(
            new(profile.AuthorityId, "other", profile.RetentionSeconds), proof));
        var revisedPolicy = new ProviderEventIntakeProfile(profile.AuthorityId, profile.RealmId, 30 * 24 * 60 * 60);
        await source.QualifyAsync(revisedPolicy, proof);
        source.IsQualified.Should().BeTrue();
        (await source.ReadScanAsync()).RetentionSeconds.Should().Be(revisedPolicy.RetentionSeconds);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.QualifyAsync(
            new(profile.AuthorityId, "other", revisedPolicy.RetentionSeconds), proof));
    }

    [Fact]
    public async Task A_matching_role_and_schema_in_a_different_database_cannot_qualify()
    {
        await using var other = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await IdentityMembershipFixture.SeedSchemaAsync(other.MigrationConnectionString, other.AppConnectionString);
        var selected = new NpgsqlConnectionStringBuilder(fixture.Postgres.IdentityIngressConnectionString)
        { Database = new NpgsqlConnectionStringBuilder(other.AppConnectionString).Database };
        await using var source = new ProviderEventIntakeSource(selected.ConnectionString);
        await using var business = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        await using var proof = await DatabaseQualificationProof.OpenAsync(business);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => source.QualifyAsync(
            new(IdentityMembershipFixture.Authority.AuthorityId, "learnstack", 604800), proof));
        failure.Message.Should().Be("Provider intake does not reach the qualified business database.");
        source.IsQualified.Should().BeFalse();
        await using var correctBusiness = ApplicationDataSource.Build(other.AppConnectionString);
        await using var correctProof = await DatabaseQualificationProof.OpenAsync(correctBusiness);
        await source.QualifyAsync(new(IdentityMembershipFixture.Authority.AuthorityId, "learnstack", 604800), correctProof);
        source.IsQualified.Should().BeTrue();
    }

    [Fact]
    public async Task Qualified_machine_source_reconciles_committed_acceptance_after_recreation()
    {
        var id = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var key = IdentitySubjectKey.FromBytes(SubjectKey(IdentityMembershipFixture.PersonA));
        ProviderEventAcceptance first;
        await using (var source = await QualifiedSourceAsync())
        {
            first = await source.AcceptAsync(id, at, "LOGIN", key);
            first.Accepted.Should().BeTrue();
            first.TargetCount.Should().Be(1);
        }
        await using var restarted = await QualifiedSourceAsync();
        (await restarted.AcceptAsync(id, at, "LOGIN", key)).Should().Be(first);
    }

    [Fact]
    public async Task A_committed_fact_survives_loss_of_the_callers_final_database_response()
    {
        var id = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var connection = await MachineAsync())
        {
            // Terminate only this fixture-owned connector after the real COMMIT.
            // The overall database call fails; no fabricated rollback proves its outcome.
            await using var command = new NpgsqlCommand("""
                BEGIN;
                SELECT * FROM public.fn_identity_provider_event_accept(@authority,'learnstack',@event,@at,'LOGIN',@subject);
                COMMIT;
                SELECT pg_terminate_backend(pg_backend_pid());
                """, connection);
            command.Parameters.AddWithValue("authority", IdentityMembershipFixture.Authority.AuthorityId.Value);
            command.Parameters.AddWithValue("event", id);
            command.Parameters.AddWithValue("at", at);
            command.Parameters.AddWithValue("subject", SubjectKey(IdentityMembershipFixture.PersonA));
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => command.ExecuteNonQueryAsync());
        }
        await using var source = await QualifiedSourceAsync();
        var recovered = await source.AcceptAsync(id, at, "LOGIN", IdentitySubjectKey.FromBytes(SubjectKey(IdentityMembershipFixture.PersonA)));
        recovered.Accepted.Should().BeTrue();
        recovered.TargetCount.Should().Be(1);
        (await source.AcceptAsync(id, at, "LOGIN", IdentitySubjectKey.FromBytes(SubjectKey(IdentityMembershipFixture.PersonA))))
            .Should().Be(recovered);
        await using var app = await OpenAsync(fixture.Postgres.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SchemaFixture.TenantA);
        (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction,
            "SELECT count(*) FROM public.identity_provider_event_targets WHERE provider_event_id=@id", ("id", id))).Should().Be(1);
    }

    [Fact]
    public async Task Actual_claim_announces_once_and_cannot_be_reused_after_rollback()
    {
        await using var source = await QualifiedSourceAsync();
        await source.AcceptAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(-1), "LOGIN",
            IdentitySubjectKey.FromBytes(SubjectKey(IdentityMembershipFixture.PersonA)));
        var claim = await source.ClaimAsync(Guid.CreateVersion7());
        claim.Should().NotBeNull();
        await using var pool = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        await using (var unit = new NpgsqlUnitOfWork(pool, NullLogger<NpgsqlUnitOfWork>.Instance))
        {
            await using var frame = await unit.BeginTransactionAsync();
            await unit.SetTenantContextAsync(claim!);
            claim!.Matches(unit).Should().BeTrue();
            unit.GetTenantContextAnnouncement(unit.Transaction)!.TenantId.Should().Be(claim.TenantId);
            await frame.FailAsync();
            claim.Matches(unit).Should().BeFalse();
        }
        await using var second = new NpgsqlUnitOfWork(pool, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var secondFrame = await second.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.SetTenantContextAsync(claim!));
        second.IsRollbackOnly.Should().BeTrue();
    }

    [Fact]
    public async Task Closing_source_revokes_an_unconsumed_claim_and_refuses_new_io()
    {
        var source = await QualifiedSourceAsync();
        await source.AcceptAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(-1), "LOGIN",
            IdentitySubjectKey.FromBytes(SubjectKey(IdentityMembershipFixture.PersonA)));
        var claim = await source.ClaimAsync(Guid.CreateVersion7());
        claim.Should().NotBeNull();
        await source.DisposeAsync();
        claim!.IsSourceActive.Should().BeFalse();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.ClaimAsync(Guid.CreateVersion7()));
        await using var pool = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(pool, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.SetTenantContextAsync(claim));
        unit.IsRollbackOnly.Should().BeTrue();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task Source_loss_after_announcement_poisoned_the_actual_unit_before_commit()
    {
        var source = await QualifiedSourceAsync();
        await source.AcceptAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(-1), "LOGIN",
            IdentitySubjectKey.FromBytes(SubjectKey(IdentityMembershipFixture.PersonA)));
        var claim = await source.ClaimAsync(Guid.CreateVersion7());
        claim.Should().NotBeNull();
        await using var pool = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(pool, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(claim!);
        await source.DisposeAsync();
        unit.IsRollbackOnly.Should().BeTrue();
        await Assert.ThrowsAsync<InvalidOperationException>(() => frame.CompleteAsync());
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task Database_lease_expiry_refuses_an_otherwise_unchanged_claim_before_actual_commit()
    {
        await using var source = await QualifiedSourceAsync();
        await source.AcceptAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(-1), "LOGIN",
            IdentitySubjectKey.FromBytes(SubjectKey(IdentityMembershipFixture.PersonA)));
        var claim = await source.ClaimAsync(Guid.CreateVersion7());
        claim.Should().NotBeNull();
        await using var pool = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(pool, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(claim!);
        var remaining = claim!.ClaimUntil - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150);
        await StageStorageProductionAsync(unit, claim);
        remaining.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThan(TimeSpan.FromSeconds(31));
        await Task.Delay(remaining);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => frame.CompleteAsync());
        failure.Message.Should().Be("Provider target production is incomplete or its claim expired before commit.");
        unit.IsRollbackOnly.Should().BeTrue();
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task Machine_source_refuses_attribute_escalation_before_any_fact_call()
    {
        await using var source = await QualifiedSourceAsync();
        await fixture.Postgres.ExecuteAsSuperuserAsync("ALTER ROLE learnstack_identity_ingress BYPASSRLS");
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => source.ClaimAsync(Guid.CreateVersion7()));
            failure.Message.Should().Be("Provider intake role attributes, ownership or grants are invalid.");
        }
        finally
        {
            var restored = await fixture.Postgres.RunRolesScriptAgainAsync();
            restored.ExitCode.Should().Be(0);
        }
        _ = await source.RetireAsync();
    }

    [Theory]
    [InlineData("SELECT ON public.membership_profiles")]
    [InlineData("UPDATE (activation_lower) ON public.identity_provider_event_scan")]
    public async Task Machine_source_refuses_extra_business_or_source_retargeting_grants(string grant)
    {
        await using var source = await QualifiedSourceAsync();
        await using var owner = await OpenAsync(fixture.Postgres.MigrationConnectionString);
        await using (var widen = new NpgsqlCommand($"GRANT {grant} TO learnstack_identity_ingress", owner))
            await widen.ExecuteNonQueryAsync();
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => source.ClaimAsync(Guid.CreateVersion7()));
            failure.Message.Should().Be("Provider intake role attributes, ownership or grants are invalid.");
        }
        finally
        {
            await using var restore = new NpgsqlCommand($"REVOKE {grant} FROM learnstack_identity_ingress", owner);
            await restore.ExecuteNonQueryAsync();
        }
        _ = await source.RetireAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Machine_source_configuration_never_echoes_supplied_values(bool withCredential)
    {
        var configuration = withCredential ? new NpgsqlConnectionStringBuilder
        { Host = "unused", Username = "learnstack_app", Password = "private-connection-canary" }.ConnectionString
            : "not a connection string";
        var failure = Assert.Throws<InvalidOperationException>(() => new ProviderEventIntakeSource(configuration));
        failure.ToString().Should().NotContain(configuration).And.NotContain("private-connection-canary");
    }

    private async Task<ProviderEventIntakeSource> QualifiedSourceAsync()
    {
        var source = new ProviderEventIntakeSource(fixture.Postgres.IdentityIngressConnectionString);
        try
        {
            await using var business = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
            await using var proof = await DatabaseQualificationProof.OpenAsync(business);
            await source.QualifyAsync(new(IdentityMembershipFixture.Authority.AuthorityId, "learnstack", 604800), proof);
            source.IsQualified.Should().BeTrue();
            return source;
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
    }
}
