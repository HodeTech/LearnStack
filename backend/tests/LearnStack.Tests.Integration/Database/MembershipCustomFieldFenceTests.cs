using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;
using static LearnStack.Tests.Integration.Database.MembershipCustomFieldFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class MembershipCustomFieldFenceTests(MembershipCustomFieldFixture fixture)
    : IClassFixture<MembershipCustomFieldFixture>
{
    [Fact]
    public async Task Value_writer_winning_shared_fence_commits_the_pin_before_actual_retirement_can_commit()
    {
        var member = await fixture.Membership.AddMemberAsync();
        var definition = await fixture.PublishAsync(Input("writer-wins-" + Guid.NewGuid().ToString("N")));
        await using var writer = await fixture.CreateAsync("value-fence-writer-wins");
        await using var retire = await fixture.CreateAsync("value-fence-retire-loses");
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(blocker);
        await using var held = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, held, SchemaFixture.TenantA);
        await LockAsync(blocker, held, SchemaFixture.TenantA, null, false);
        Assert.Equal(member.Membership, await ScalarAsync<Guid>(blocker, held,
            "SELECT id FROM memberships WHERE id=@id FOR NO KEY UPDATE", ("id", member.Membership)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var write = writer.Sender.Send(new SetMembershipCustomFieldsCommand(member.Membership, 0,
            new([Set(definition)], [])), deadline.Token);
        Task? retirement = null;
        try
        {
            await fixture.Membership.ObserveWaitAsync(writer.ApplicationName, blocker.ProcessID, write, deadline.Token);
            var retiring = retire.Sender.Send(new RetireCustomFieldDefinitionCommand(definition.DefinitionId, definition.Version), deadline.Token);
            retirement = retiring;
            // A dirty control: the real retirement command is waiting on a live
            // shared owner, rather than a scheduling delay being called a fence.
            await fixture.Membership.ObserveWaitAsync(retire.ApplicationName, blocker.ProcessID, retirement, deadline.Token);
            Assert.False(write.IsCompleted);
            Assert.False(retirement.IsCompleted);
            await held.CommitAsync();
            Assert.True((await write).IsSuccess);
            Assert.True((await retiring).IsSuccess);
            var read = await fixture.SendAsync(new GetMembershipCustomFieldsQuery(member.Membership), "value-fence-retained-read");
            Assert.True(read.IsSuccess, read.Error?.Code);
            var pinned = Assert.Single(read.Value.Fields, field => field.DefinitionId == definition.DefinitionId);
            Assert.Equal("Retired", pinned.State);
            Assert.True(pinned.Envelope!.Value.GetProperty("value").GetBoolean());
        }
        finally { await StopAsync(held, deadline, write, retirement); }
    }

    [Fact]
    public async Task Actual_retirement_winning_exclusive_fence_forces_waiting_writer_to_refuse_a_new_pin()
    {
        var member = await fixture.Membership.AddMemberAsync();
        var definition = await fixture.PublishAsync(Input("retire-wins-" + Guid.NewGuid().ToString("N")));
        await using var writer = await fixture.CreateAsync("value-fence-writer-loses");
        await using var retire = await fixture.CreateAsync("value-fence-retire-wins");
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(blocker);
        await using var held = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, held, SchemaFixture.TenantA);
        // Only a row-read lock: the actual retirement command itself must take
        // and retain the exclusive advisory fence before reaching this wait.
        Assert.Equal(definition.DefinitionId, await ScalarAsync<Guid>(blocker, held,
            "SELECT id FROM tenant_custom_field_defs WHERE id=@id FOR NO KEY UPDATE", ("id", definition.DefinitionId)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var retirement = retire.Sender.Send(new RetireCustomFieldDefinitionCommand(definition.DefinitionId, definition.Version), deadline.Token);
        Task? write = null;
        try
        {
            await fixture.Membership.ObserveWaitAsync(retire.ApplicationName, blocker.ProcessID, retirement, deadline.Token);
            var retirePid = await ScalarAsync<int>(blocker, held,
                "SELECT pid FROM pg_stat_activity WHERE application_name=@application", ("application", retire.ApplicationName));
            var writing = writer.Sender.Send(new SetMembershipCustomFieldsCommand(member.Membership, 0,
                new([Set(definition)], [])), deadline.Token);
            write = writing;
            await fixture.Membership.ObserveWaitAsync(writer.ApplicationName, retirePid, writing, deadline.Token);
            Assert.False(retirement.IsCompleted);
            Assert.False(write.IsCompleted);
            await held.CommitAsync();
            Assert.True((await retirement).IsSuccess);
            Assert.Equal("business_rule_violation", (await writing).Error!.Code);
            Assert.Equal(0, (await fixture.Membership.ReadAsync(member.Membership)).Version);
            Assert.Empty(MembershipCustomFieldValues.Read(await fixture.StoredAsync(member.Membership)));
        }
        finally { await StopAsync(held, deadline, retirement, write); }
    }

    [Fact]
    public async Task Value_writer_winning_shared_fence_commits_original_grant_pin_before_raw_withdrawal_and_then_withholds_it()
    {
        var member = await fixture.Membership.AddMemberAsync();
        var definition = await fixture.PublishAsync(Input("consent-writer-wins-" + Guid.NewGuid().ToString("N"),
            category: PersonalDataCategory.Sensitive));
        var grant = await GrantAsync(definition, member.Actor);
        await using var writer = await fixture.CreateAsync("value-consent-writer-wins");
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.AppConnectionString);
        await using var withdrawal = (NpgsqlConnection)await PostgresFixture.OpenAsync(new NpgsqlConnectionStringBuilder(
            fixture.Membership.AppConnectionString)
        { ApplicationName = "value-consent-withdrawal-loses" }.ConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(blocker);
        await SchemaQueries.AssertApplicationRoleAsync(withdrawal);
        await using var held = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, held, SchemaFixture.TenantA);
        await LockAsync(blocker, held, SchemaFixture.TenantA, null, false);
        Assert.Equal(member.Membership, await ScalarAsync<Guid>(blocker, held,
            "SELECT id FROM memberships WHERE id=@id FOR NO KEY UPDATE", ("id", member.Membership)));
        await using var withdrawalFrame = await withdrawal.BeginTransactionAsync();
        await ScopeAsync(withdrawal, withdrawalFrame, SchemaFixture.TenantA);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var write = writer.Sender.Send(new SetMembershipCustomFieldsCommand(member.Membership, 0,
            new([Set(definition, evidence: grant)], [])), deadline.Token);
        Task? withdrawing = null;
        try
        {
            await fixture.Membership.ObserveWaitAsync(writer.ApplicationName, blocker.ProcessID, write, deadline.Token);
            var writerPid = await ScalarAsync<int>(blocker, held,
                "SELECT pid FROM pg_stat_activity WHERE application_name=@application", ("application", writer.ApplicationName));
            // Raw evidence setup proves the value/consent fence ordering only;
            // the separate consent suite owns the withdrawal command boundary.
            withdrawing = WithdrawAsync();
            await fixture.Membership.ObserveWaitAsync("value-consent-withdrawal-loses", writerPid, withdrawing, deadline.Token);
            Assert.False(write.IsCompleted);
            Assert.False(withdrawing.IsCompleted);
            await held.CommitAsync();
            Assert.True((await write).IsSuccess);
            await withdrawing;
            var read = await fixture.SendAsync(new GetMembershipCustomFieldsQuery(member.Membership), "value-consent-winner-withheld");
            Assert.True(read.IsSuccess, read.Error?.Code);
            var field = Assert.Single(read.Value.Fields, value => value.DefinitionId == definition.DefinitionId);
            Assert.Equal("Withheld", field.State);
            Assert.Null(field.Envelope);
            var retained = Assert.Single(MembershipCustomFieldValues.Read(await fixture.StoredAsync(member.Membership)));
            Assert.Equal(grant, retained.ConsentEvidenceId);
            Assert.True(retained.Envelope.GetProperty("value").GetBoolean());
            Assert.Equal(1, (await fixture.Membership.ReadAsync(member.Membership)).Version);
        }
        finally { await StopAsync(held, deadline, write, withdrawing); }

        async Task WithdrawAsync()
        {
            await ExclusiveFenceAsync(withdrawal, withdrawalFrame, deadline.Token);
            await AppendWithdrawalAsync(withdrawal, withdrawalFrame, member.Membership, member.Actor, definition, grant);
            await withdrawalFrame.CommitAsync(deadline.Token);
        }
    }

    [Fact]
    public async Task Raw_withdrawal_winning_exclusive_fence_makes_waiting_real_value_command_refuse_without_membership_change()
    {
        var member = await fixture.Membership.AddMemberAsync();
        var definition = await fixture.PublishAsync(Input("consent-withdraw-wins-" + Guid.NewGuid().ToString("N"),
            category: PersonalDataCategory.Sensitive));
        var grant = await GrantAsync(definition, member.Actor);
        await using var writer = await fixture.CreateAsync("value-consent-writer-loses");
        await using var withdrawal = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(withdrawal);
        await using var held = await withdrawal.BeginTransactionAsync();
        await ScopeAsync(withdrawal, held, SchemaFixture.TenantA);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ExclusiveFenceAsync(withdrawal, held, deadline.Token);
        await AppendWithdrawalAsync(withdrawal, held, member.Membership, member.Actor, definition, grant);
        var write = writer.Sender.Send(new SetMembershipCustomFieldsCommand(member.Membership, 0,
            new([Set(definition, evidence: grant)], [])), deadline.Token);
        try
        {
            await fixture.Membership.ObserveWaitAsync(writer.ApplicationName, withdrawal.ProcessID, write, deadline.Token);
            Assert.False(write.IsCompleted);
            await held.CommitAsync(deadline.Token);
            Assert.Equal("business_rule_violation", (await write).Error!.Code);
            Assert.Equal(0, (await fixture.Membership.ReadAsync(member.Membership)).Version);
            Assert.Empty(MembershipCustomFieldValues.Read(await fixture.StoredAsync(member.Membership)));
            await using var observed = await withdrawal.BeginTransactionAsync();
            await ScopeAsync(withdrawal, observed, SchemaFixture.TenantA);
            Assert.Equal(1L, await ScalarAsync<long>(withdrawal, observed,
                "SELECT count(*) FROM membership_field_consents WHERE id=@id AND state='Granted'", ("id", grant)));
            Assert.Equal(1L, await ScalarAsync<long>(withdrawal, observed,
                "SELECT count(*) FROM membership_field_consents WHERE predecessor_id=@id AND state='Withdrawn' AND sequence=2", ("id", grant)));
        }
        finally { await StopAsync(held, deadline, write); }
    }

    private async Task<Guid> GrantAsync(CustomFieldDefinitionDto definition, Guid actor)
    {
        var granted = await fixture.SendAsync(new GrantOwnMembershipFieldConsentCommand(definition.DefinitionId,
            definition.SchemaVersion, "en", MembershipFieldConsentNotice.ComputeHash(definition.Notices!["en"]), 0, null, true),
            "value-consent-fence-grant", actor);
        Assert.True(granted.IsSuccess, granted.Error?.Code);
        return granted.Value.EvidenceId;
    }

    private static async Task ExclusiveFenceAsync(NpgsqlConnection app, NpgsqlTransaction frame, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", app, frame) { CommandTimeout = 15 };
        command.Parameters.AddWithValue("key", IdentityAdvisoryLockKey.ForTenant(TenantId.From(SchemaFixture.TenantA)));
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task StopAsync(NpgsqlTransaction held, CancellationTokenSource deadline, params Task?[] running)
    {
        await held.DisposeAsync();
        if (running.Any(task => task is { IsCompleted: false })) deadline.Cancel();
        foreach (var task in running.Where(task => task is not null))
            try { await task!; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
    }
}
