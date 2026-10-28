using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Caching;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Modules.Identity.Infrastructure.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Production discovery witnesses and the actual common READ ONLY owner on a one-connector pool.</summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class IdentitySelfDiscoveryOwnerTests(PublicReadFixture parent) : IAsyncLifetime
{
    private IdentityPublicReadFixture _database = null!;
    private ServiceProvider _provider = null!;
    private NpgsqlDataSource _source = null!;
    private ExactEntryAdmission _admission = null!;
    private GlobalIdentitySubject _subject = null!;
    private static UserId Actor => SeedData.IdentityUsers[1].UserId;

    public async Task InitializeAsync()
    {
        _database = await IdentityPublicReadFixture.CreateAsync(parent, "https://self-discovery.invalid/realms/learnstack");
        await _database.SeedHttpFoundationAsync();
        await _database.AddMembershipAsync(SeedData.Yoga, Actor, SeedData.Yoga.DefaultOrganization.OrganizationId);
        _subject = new(_database.Registration.AuthorityId, _database.Registration.Issuer,
            IdentityPublicReadFixture.FoundationSubject(Actor));
        _admission = new(_subject);
        var parsed = new NpgsqlConnectionStringBuilder(_database.AppConnectionString)
        {
            Pooling = true,
            MaxPoolSize = 1,
            NoResetOnClose = true,
        };
        _source = ApplicationDataSource.Build(parsed.ConnectionString);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IGuidFactory, SystemGuidFactory>();
        services.AddSingleton<ICacheService, InMemoryCacheService>();
        services.AddIdentityOperationPolicies();
        services.AddLearnStackMediatRPipeline();
        services.AddSingleton<ITenantContextAccessor>(StaticTenantContextAccessor.Unresolved);
        services.AddTransient<ITenantContext>(provider =>
            provider.GetRequiredService<ITenantContextAccessor>().Current ?? UnresolvedTenantContext.Instance);
        services.AddSingleton(new Lazy<NpgsqlDataSource>(() => _source));
        services.AddSingleton<IIdentitySelfEntryAdmission>(_admission);
        services.AddLearnStackPersistence(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = parsed.ConnectionString,
            ["Identity:AuthorityId"] = _database.Registration.AuthorityId.Value.ToString("D"),
            ["Authentication:Tenant:Issuer"] = _database.Registration.Issuer,
        }).Build());
        _provider = services.BuildServiceProvider(validateScopes: true);
        await _provider.GetRequiredService<IdentityAuthorityQualification>().QualifyAsync();
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        if (_source is not null) await _source.DisposeAsync();
        if (_database is not null) await _database.DisposeAsync();
    }

    [Fact]
    public async Task Captured_scope_uses_app_role_READ_ONLY_and_clears_every_other_announcement()
    {
        using var page = await CaptureAsync();
        var witness = page.Scopes[0];
        await using var scope = _provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var self = (IPreContextSelfDiscoveryUnitOfWork)unit;
        await using var frame = await self.BeginSelfDiscoveryTransactionAsync(witness);
        self.IsSelfDiscoveryOwner(witness).Should().BeTrue();
        self.IsSelfDiscoveryOwner(page.Scopes[1]).Should().BeFalse("another captured tuple is not this physical owner");
        ((IOperationReadScopeView)unit).IsTenantWideRead.Should().BeFalse();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current.Should().BeNull();
        (await ScalarAsync(unit, """
            SELECT current_user='learnstack_app' AND current_setting('transaction_read_only')='on'
                AND current_setting('transaction_isolation')='read committed'
                AND NULLIF(current_setting('app.tenant_id',true),'')=@tenant
                AND NULLIF(current_setting('app.organization_id',true),'') IS NOT DISTINCT FROM @organization
                AND NULLIF(current_setting('app.scope',true),'') IS NULL
                AND NULLIF(current_setting('app.resolving_host',true),'') IS NULL
                AND NULLIF(current_setting('app.identity_authority_id',true),'') IS NULL
                AND NULLIF(current_setting('app.identity_subject_key',true),'') IS NULL
            """, witness)).Should().Be(true);
        var update = async () => await ScalarAsync(unit, "UPDATE public.tenants SET display_name=display_name RETURNING id");
        (await update.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ReadOnlySqlTransaction);
        await frame.FailAsync();
    }

    [Fact]
    public async Task Captured_tuple_is_single_use_even_after_the_first_owner_completed()
    {
        using var page = await CaptureAsync();
        var witness = page.Scopes[0];
        await using (var firstScope = _provider.CreateAsyncScope())
        {
            var first = (IPreContextSelfDiscoveryUnitOfWork)firstScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var frame = await first.BeginSelfDiscoveryTransactionAsync(witness);
            await frame.CompleteAsync();
        }
        await using var nextScope = _provider.CreateAsyncScope();
        var next = (IPreContextSelfDiscoveryUnitOfWork)nextScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await FluentActions.Awaiting(() => next.BeginSelfDiscoveryTransactionAsync(witness))
            .Should().ThrowAsync<InvalidOperationException>();
        next.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task Actual_label_reader_completes_each_captured_scope_before_taking_the_next_owner()
    {
        using var page = await CaptureAsync();
        await using var scope = _provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        var reader = services.GetRequiredService<IOwnMembershipScopeLabels>();
        var english = await reader.ReadAsync(page.Scopes[0]);
        english.Should().NotBeNull();
        english!.TenantName.Should().Be(SeedData.English.DisplayName);
        english.EntryHost.Should().Be(SeedData.English.Host);
        unit.HasActiveTransaction.Should().BeFalse();
        unit.IsRollbackOnly.Should().BeFalse();
        var yoga = await reader.ReadAsync(page.Scopes[1]);
        yoga.Should().NotBeNull();
        yoga!.TenantName.Should().Be(SeedData.Yoga.DisplayName);
        yoga.OrganizationName.Should().Be(SeedData.Yoga.DefaultOrganization.DisplayName);
        unit.HasActiveTransaction.Should().BeFalse();
        services.GetRequiredService<ITenantContextAccessor>().Current.Should().BeNull();
        await using var available = await _source.OpenConnectionAsync();
        await using var cleared = new NpgsqlCommand("SELECT NULLIF(current_setting('app.tenant_id',true),'') IS NULL", available);
        (await cleared.ExecuteScalarAsync()).Should().Be(true,
            "returning labels must already have closed the actual max-one pooled connector");
    }

    [Fact]
    public async Task Label_owner_cannot_be_joined_by_ordinary_global_or_another_label_operation()
    {
        using var page = await CaptureAsync();
        await using var scope = _provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unit = services.GetRequiredService<IUnitOfWork>();
        var self = (IPreContextSelfDiscoveryUnitOfWork)unit;
        await using var frame = await self.BeginSelfDiscoveryTransactionAsync(page.Scopes[0]);
        await FluentActions.Awaiting(() => unit.BeginTransactionAsync(TransactionMode.ReadOnly))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => self.BeginSelfDiscoveryTransactionAsync(page.Scopes[1]))
            .Should().ThrowAsync<InvalidOperationException>();
        (await services.GetRequiredService<IIdentityBindingReader>().ResolveExistingAsync(_subject))
            .Outcome.Should().Be(IdentityBindingOutcome.AdmissionDenied);
        await FluentActions.Awaiting(() => unit.SetTenantContextAsync(new SeedTenantContext(SeedData.Yoga.TenantId, null)))
            .Should().ThrowAsync<InvalidOperationException>();
        await frame.FailAsync();
    }

    [Fact]
    public async Task Label_capture_does_not_join_an_already_open_ordinary_owner_or_consume_its_witness()
    {
        using var page = await CaptureAsync();
        await using var scope = _provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var self = (IPreContextSelfDiscoveryUnitOfWork)unit;
        await using (var ordinary = await unit.BeginTransactionAsync())
        {
            await FluentActions.Awaiting(() => self.BeginSelfDiscoveryTransactionAsync(page.Scopes[0]))
                .Should().ThrowAsync<InvalidOperationException>();
            await ordinary.CompleteAsync();
        }
        await using var captured = await self.BeginSelfDiscoveryTransactionAsync(page.Scopes[0]);
        self.IsSelfDiscoveryOwner(page.Scopes[0]).Should().BeTrue("the refused nested call did not consume another owner's witness");
        await captured.CompleteAsync();
    }

    [Fact]
    public async Task Disposed_page_cannot_start_a_label_owner()
    {
        var page = await CaptureAsync();
        var witness = page.Scopes[0];
        page.Dispose();
        await using var scope = _provider.CreateAsyncScope();
        var self = (IPreContextSelfDiscoveryUnitOfWork)scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await FluentActions.Awaiting(() => self.BeginSelfDiscoveryTransactionAsync(witness))
            .Should().ThrowAsync<InvalidOperationException>();
        self.HasActiveTransaction.Should().BeFalse();
        self.IsSelfDiscoveryOwner(witness).Should().BeFalse();
    }

    [Fact]
    public async Task Withdrawing_source_provenance_poisoned_the_live_owner_before_any_further_SQL()
    {
        using var page = await CaptureAsync();
        await using var scope = _provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var self = (IPreContextSelfDiscoveryUnitOfWork)unit;
        await using var frame = await self.BeginSelfDiscoveryTransactionAsync(page.Scopes[0]);
        _admission.Active = false;
        var connection = () => unit.Connection;
        connection.Should().Throw<InvalidOperationException>();
        unit.IsRollbackOnly.Should().BeTrue();
        self.IsSelfDiscoveryOwner(page.Scopes[0]).Should().BeFalse();
        await FluentActions.Awaiting(() => frame.CompleteAsync()).Should().ThrowAsync<InvalidOperationException>();
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_or_rolled_back_label_owner_returns_the_same_unreset_connector_without_authority(bool complete)
    {
        using var page = await CaptureAsync();
        int backend;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var self = (IPreContextSelfDiscoveryUnitOfWork)unit;
            await using var frame = await self.BeginSelfDiscoveryTransactionAsync(page.Scopes[0]);
            backend = ((NpgsqlConnection)unit.Connection).ProcessID;
            (await ScalarAsync(unit, "SELECT current_setting('app.tenant_id')")).Should().Be(page.Scopes[0].TenantId.Value.ToString("D"));
            if (complete) await frame.CompleteAsync();
            else await frame.FailAsync();
        }
        await using var reused = await _source.OpenConnectionAsync();
        reused.ProcessID.Should().Be(backend, "a max-one pool and NoResetOnClose prove actual owner cleanup");
        await using var reset = new NpgsqlCommand("""
            SELECT NULLIF(current_setting('app.tenant_id',true),'') IS NULL
                AND NULLIF(current_setting('app.organization_id',true),'') IS NULL
                AND NULLIF(current_setting('app.scope',true),'') IS NULL
                AND NULLIF(current_setting('app.resolving_host',true),'') IS NULL
                AND NULLIF(current_setting('app.identity_authority_id',true),'') IS NULL
                AND NULLIF(current_setting('app.identity_subject_key',true),'') IS NULL
                AND current_setting('transaction_read_only')='off'
            """, reused);
        (await reset.ExecuteScalarAsync()).Should().Be(true);
    }

    [Fact]
    public async Task Cancelled_label_checkout_settles_before_a_new_owner_can_reuse_the_only_connector()
    {
        using var page = await CaptureAsync();
        await using var blocked = await _source.OpenConnectionAsync();
        var backend = blocked.ProcessID;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            using var cancellation = new CancellationTokenSource();
            var read = scope.ServiceProvider.GetRequiredService<IOwnMembershipScopeLabels>()
                .ReadAsync(page.Scopes[0], cancellation.Token);
            read.IsCompleted.Should().BeFalse("the only physical connector is held by the test control");
            await cancellation.CancelAsync();
            await FluentActions.Awaiting(() => read).Should().ThrowAsync<OperationCanceledException>();
            unit.HasActiveTransaction.Should().BeFalse();
            unit.IsRollbackOnly.Should().BeTrue();
        }
        await blocked.CloseAsync();
        await using var reused = await _source.OpenConnectionAsync();
        reused.ProcessID.Should().Be(backend);
        await using var query = new NpgsqlCommand("SELECT NULLIF(current_setting('app.tenant_id',true),'') IS NULL", reused);
        (await query.ExecuteScalarAsync()).Should().Be(true);
    }

    [Theory]
    [InlineData("Active", true)]
    [InlineData("Suspended", false)]
    [InlineData("Revoked", false)]
    public async Task Discovery_returns_only_current_Active_nondeleted_memberships(string status, bool visible)
    {
        Guid branch;
        using (var initial = await CaptureAsync()) branch = initial.Scopes[1].MembershipId;
        if (status != "Active")
            await MutateBranchAsync(branch, """
                UPDATE public.memberships SET status=@status,row_version=row_version+1,
                    deleted_at=CASE WHEN @status='Revoked' THEN clock_timestamp() ELSE NULL END,
                    deleted_by=CASE WHEN @status='Revoked' THEN @actor ELSE NULL END
                WHERE id=@member;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("status", status));
        await using var scope = _provider.CreateAsyncScope();
        using var page = await scope.ServiceProvider.GetRequiredService<IIdentitySelfDiscovery>()
            .ReadScopesAsync(_subject, null, null, 100);
        page.Binding.Outcome.Should().Be(IdentityBindingOutcome.Existing);
        page.Scopes.Select(value => value.MembershipId).Should().Contain(SeedData.IdentityMemberships[1].MembershipId);
        page.Scopes.Any(value => value.MembershipId == branch).Should().Be(visible);
        page.Scopes.Should().HaveCount(visible ? 2 : 1);
        page.Scopes.Should().OnlyContain(value => value.Actor == Actor);
    }

    [Fact]
    public async Task An_Active_soft_deleted_fixture_is_impossible_under_the_actual_lifecycle_guard()
    {
        Guid branch;
        using (var initial = await CaptureAsync()) branch = initial.Scopes[1].MembershipId;
        var malformed = () => MutateBranchAsync(branch, """
            UPDATE public.memberships SET deleted_at=clock_timestamp(),deleted_by=@actor WHERE id=@member;
            SET CONSTRAINTS ALL IMMEDIATE;
            """);
        (await malformed.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        using var unchanged = await CaptureAsync();
        unchanged.Scopes.Select(value => value.MembershipId).Should().Contain(branch,
            "the failed malformed control rolls back without disabling any lifecycle guard");
    }

    [Fact]
    public async Task Mismatched_claimed_actor_cannot_retarget_a_verified_subjects_discovery()
    {
        await using var scope = _provider.CreateAsyncScope();
        // The transport stub deliberately permits this claim so the actual
        // binding comparison, rather than the test admission, must reject it.
        _admission.AcceptClaimedActor = true;
        using var page = await scope.ServiceProvider.GetRequiredService<IIdentitySelfDiscovery>()
            .ReadScopesAsync(_subject, SeedData.IdentityUsers[0].UserId, null, 100);
        page.Binding.Outcome.Should().Be(IdentityBindingOutcome.Conflict);
        page.Scopes.Should().BeEmpty();
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>().HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task Foreign_membership_cursor_is_only_an_order_boundary_and_never_an_actor_selector()
    {
        await using var scope = _provider.CreateAsyncScope();
        var foreign = SeedData.IdentityMemberships[2];
        foreign.Identity.UserId.Should().NotBe(Actor);
        using var page = await scope.ServiceProvider.GetRequiredService<IIdentitySelfDiscovery>()
            .ReadScopesAsync(_subject, null, foreign.MembershipId, 100);
        page.Binding.Actor.Should().Be(Actor);
        var item = page.Scopes.Should().ContainSingle().Which;
        item.Actor.Should().Be(Actor);
        item.TenantId.Should().Be(SeedData.Yoga.TenantId);
        item.OrganizationId.Should().Be(SeedData.Yoga.DefaultOrganization.OrganizationId);
        item.MembershipId.Should().NotBe(foreign.MembershipId);
    }

    [Fact]
    public async Task Deployed_discovery_query_can_use_the_Active_user_and_ordering_partial_index()
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
        await using var read = await owner.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        await IdentityMembershipFixture.ExecuteAsync(owner, read, """
            SET TRANSACTION READ ONLY;
            SELECT set_config('app.identity_authority_id',@authority,true),
                set_config('app.identity_subject_key',@subject,true);
            SET LOCAL enable_seqscan=off;
            """, ("authority", _subject.AuthorityId.Value.ToString("D")), ("subject", _subject.SubjectKey.Hex));
        var body = await IdentityMembershipFixture.ScalarAsync<string>(owner, read,
            "SELECT prosrc FROM pg_catalog.pg_proc WHERE oid='public.fn_identity_self_membership_scopes(uuid,integer)'::regprocedure");
        const string Marker = "RETURN QUERY ";
        var start = body.IndexOf(Marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        start += Marker.Length;
        var end = body.IndexOf(';', start);
        end.Should().BeGreaterThan(start);
        // EXPLAIN the deployed function's SELECT under its actual owner/RLS
        // branch. A function scan cannot reveal its internal index plan. Forced
        // eligibility on this tiny fixture is not a production latency claim.
        var select = body[start..end]
            .Replace("selected_authority", "@authority", StringComparison.Ordinal)
            .Replace("selected_subject", "@subject", StringComparison.Ordinal)
            .Replace("after_membership_id", "@after", StringComparison.Ordinal)
            .Replace("page_limit", "@limit", StringComparison.Ordinal);
        await using var explain = new NpgsqlCommand("EXPLAIN (FORMAT JSON,COSTS OFF) " + select, owner, read);
        explain.Parameters.AddWithValue("authority", _subject.AuthorityId.Value.ToString("D"));
        explain.Parameters.AddWithValue("subject", _subject.SubjectKey.Hex);
        explain.Parameters.AddWithValue("after", NpgsqlTypes.NpgsqlDbType.Uuid, SeedData.IdentityMemberships[1].MembershipId);
        explain.Parameters.AddWithValue("limit", 100);
        var plan = (string)(await explain.ExecuteScalarAsync())!;
        plan.Should().Contain("ix_memberships_self_active_user_id_id");
    }

    private async Task MutateBranchAsync(Guid member, string sql, params (string Name, object? Value)[] additional)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.Yoga.TenantId.Value,
            SeedData.Yoga.DefaultOrganization.OrganizationId.Value);
        await IdentityMembershipFixture.LockAsync(app, transaction, SeedData.Yoga.TenantId.Value, null, false);
        await IdentityMembershipFixture.ExecuteAsync(app, transaction, sql,
            [("member", member), ("actor", UserId.SystemActor.Value), .. additional]);
        await transaction.CommitAsync();
    }

    private async Task<OwnMembershipScopePage> CaptureAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var page = await scope.ServiceProvider.GetRequiredService<IIdentitySelfDiscovery>()
            .ReadScopesAsync(_subject, null, null, 100);
        page.Binding.Outcome.Should().Be(IdentityBindingOutcome.Existing);
        page.Scopes.Should().HaveCount(2);
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>().HasActiveTransaction.Should().BeFalse();
        return page;
    }

    private static async Task<object?> ScalarAsync(IUnitOfWork unit, string sql, OwnMembershipScopeWitness? witness = null)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection,
            (NpgsqlTransaction?)unit.Transaction);
        if (witness is not null)
        {
            command.Parameters.AddWithValue("tenant", witness.TenantId.Value.ToString("D"));
            command.Parameters.AddWithValue("organization", NpgsqlTypes.NpgsqlDbType.Text,
                witness.OrganizationId is { } org ? org.Value.ToString("D") : DBNull.Value);
        }
        return await command.ExecuteScalarAsync();
    }

    private sealed class ExactEntryAdmission(GlobalIdentitySubject expected) : IIdentitySelfEntryAdmission
    {
        internal bool Active { get; set; } = true;
        internal bool AcceptClaimedActor { get; set; }
        public bool CreationEnabled => false;
        public bool Allows(GlobalIdentitySubject subject, IdentitySelfOperation operation, UserId? claimedActor) =>
            Active && operation == IdentitySelfOperation.Contexts && (claimedActor is null || AcceptClaimedActor)
            && subject.AuthorityId == expected.AuthorityId && subject.SubjectKey.Equals(expected.SubjectKey)
            && subject.Issuer == expected.Issuer && subject.Subject == expected.Subject;
    }
}
