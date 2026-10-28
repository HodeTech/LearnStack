using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>A single immutable issuer anchor and real app-role bindings, isolated from anonymous fixtures.</summary>
internal sealed class IdentityPublicReadFixture : PublicReadFixture
{
    private IdentityPublicReadFixture(DisposableSchemaDatabase database, IdentityAuthorityRegistration registration)
        : base(database) => Registration = registration;

    internal IdentityAuthorityRegistration Registration { get; }
    protected override bool RetainIdentityQualification => true;

    internal static async Task<IdentityPublicReadFixture> CreateAsync(PublicReadFixture parent, string issuer,
        IReadOnlyDictionary<UserId, string>? subjects = null)
    {
        var registration = new IdentityAuthorityRegistration(IdentityAuthorityId.From(Guid.CreateVersion7()), issuer);
        var database = await DisposableSchemaDatabase.CreateAsync(parent.Postgres);
        var fixture = new IdentityPublicReadFixture(database, registration);
        try
        {
            await fixture.InitializeAsync();
            await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
                await IdentityAuthorityDeploymentInstaller.InstallAsync(owner, fixture.Registration);
            if (subjects is not null)
                foreach (var (actor, subject) in subjects) await fixture.AdoptAsync(actor, subject);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal static async Task<IdentityPublicReadFixture> CreateWithMembershipsAsync(PublicReadFixture parent,
        string issuer, IReadOnlyDictionary<UserId, string> subjects)
    {
        var fixture = await CreateAsync(parent, issuer, subjects);
        try
        {
            await fixture.SeedHttpFoundationAsync();
            foreach (var actor in subjects.Keys)
                foreach (var tenant in SeedData.All)
                    await fixture.AddMembershipAsync(tenant, actor);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    internal static string FoundationSubject(UserId actor) => "http-foundation-" + actor.Value.ToString("N");

    internal async Task SeedHttpFoundationAsync()
    {
        await using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            issuer = Registration.Issuer,
            users = SeedData.IdentityUsers.Select(person => new
            {
                actorId = person.UserId.Value,
                opaqueSub = FoundationSubject(person.UserId),
            }),
        }));
        await SeedFoundationAsync(await SeedProviderWitness.ReadAsync(input, Registration));
    }

    internal async Task<Guid> AddMembershipAsync(SeedTenant tenant, UserId actor,
        OrganizationId? organization = null, BuiltInRoleKey role = BuiltInRoleKey.Learner,
        string? displayName = "HTTP contextual profile")
    {
        var membership = Guid.CreateVersion7();
        var permissionScope = organization is null ? PermissionScope.Tenant : PermissionScope.Organization;
        var roleId = SeedData.IdentityRoles.Single(value => value.Tenant == tenant
            && value.BuiltInKey == role && value.Scope == permissionScope).RoleId;
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value, organization?.Value);
        await IdentityMembershipFixture.LockAsync(app, transaction, tenant.TenantId.Value, actor.Value,
            role == BuiltInRoleKey.TenantAdmin);
        await IdentityMembershipFixture.InsertMemberAsync(app, transaction, tenant.TenantId.Value,
            organization?.Value, membership, actor.Value, roleId, displayName);
        await transaction.CommitAsync();
        return membership;
    }

    internal async Task SeedFoundationAsync(SeedProviderWitness witness)
    {
        await using var source = ApplicationDataSource.Build(AppConnectionString);
        var admission = new FixedSeedIdentityFoundationAdmission(witness);
        var runner = new SeedIdentityFoundationRunner(context => SeedComposition.Build(source, context,
            NullLoggerFactory.Instance, identityAuthority: Registration, seedAdmission: admission), admission);
        await runner.RunAsync();
        foreach (var (actor, subject) in witness.Subjects)
        {
            await using var provider = SeedComposition.Build(source, null, NullLoggerFactory.Instance,
                identityAuthority: Registration);
            await provider.GetRequiredService<IdentityAuthorityQualification>().QualifyAsync();
            await using var scope = provider.CreateAsyncScope();
            var found = await scope.ServiceProvider.GetRequiredService<IIdentityBindingReader>().ResolveExistingAsync(subject);
            found.Outcome.Should().Be(IdentityBindingOutcome.Existing);
            found.Actor.Should().Be(actor);
        }
    }

    // Fixture-created synthetic/claimless subjects still use the production writer,
    // an exact single-act admission and a separate app-role owner outside HTTP admission.
    internal async Task AdoptAsync(UserId actor, string opaqueSubject)
    {
        var subject = new GlobalIdentitySubject(Registration.AuthorityId, Registration.Issuer, opaqueSubject);
        await using var source = ApplicationDataSource.Build(AppConnectionString);
        await using var provider = SeedComposition.Build(source, null, NullLoggerFactory.Instance,
            identityAuthority: Registration, seedAdmission: new ExactBindingAdmission(subject, actor));
        await provider.GetRequiredService<IdentityAuthorityQualification>().QualifyAsync();
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISeedIdentityBindingAdopter>().AdoptAsync(subject, actor);
        result.IsBound.Should().BeTrue();
        result.Actor.Should().Be(actor);
    }

    protected override void ConfigureTestHost(IWebHostBuilder builder)
    {
        base.ConfigureTestHost(builder);
        builder.UseSetting("ConnectionStrings:IdentityAuthorization", AppConnectionString);
        builder.UseSetting("ConnectionStrings:AuthorizationEvidence", AppConnectionString);
        builder.UseSetting("Identity:AuthorityId", Registration.AuthorityId.Value.ToString("D"));
        builder.UseSetting("Authentication:Tenant:Issuer", Registration.Issuer);
    }

    private sealed class ExactBindingAdmission(GlobalIdentitySubject subject, UserId actor) : ISeedIdentityFoundationAdmission
    {
        public ValueTask<bool> AllowsAsync(GlobalIdentitySubject candidate, UserId declaredActor,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(declaredActor == actor
                && candidate.AuthorityId == subject.AuthorityId
                && candidate.SubjectKey.Equals(subject.SubjectKey)
                && string.Equals(candidate.Issuer, subject.Issuer, StringComparison.Ordinal)
                && string.Equals(candidate.Subject, subject.Subject, StringComparison.Ordinal));
    }
}
