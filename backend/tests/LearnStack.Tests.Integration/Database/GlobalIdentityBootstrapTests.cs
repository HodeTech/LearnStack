using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Caching;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Real production global consumers on app-role PostgreSQL, without an HTTP creation surface.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class GlobalIdentityBootstrapTests(SchemaFixture schema) : IAsyncLifetime
{
    private readonly SchemaFixture _schema = schema;
    private static readonly IdentityAuthorityId AuthorityId = IdentityAuthorityId.From(
        Guid.Parse("01930000-0000-7000-8000-00000000f301"));
    private static readonly IdentityAuthorityRegistration Registration = new(AuthorityId, "https://localhost/realms/learnstack");
    private static readonly UserId Actor = SeedData.IdentityUsers[0].UserId;
    private static GlobalIdentitySubject Subject => new(AuthorityId, Registration.Issuer, "opaque-bootstrap-subject-canary");
    private DisposableSchemaDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await DisposableSchemaDatabase.CreateAsync(_schema.Postgres);
        await InstallAsync(_database.MigrationConnectionString, Registration);
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task Production_creation_and_existing_reads_use_one_binding_one_MUST_row_and_no_repair()
    {
        await using var provider = BuildProvider(_database.AppConnectionString, Registration,
            new FixtureAdmission(Subject, Actor));
        await QualifyAsync(provider);
        (await AdoptAsync(provider, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.Created);
        (await AdoptAsync(provider, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.Existing);
        (await ReadAsync(provider, Subject)).Outcome.Should().Be(IdentityBindingOutcome.Existing);
        (await ReadAsync(provider, Subject, SeedData.IdentityUsers[1].UserId)).Outcome.Should().Be(IdentityBindingOutcome.Conflict);
        (await ReadAsync(provider, new(AuthorityId, Registration.Issuer, "unbound-other-subject")))
            .Outcome.Should().Be(IdentityBindingOutcome.NotFound);
        (await CountsAsync()).Should().Be((1L, 1L));
        var rows = await AuditRowsAsync();
        rows.Should().ContainSingle();
        rows[0].Outcome.Should().Be("success");
        rows[0].Metadata.Should().Contain("identity-seed-adoption").And.Contain(Subject.SubjectKey.Hex)
            .And.NotContain(Subject.Subject);
        rows[0].Snapshots.Should().Contain(SensitiveTokenCatalog.RedactedValue).And.NotContain(Subject.Subject);
    }

    [Fact]
    public async Task API_composition_denies_seed_adoption_before_opening_any_global_owner()
    {
        await using var provider = BuildProvider(_database.AppConnectionString, Registration);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var result = await services.GetRequiredService<ISeedIdentityBindingAdopter>().AdoptAsync(Subject, Actor);
        result.Outcome.Should().Be(IdentityBindingOutcome.AdmissionDenied);
        result.Actor.Should().BeNull();
        services.GetRequiredService<IUnitOfWork>().HasActiveTransaction.Should().BeFalse();
        services.GetRequiredService<IAuditStateCapture>().Intents.Should().BeEmpty();
        (await CountsAsync()).Should().Be((0L, 0L));
    }

    [Fact]
    public async Task Read_only_normalization_completes_then_a_tenant_owner_can_begin_in_the_same_scope()
    {
        await using var provider = BuildProvider(_database.AppConnectionString, Registration,
            new FixtureAdmission(Subject, Actor));
        await QualifyAsync(provider);
        await AdoptAsync(provider, Subject, Actor);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        (await services.GetRequiredService<IIdentityBindingReader>().ResolveExistingAsync(Subject))
            .Outcome.Should().Be(IdentityBindingOutcome.Existing);
        var unit = services.GetRequiredService<IUnitOfWork>();
        unit.HasActiveTransaction.Should().BeFalse();
        unit.IsRollbackOnly.Should().BeFalse();
        await using var tenant = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(new SeedTenantContext(SeedData.English.TenantId, null));
        await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
        await tenant.CompleteAsync();
        unit.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task Existing_seed_adoption_completes_without_intent_and_permits_same_scope_reuse()
    {
        await using var provider = BuildProvider(_database.AppConnectionString, Registration,
            new FixtureAdmission(Subject, Actor));
        await QualifyAsync(provider);
        (await AdoptAsync(provider, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.Created);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var adopter = services.GetRequiredService<ISeedIdentityBindingAdopter>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            (await adopter.AdoptAsync(Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.Existing);
            services.GetRequiredService<IUnitOfWork>().HasActiveTransaction.Should().BeFalse();
            services.GetRequiredService<IUnitOfWork>().IsRollbackOnly.Should().BeFalse();
            services.GetRequiredService<IAuditStateCapture>().Intents.Should().BeEmpty();
        }
        var unit = services.GetRequiredService<IUnitOfWork>();
        await using var tenant = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(new SeedTenantContext(SeedData.English.TenantId, null));
        await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
        await tenant.CompleteAsync();
        (await CountsAsync()).Should().Be((1L, 1L));
    }

    [Fact]
    public async Task Resolved_tenant_context_cannot_read_or_adopt_even_an_existing_binding()
    {
        await using (var normal = BuildProvider(_database.AppConnectionString, Registration, new FixtureAdmission(Subject, Actor)))
        {
            await QualifyAsync(normal);
            (await AdoptAsync(normal, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.Created);
        }
        var storeResolutions = 0;
        await using var provider = BuildProvider(_database.AppConnectionString, Registration, new FixtureAdmission(Subject, Actor), services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ITenantContextAccessor>(
                new StaticTenantContextAccessor(new SeedTenantContext(SeedData.English.TenantId, null))));
            services.Replace(ServiceDescriptor.Scoped<IUserBindingStore>(_ =>
            {
                Interlocked.Increment(ref storeResolutions);
                throw new InvalidOperationException("The inherited-context proof must refuse before resolving its store.");
            }));
        });
        await QualifyAsync(provider);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        (await services.GetRequiredService<IIdentityBindingReader>().ResolveExistingAsync(Subject))
            .Outcome.Should().Be(IdentityBindingOutcome.AdmissionDenied);
        (await services.GetRequiredService<ISeedIdentityBindingAdopter>().AdoptAsync(Subject, Actor))
            .Outcome.Should().Be(IdentityBindingOutcome.AdmissionDenied);
        storeResolutions.Should().Be(0);
        services.GetRequiredService<IUnitOfWork>().HasActiveTransaction.Should().BeFalse();
        services.GetRequiredService<IGlobalIdentityScopeAccessor>().Current.Should().BeNull();
        services.GetRequiredService<IAuditStateCapture>().Intents.Should().BeEmpty();
        (await CountsAsync()).Should().Be((1L, 1L));
    }

    [Fact]
    public async Task Qualification_refuses_missing_or_changed_anchor_without_installing_or_rebinding()
    {
        await using (var missing = await DisposableSchemaDatabase.CreateAsync(_schema.Postgres))
        await using (var unqualified = BuildProvider(missing.AppConnectionString, Registration, new FixtureAdmission(Subject, Actor)))
        {
            await FluentActions.Awaiting(() => QualifyAsync(unqualified)).Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Identity authority startup qualification is unavailable or does not match deployment configuration.");
            (await AdoptAsync(unqualified, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.AuthorityUnavailable);
        }
        var changed = new IdentityAuthorityRegistration(AuthorityId, "https://other.invalid/realms/learnstack");
        await using var provider = BuildProvider(_database.AppConnectionString, changed, new FixtureAdmission(Subject, Actor));
        await FluentActions.Awaiting(() => QualifyAsync(provider)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Identity authority startup qualification is unavailable or does not match deployment configuration.");
        (await AdoptAsync(provider, Subject, Actor)).Outcome.Should().Be(IdentityBindingOutcome.AuthorityUnavailable);
        await using var connection = await OpenConnectionAsync(_database.MigrationConnectionString);
        await FluentActions.Awaiting(() => IdentityAuthorityDeploymentInstaller.InstallAsync(connection, changed))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("Identity authority deployment installation was refused.");
        (await CountsAsync()).Should().Be((0L, 0L));
    }

    private static ServiceProvider BuildProvider(string appConnection, IdentityAuthorityRegistration registration,
        ISeedIdentityFoundationAdmission? admission = null, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IGuidFactory, SystemGuidFactory>();
        services.AddSingleton<ICacheService, InMemoryCacheService>();
        services.AddLearnStackMediatRPipeline();
        services.AddSingleton<ITenantContextAccessor>(StaticTenantContextAccessor.Unresolved);
        services.AddTransient<ITenantContext>(provider =>
            provider.GetRequiredService<ITenantContextAccessor>().Current ?? UnresolvedTenantContext.Instance);
        if (admission is not null) services.AddSingleton(admission);
        services.AddLearnStackPersistence(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = appConnection,
            ["Identity:AuthorityId"] = registration.AuthorityId.Value.ToString("D"),
            ["Authentication:Tenant:Issuer"] = registration.Issuer,
        }).Build());
        configure?.Invoke(services);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static Task QualifyAsync(ServiceProvider provider) => provider.GetRequiredService<IdentityAuthorityQualification>().QualifyAsync();

    private static async Task<NpgsqlConnection> OpenConnectionAsync(string connectionString) =>
        (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);

    private static async Task InstallAsync(string connectionString, IdentityAuthorityRegistration registration)
    {
        await using var connection = await OpenConnectionAsync(connectionString);
        await IdentityAuthorityDeploymentInstaller.InstallAsync(connection, registration);
    }

    private static async Task<IdentityBindingResult> AdoptAsync(ServiceProvider provider, GlobalIdentitySubject subject, UserId actor)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISeedIdentityBindingAdopter>().AdoptAsync(subject, actor);
    }

    private static async Task<IdentityBindingResult> ReadAsync(ServiceProvider provider, GlobalIdentitySubject subject, UserId? actor = null)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IIdentityBindingReader>().ResolveExistingAsync(subject, actor);
    }

    private async Task<(long Users, long Audits)> CountsAsync(GlobalIdentitySubject? subject = null)
    {
        var selected = subject ?? Subject;
        await using var connection = await OpenConnectionAsync(_database.MigrationConnectionString);
        await using var transaction = await AnnounceEvidenceAsync(connection, selected);
        await using var command = new NpgsqlCommand("SELECT (SELECT count(*) FROM public.users WHERE authority_id=@authority "
            + "AND subject_key=@key), (SELECT count(*) FROM public.audit_log WHERE operation='identity.user.create' "
            + "AND metadata->>'identity_authority_id'=@authority_text AND metadata->>'identity_subject_key'=@key_hex)", connection, transaction);
        command.Parameters.AddWithValue("authority", selected.AuthorityId.Value);
        command.Parameters.AddWithValue("key", selected.SubjectKey.ToByteArray());
        command.Parameters.AddWithValue("authority_text", selected.AuthorityId.Value.ToString("D"));
        command.Parameters.AddWithValue("key_hex", selected.SubjectKey.Hex);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private async Task<List<AuditRow>> AuditRowsAsync(GlobalIdentitySubject? subject = null)
    {
        var selected = subject ?? Subject;
        await using var connection = await OpenConnectionAsync(_database.MigrationConnectionString);
        await using var transaction = await AnnounceEvidenceAsync(connection, selected);
        await using var command = new NpgsqlCommand("SELECT outcome, metadata::text, "
            + "coalesce(before_state::text,'') || coalesce(after_state::text,'') || coalesce(changes::text,'') "
            + "FROM public.audit_log WHERE operation='identity.user.create' "
            + "AND metadata->>'identity_authority_id'=@authority AND metadata->>'identity_subject_key'=@key ORDER BY timestamp", connection, transaction);
        command.Parameters.AddWithValue("authority", selected.AuthorityId.Value.ToString("D"));
        command.Parameters.AddWithValue("key", selected.SubjectKey.Hex);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<AuditRow>();
        while (await reader.ReadAsync()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private static async Task<NpgsqlTransaction> AnnounceEvidenceAsync(NpgsqlConnection connection, GlobalIdentitySubject subject)
    {
        var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT set_config('app.identity_authority_id', @authority, true), "
            + "set_config('app.identity_subject_key', @subject, true)", connection, transaction);
        command.Parameters.AddWithValue("authority", subject.AuthorityId.Value.ToString("D"));
        command.Parameters.AddWithValue("subject", subject.SubjectKey.Hex);
        await command.ExecuteNonQueryAsync();
        return transaction;
    }

    private sealed record AuditRow(string Outcome, string Metadata, string Snapshots);

    private sealed class FixtureAdmission(GlobalIdentitySubject subject, UserId actor) : ISeedIdentityFoundationAdmission
    {
        public ValueTask<bool> AllowsAsync(GlobalIdentitySubject candidate, UserId declaredActor,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(declaredActor == actor
                && candidate.AuthorityId == subject.AuthorityId
                && candidate.SubjectKey.Equals(subject.SubjectKey)
                && string.Equals(candidate.Issuer, subject.Issuer, StringComparison.Ordinal)
                && string.Equals(candidate.Subject, subject.Subject, StringComparison.Ordinal));
    }
}
