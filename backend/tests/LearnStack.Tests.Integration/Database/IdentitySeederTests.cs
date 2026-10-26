using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>The shipped external Seeder capability, app-role commands and durable separate acts.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class IdentitySeederTests(SchemaFixture schema) : IAsyncLifetime
{
    private static readonly IdentityAuthorityRegistration Registration = new(
        IdentityAuthorityId.From(Guid.Parse("01930000-0000-7000-8000-000000009001")),
        "http://localhost:8080/realms/learnstack");
    private DisposableSchemaDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
        await IdentityAuthorityDeploymentInstaller.InstallAsync(owner, Registration);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task Four_actual_provider_bindings_sixteen_templates_and_four_scoped_memberships_are_exact_and_reruns_write_nothing()
    {
        var captured = new SeedLogCapture();
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(captured));
        await using var dataSource = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedContentAsync(dataSource, logs);
        await RunAsync(dataSource, loggerFactory: logs);
        await AssertCompleteAsync();
        await AssertCapturedChildrenAsync();
        captured.Text.Should().NotBeNullOrEmpty();
        for (var index = 0; index < 4; index++) captured.Text.Should().NotContain($"provider-admin-id-{index}");
        var before = await AuditCountAsync();
        before.Should().Be(SeedData.ExpectedCombinedAuditWrites);
        await RunAsync(dataSource);
        await AssertCompleteAsync();
        (await AuditCountAsync()).Should().Be(before, "completed acts are read before any writer is dispatched");
    }

    [Theory]
    [InlineData("identity.membership.create_first_seed_admin", 0)]
    [InlineData("identity.membership_profile.set_seed_profile", 4)]
    public async Task Central_MUST_failure_rolls_back_only_the_failed_graph_and_clean_rerun_resumes(string operation, long priorMembers)
    {
        await using var dataSource = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedContentAsync(dataSource);
        if (operation is not ("identity.membership.create_first_seed_admin" or "identity.membership_profile.set_seed_profile"))
            throw new ArgumentException("Fixture operation is invalid.", nameof(operation));
        await DeploymentAsync($$"""
            CREATE FUNCTION public.proof_reject_seed_success() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.operation='{{operation}}' AND NEW.outcome='success' THEN
                    RAISE EXCEPTION 'fixed audit proof refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER proof_reject_seed_success BEFORE INSERT ON public.audit_log
            FOR EACH ROW EXECUTE FUNCTION public.proof_reject_seed_success();
            """);
        try
        {
            var act = async () => await RunAsync(dataSource);
            await act.Should().ThrowAsync<Exception>();
            (await CountContextualAsync("roles")).Should().Be(16L);
            (await CountContextualAsync("memberships")).Should().Be(priorMembers);
            (await CountContextualAsync("membership_profiles")).Should().Be(priorMembers);
            (await CountContextualAsync("membership_role_assignments")).Should().Be(priorMembers);
            await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.PlatformConnectionString);
            (await IdentityMembershipFixture.ScalarAsync<long>(platform, null,
                "SELECT count(*) FROM public.audit_log WHERE operation=@operation AND outcome='success'", ("operation", operation))).Should().Be(0L);
            (await IdentityMembershipFixture.ScalarAsync<long>(platform, null,
                "SELECT count(*) FROM public.audit_log WHERE operation='identity.user.create' AND outcome='success'")).Should().Be(4L);
            if (priorMembers == 4)
            {
                foreach (var member in SeedData.IdentityMemberships)
                {
                    await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
                    await using var transaction = await app.BeginTransactionAsync();
                    await IdentityMembershipFixture.ScopeAsync(app, transaction, member.Identity.Tenant.TenantId.Value, member.OrganizationId?.Value);
                    (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction, """
                        SELECT count(*) FROM public.memberships m JOIN public.membership_profiles p
                            ON p.tenant_id=m.tenant_id AND p.membership_id=m.id
                        WHERE m.id=@member AND m.row_version=0 AND p.display_name IS NULL AND p.preferred_locale IS NULL
                        """, ("member", member.MembershipId))).Should().Be(1L);
                }
            }
        }
        finally
        {
            await DeploymentAsync("DROP TRIGGER proof_reject_seed_success ON public.audit_log; DROP FUNCTION public.proof_reject_seed_success();");
        }
        await RunAsync(dataSource);
        await AssertCompleteAsync();
        await using var success = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.PlatformConnectionString);
        (await IdentityMembershipFixture.ScalarAsync<long>(success, null,
            "SELECT count(*) FROM public.audit_log WHERE outcome='success'")).Should().Be(SeedData.ExpectedCombinedAuditWrites);
    }

    [Theory]
    [InlineData(7, 2, 0)]
    [InlineData(55, 16, 2)]
    [InlineData(64, 16, 4)]
    public async Task Interrupted_committed_acts_resume_only_missing_declarations(int failAtContext, long expectedRoles, long expectedMembers)
    {
        await using var dataSource = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedContentAsync(dataSource);
        var act = async () => await RunAsync(dataSource, failAtContext);
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await CountContextualAsync("roles")).Should().Be(expectedRoles);
        (await CountContextualAsync("memberships")).Should().Be(expectedMembers);
        await RunAsync(dataSource);
        await AssertCompleteAsync();
        (await AuditCountAsync()).Should().Be(SeedData.ExpectedCombinedAuditWrites);
    }

    [Fact]
    public async Task Edited_or_upgraded_initial_grants_refuse_without_repair_or_additional_audit()
    {
        await using var dataSource = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedContentAsync(dataSource);
        await RunAsync(dataSource);
        var role = SeedData.IdentityRoles[0];
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, role.Tenant.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, transaction, role.Tenant.TenantId.Value, null, true);
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                DELETE FROM public.role_permission_grants WHERE tenant_id=@tenant AND role_id=@role
                    AND permission_key='identity.membership_profile.write';
                UPDATE public.roles SET row_version=row_version+1 WHERE tenant_id=@tenant AND id=@role;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("tenant", role.Tenant.TenantId.Value), ("role", role.RoleId));
            await transaction.CommitAsync();
        }
        var before = await AuditCountAsync();
        var act = async () => await RunAsync(dataSource);
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be("Development identity foundation was refused.");
        (await AuditCountAsync()).Should().Be(before);
        (await CountContextualAsync("role_permission_grants")).Should().Be(SeedData.IdentityRoles.Sum(roleDeclaration =>
            LearnStack.Modules.Identity.Domain.IdentityFoundationManifest.Version1.FindTemplate(roleDeclaration.BuiltInKey, roleDeclaration.Scope)!.Grants.Length) - 1);
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Revoked")]
    public async Task A_nonactive_membership_stops_remaining_acts_and_is_never_recreated(string status)
    {
        await using var dataSource = ApplicationDataSource.Build(_database.AppConnectionString);
        await SeedContentAsync(dataSource);
        await RunAsync(dataSource);
        var member = SeedData.IdentityMemberships[1];
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, member.Identity.Tenant.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, transaction, member.Identity.Tenant.TenantId.Value, null, false);
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                UPDATE public.memberships SET status=@status,row_version=row_version+1,
                    deleted_at=CASE WHEN @status='Revoked' THEN clock_timestamp() ELSE NULL END,
                    deleted_by=CASE WHEN @status='Revoked' THEN @actor ELSE NULL END
                WHERE tenant_id=@tenant AND id=@member;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("status", status), ("actor", UserId.SystemActor.Value),
                ("tenant", member.Identity.Tenant.TenantId.Value), ("member", member.MembershipId));
            await transaction.CommitAsync();
        }
        var before = await AuditCountAsync();
        var act = async () => await RunAsync(dataSource);
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await AuditCountAsync()).Should().Be(before);
        (await CountContextualAsync("memberships")).Should().Be(4L);
    }

    [Fact]
    public async Task A_mismatched_deployment_anchor_refuses_before_any_binding_or_contextual_mutation()
    {
        await using var dataSource = ApplicationDataSource.Build(_database.AppConnectionString);
        var other = new IdentityAuthorityRegistration(Registration.AuthorityId, "https://localhost/realms/learnstack");
        var witness = await WitnessAsync(other);
        var admission = new FixedSeedIdentityFoundationAdmission(witness);
        var runner = new SeedIdentityFoundationRunner(context => SeedComposition.Build(dataSource, context,
            NullLoggerFactory.Instance, identityAuthority: other, seedAdmission: admission), admission);
        var act = async () => await runner.RunAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await AuditCountAsync()).Should().Be(0L);
        (await CountContextualAsync("roles")).Should().Be(0L);
    }

    private static Task<int> SeedContentAsync(NpgsqlDataSource dataSource, ILoggerFactory? loggerFactory = null) =>
        new SeedRunner(context => SeedComposition.Build(dataSource, context, loggerFactory ?? NullLoggerFactory.Instance),
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SeedRunner>()).RunAsync(CancellationToken.None);

    private static async Task RunAsync(NpgsqlDataSource dataSource, int? failAtContext = null, ILoggerFactory? loggerFactory = null)
    {
        var witness = await WitnessAsync(Registration);
        var admission = new FixedSeedIdentityFoundationAdmission(witness);
        var contexts = 0;
        var runner = new SeedIdentityFoundationRunner(context =>
        {
            if (context is not null && ++contexts == failAtContext)
                throw new InvalidOperationException("Fixture interruption before dispatch.");
            return SeedComposition.Build(dataSource, context, loggerFactory ?? NullLoggerFactory.Instance,
                identityAuthority: Registration, seedAdmission: admission);
        }, admission);
        await runner.RunAsync();
        foreach (var person in SeedData.IdentityUsers)
        {
            await using var provider = SeedComposition.Build(dataSource, null, NullLoggerFactory.Instance,
                identityAuthority: Registration, seedAdmission: admission);
            await provider.GetRequiredService<IdentityAuthorityQualification>().QualifyAsync();
            await using var scope = provider.CreateAsyncScope();
            var binding = await scope.ServiceProvider.GetRequiredService<IIdentityBindingReader>()
                .ResolveExistingAsync(witness.Subjects[person.UserId]);
            binding.IsBound.Should().BeTrue();
            binding.Actor.Should().Be(person.UserId);
        }
    }

    private static async Task<SeedProviderWitness> WitnessAsync(IdentityAuthorityRegistration registration)
    {
        var source = JsonSerializer.Serialize(new
        {
            version = 1,
            issuer = registration.Issuer,
            users = SeedData.IdentityUsers.Select((person, index) => new { actorId = person.UserId.Value, opaqueSub = $"provider-admin-id-{index}" }),
        });
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
        return await SeedProviderWitness.ReadAsync(input, registration);
    }

    private async Task AssertCompleteAsync()
    {
        (await CountContextualAsync("roles")).Should().Be(16L);
        (await CountContextualAsync("memberships")).Should().Be(4L);
        (await CountContextualAsync("membership_profiles")).Should().Be(4L);
        (await CountContextualAsync("membership_role_assignments")).Should().Be(4L);
        foreach (var declared in SeedData.IdentityMemberships)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, declared.Identity.Tenant.TenantId.Value,
                declared.OrganizationId?.Value);
            (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction, """
                SELECT count(*) FROM public.memberships m JOIN public.membership_profiles p
                    ON p.tenant_id=m.tenant_id AND p.membership_id=m.id
                JOIN public.membership_role_assignments a ON a.tenant_id=m.tenant_id AND a.membership_id=m.id
                WHERE m.id=@member AND m.user_id=@person AND m.organization_id IS NOT DISTINCT FROM @organization
                    AND m.status='Active' AND m.deleted_at IS NULL AND m.row_version=1
                    AND p.display_name=@display AND p.preferred_locale=@locale AND a.role_id=@role
                """, ("member", declared.MembershipId), ("person", declared.Identity.UserId.Value),
                ("organization", declared.OrganizationId?.Value), ("display", declared.DisplayName),
                ("locale", declared.PreferredLocale), ("role", declared.Role.RoleId))).Should().Be(1L);
        }
    }

    private async Task<long> CountContextualAsync(string table)
    {
        if (table is not ("roles" or "role_permission_grants" or "memberships" or "membership_profiles" or "membership_role_assignments"))
            throw new ArgumentException("Fixture table is invalid.", nameof(table));
        long count = 0;
        foreach (var tenant in SeedData.All)
        {
            OrganizationId?[] organizations = table is "roles" or "role_permission_grants"
                ? [null] : [null, tenant.DefaultOrganization.OrganizationId, tenant.SecondOrganization.OrganizationId];
            foreach (var organization in organizations)
            {
                await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
                await using var transaction = await app.BeginTransactionAsync();
                await SchemaQueries.AssertApplicationRoleAsync(app);
                await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value, organization?.Value);
                count += table is "roles" or "role_permission_grants"
                    ? await IdentityMembershipFixture.ScalarAsync<long>(app, transaction, $"SELECT count(*) FROM public.{table}")
                    : await IdentityMembershipFixture.ScalarAsync<long>(app, transaction,
                        $"SELECT count(*) FROM public.{table} WHERE organization_id IS NOT DISTINCT FROM @organization",
                        ("organization", organization?.Value));
            }
        }
        return count;
    }

    private async Task<long> AuditCountAsync()
    {
        await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.PlatformConnectionString);
        return await IdentityMembershipFixture.ScalarAsync<long>(platform, null, "SELECT count(*) FROM public.audit_log");
    }

    private async Task DeploymentAsync(string sql)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
        await IdentityMembershipFixture.ExecuteAsync(owner, null, sql);
    }

    private async Task AssertCapturedChildrenAsync()
    {
        await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.PlatformConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT operation,coalesce(after_state::text,''),coalesce(changes::text,''),coalesce(before_state::text,'')
            FROM public.audit_log WHERE module='identity' AND outcome='success'
            """, platform);
        await using var rows = await command.ExecuteReaderAsync();
        var roles = 0; var grantedRoles = 0; var members = 0; var profiles = 0;
        while (await rows.ReadAsync())
        {
            var operation = rows.GetString(0);
            var after = rows.GetString(1);
            var changes = rows.GetString(2);
            var snapshots = after + changes + rows.GetString(3);
            for (var index = 0; index < 4; index++) snapshots.Should().NotContain($"provider-admin-id-{index}");
            if (operation == "identity.role.initialize")
            {
                roles++;
                if (after.Contains("permissionKey", StringComparison.OrdinalIgnoreCase))
                {
                    grantedRoles++;
                    after.Should().ContainEquivalentOf("grants");
                    changes.Should().ContainEquivalentOf("grants");
                }
            }
            else if (operation is "identity.membership.create_first_seed_admin" or "identity.membership.create_seed")
            {
                members++;
                after.Should().ContainEquivalentOf("profile").And.ContainEquivalentOf("roleAssignments");
                changes.Should().ContainEquivalentOf("roleAssignments");
                using var created = JsonDocument.Parse(after);
                created.RootElement.GetProperty("Profile").GetProperty("DisplayName").GetString()
                    .Should().Be(SensitiveTokenCatalog.RedactedValue);
                created.RootElement.GetProperty("Profile").GetProperty("PreferredLocale").GetString()
                    .Should().Be(SensitiveTokenCatalog.RedactedValue);
            }
            else if (operation == "identity.membership_profile.set_seed_profile")
            {
                profiles++;
                changes.Should().ContainEquivalentOf("profile").And.Contain(SensitiveTokenCatalog.RedactedValue);
                using var diff = JsonDocument.Parse(changes);
                var sensitive = diff.RootElement.EnumerateArray().Where(field =>
                    field.GetProperty("path").GetString() is { } path && path.Contains("/Profile/", StringComparison.Ordinal)
                    && (path.EndsWith("/DisplayName", StringComparison.Ordinal) || path.EndsWith("/PreferredLocale", StringComparison.Ordinal))).ToArray();
                sensitive.Should().HaveCount(2);
                sensitive.Should().OnlyContain(field => field.GetProperty("after").GetString() == SensitiveTokenCatalog.RedactedValue
                    && field.GetProperty("before").GetString() == SensitiveTokenCatalog.RedactedValue);
                foreach (var member in SeedData.IdentityMemberships) snapshots.Should().NotContain(member.DisplayName);
            }
        }
        roles.Should().Be(16); grantedRoles.Should().Be(8); members.Should().Be(4); profiles.Should().Be(4);
    }

    private sealed class SeedLogCapture : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        public string Text { get { lock (_messages) return string.Join('\n', _messages); } }
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }
        private sealed class CapturingLogger(SeedLogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._messages) owner._messages.Add(formatter(state, exception));
            }
        }
    }
}
