using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class ProviderEventStorageTests
{
    [Theory]
    [InlineData("same")]
    [InlineData("foreign")]
    [InlineData("unresolved")]
    public async Task A_second_announcement_cannot_remove_the_provider_owner(string replacement)
    {
        await using var source = await QualifiedSourceAsync();
        var claim = await ClaimFixtureAsync(source);
        await using var pool = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(pool, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(claim);
        ITenantContext next = replacement == "unresolved" ? UnresolvedTenantContext.Instance
            : new OrdinaryReplacement(replacement == "same" ? claim.TenantId : TenantId.From(Guid.CreateVersion7()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.SetTenantContextAsync(next));
        unit.IsRollbackOnly.Should().BeTrue();
        await source.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => frame.CompleteAsync());
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task A_real_claim_without_its_production_cannot_commit_an_empty_transaction()
    {
        await using var source = await QualifiedSourceAsync();
        var claim = await ClaimFixtureAsync(source);
        await using var pool = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(pool, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(claim);
        await Assert.ThrowsAsync<InvalidOperationException>(() => frame.CompleteAsync());
        unit.IsRollbackOnly.Should().BeTrue();
    }

    [Fact]
    public async Task A_real_claim_cannot_run_an_ordinary_module_EF_command()
    {
        await using var source = await QualifiedSourceAsync();
        var claim = await ClaimFixtureAsync(source);
        await using var pool = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(pool, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(claim);
        await using var context = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(unit.Connection).AddInterceptors(new TenantContextGuardInterceptor(unit)).Options,
            new StaticTenantContextAccessor(claim), EmptyIdentityScope.Instance);
        await context.Database.UseTransactionAsync(unit.Transaction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.ExecuteSqlRawAsync(
            "UPDATE public.membership_profiles SET display_name='forbidden-provider-write'"));
        unit.IsRollbackOnly.Should().BeTrue();
        await Assert.ThrowsAsync<InvalidOperationException>(() => frame.CompleteAsync());
    }

    [Fact]
    public async Task An_unrelated_tenant_write_without_claimed_production_is_rolled_back()
    {
        await using var source = await QualifiedSourceAsync();
        var claim = await ClaimFixtureAsync(source);
        var tenant = claim.TenantId.Value;
        var actor = tenant == SchemaFixture.TenantA ? IdentityMembershipFixture.AdminA : IdentityMembershipFixture.AdminB;
        var member = tenant == SchemaFixture.TenantA ? IdentityMembershipFixture.AdminMembershipA : IdentityMembershipFixture.AdminMembershipB;
        await using var pool = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(pool, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(claim);
        var connection = (NpgsqlConnection)unit.Connection;
        var transaction = (NpgsqlTransaction)unit.Transaction!;
        var original = await IdentityMembershipFixture.ScalarAsync<string>(connection, transaction,
            "SELECT display_name FROM public.membership_profiles WHERE membership_id=@member", ("member", member));
        await IdentityMembershipFixture.LockAsync(connection, transaction, tenant, actor, exclusive: false);
        await using (var update = new NpgsqlCommand("UPDATE public.membership_profiles SET display_name='provider-escape' WHERE membership_id=@member", connection, transaction))
        {
            update.Parameters.AddWithValue("member", member);
            (await update.ExecuteNonQueryAsync()).Should().Be(1, "the complete app-role graph admits the write before the provider completion fence");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => frame.CompleteAsync());
        await using var app = await OpenAsync(fixture.Postgres.AppConnectionString);
        await using var check = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, check, tenant);
        (await IdentityMembershipFixture.ScalarAsync<string>(app, check,
            "SELECT display_name FROM public.membership_profiles WHERE membership_id=@member", ("member", member))).Should().Be(original);
    }

    [Fact]
    public async Task Exact_target_and_matching_outbox_are_both_required_for_provider_commit()
    {
        await using var source = await QualifiedSourceAsync();
        var claim = await ClaimFixtureAsync(source);
        await using var pool = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(pool, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(claim);
        await StageStorageProductionAsync(unit, claim);
        await frame.CompleteAsync();
        await using var app = await OpenAsync(fixture.Postgres.AppConnectionString);
        await using var check = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, check, claim.TenantId.Value);
        (await IdentityMembershipFixture.ScalarAsync<bool>(app, check,
            "SELECT enqueued_at IS NOT NULL FROM public.identity_provider_event_targets WHERE id=@id", ("id", claim.TargetId))).Should().BeTrue();
    }

    [Fact]
    public async Task Retained_targets_have_both_affected_User_and_subject_digest_locator_indexes()
    {
        await using var machine = await MachineAsync();
        var indexes = await SchemaQueries.ReadStringsAsync(machine, """
            SELECT indexdef FROM pg_catalog.pg_indexes WHERE schemaname='public'
                AND tablename='identity_provider_event_targets'
            """);
        indexes.Should().Contain(value => value.Contains("(affected_user_id, id)", StringComparison.Ordinal));
        indexes.Should().Contain(value => value.Contains("(subject_key, id)", StringComparison.Ordinal));
    }

    private static async Task<ProviderEventRecipient> ClaimFixtureAsync(LearnStack.Infrastructure.Identity.ProviderEvents.ProviderEventIntakeSource source)
    {
        await source.AcceptAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddMinutes(-1), "LOGIN",
            IdentitySubjectKey.FromBytes(SubjectKey(IdentityMembershipFixture.PersonA)));
        return await source.ClaimAsync(Guid.CreateVersion7()) ?? throw new InvalidOperationException("Fixture claim missing.");
    }

    private static async Task StageStorageProductionAsync(NpgsqlUnitOfWork unit, ProviderEventRecipient claim)
    {
        // Storage-only positive fixture. Actual IOutbox serialization, producer
        // admission and required consumer proof belong to Step 2's real flow.
        var payload = JsonSerializer.Serialize(new { claim.TargetId, claim.EventId, TenantId = claim.TenantId.Value });
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.outbox_messages(id,occurred_at,tenant_id,organization_id,correlation_id,
                actor_user_id,type,topic,partition_key,payload)
            VALUES(@event,clock_timestamp(),@tenant,NULL,'provider-storage-control',@actor,
                'LearnStack.Modules.Identity.Application.Contracts.IntegrationEvents.UserProviderEventObservedIntegrationEventV1, LearnStack.Modules.Identity.Application.Contracts',
                'learnstack.identity.user',@partition,@payload);
            UPDATE public.identity_provider_event_targets SET enqueued_at=clock_timestamp()
                WHERE id=@target AND claim_token=@token;
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        command.Parameters.AddWithValue("event", claim.EventId);
        command.Parameters.AddWithValue("tenant", claim.TenantId.Value);
        command.Parameters.AddWithValue("target", claim.TargetId);
        command.Parameters.AddWithValue("token", claim.ClaimToken);
        command.Parameters.AddWithValue("actor", UserId.SystemActor.Value);
        command.Parameters.AddWithValue("partition", claim.TargetId.ToString("D"));
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class OrdinaryReplacement(TenantId tenant) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => tenant;
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public string? CorrelationId => null;
        public string? ModuleName => "identity";
    }
}
