using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Tools.Seeder;

/// <summary>Separate root acts, with exact fresh preconditions before any mutation request.</summary>
public sealed class SeedIdentityFoundationRunner(Func<ITenantContext?, ServiceProvider> compose,
    FixedSeedIdentityFoundationAdmission admission)
{
    private static readonly IdentityFoundationManifest Manifest = IdentityFoundationManifest.Version1;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        foreach (var person in SeedData.IdentityUsers)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(10));
            await using var provider = compose(null);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IdentityAuthorityQualification>().QualifyAsync(budget.Token);
            var result = await scope.ServiceProvider.GetRequiredService<ISeedIdentityBindingAdopter>()
                .AdoptAsync(admission.Witness.Subjects[person.UserId], person.UserId, budget.Token);
            if (!result.IsBound || result.Actor != person.UserId) throw Refused();
            admission.RecordAdopted(person.UserId);
        }
        await VerifyInstallationDispositionAsync(cancellationToken);
        // Every Role is a separate aggregate transaction. All sixteen precede Memberships.
        foreach (var declaration in SeedData.IdentityRoles)
        {
            var context = new SeedIdentityTenantContext(declaration.Tenant.TenantId, null);
            var fingerprint = Manifest.RoleFingerprint(declaration.Tenant.TenantId, declaration.RoleId,
                declaration.BuiltInKey, declaration.Scope);
            var query = new GetRoleSeedStateQuery(declaration.RoleId, declaration.BuiltInKey,
                declaration.Scope, Manifest.Version, fingerprint);
            var before = (await SendAsync(context, query, cancellationToken)).State;
            if (MatchesRole(before, declaration)) continue;
            await SendAsync(context, new InitializeSeedRoleCommand(declaration.RoleId, declaration.BuiltInKey,
                declaration.Scope, Manifest.Version, fingerprint), cancellationToken);
            if (!MatchesRole((await SendAsync(context, query, cancellationToken)).State, declaration)) throw Refused();
        }
        // Establish both administrators first; an interruption retains every prior commit.
        foreach (var declaration in SeedData.IdentityMemberships.OrderByDescending(member => member.IsFirstAdmin))
        {
            var context = Context(declaration);
            var fingerprint = MembershipFingerprint(declaration);
            var query = MembershipQuery(declaration);
            var before = (await SendAsync(context, query, cancellationToken)).State;
            if (MatchesMembership(before, declaration)) continue;
            if (declaration.IsFirstAdmin)
                await SendAsync(context, new CreateFirstSeedAdminMembershipCommand(declaration.MembershipId,
                    declaration.Identity.UserId, declaration.Role.RoleId, Manifest.Version, fingerprint), cancellationToken);
            else
                await SendAsync(context, new CreateSeedMembershipCommand(declaration.MembershipId,
                    declaration.Identity.UserId, declaration.Role.RoleId, declaration.Scope,
                    Manifest.Version, fingerprint), cancellationToken);
            if (!MatchesMembership((await SendAsync(context, query, cancellationToken)).State, declaration)) throw Refused();
        }
        foreach (var declaration in SeedData.IdentityMemberships)
        {
            var context = Context(declaration);
            var query = MembershipQuery(declaration);
            var before = (await SendAsync(context, query, cancellationToken)).State;
            if (!MatchesMembership(before, declaration)) throw Refused();
            // A committed later version is retained history, including an edited empty profile.
            if (before!.Version > 0) continue;
            await SendAsync(context, new SetSeedMembershipProfileCommand(declaration.MembershipId,
                declaration.Identity.UserId, declaration.Role.RoleId, declaration.Scope, declaration.DisplayName, declaration.PreferredLocale, Manifest.Version,
                Manifest.ProfileFingerprint(declaration.Identity.Tenant.TenantId, declaration.OrganizationId,
                    declaration.MembershipId, declaration.DisplayName, declaration.PreferredLocale)), cancellationToken);
            var after = (await SendAsync(context, query, cancellationToken)).State;
            if (!MatchesMembership(after, declaration) || !MatchesProfile(after!, declaration)) throw Refused();
        }
    }

    private static bool MatchesRole(RoleSeedDto? row, SeedFoundationRole declaration)
    {
        if (row is null) return false;
        if (!SeedIdentityInstallationEvidence.RoleIsInstalled(row, declaration.RoleId,
                declaration.Tenant.TenantId, declaration.BuiltInKey, declaration.Scope))
            throw Refused();
        return true;
    }

    private static bool MatchesMembership(MembershipSeedDto? row, SeedFoundationMembership declaration)
    {
        if (row is null) return false;
        if (!SeedIdentityInstallationEvidence.MembershipIsInstalled(row, declaration.MembershipId,
                declaration.Identity.Tenant.TenantId, declaration.OrganizationId, declaration.Identity.UserId)) throw Refused();
        return true;
    }

    private static bool MatchesProfile(MembershipSeedDto row, SeedFoundationMembership declaration) =>
        row.Version == 1 && string.Equals(row.DisplayName, declaration.DisplayName, StringComparison.Ordinal)
        && string.Equals(row.PreferredLocale, declaration.PreferredLocale, StringComparison.Ordinal);

    private async Task VerifyInstallationDispositionAsync(CancellationToken ct)
    {
        foreach (var tenant in SeedData.All)
        {
            var roles = new List<(SeedFoundationRole Declaration, RoleSeedDto? State)>();
            foreach (var role in SeedData.IdentityRoles.Where(role => role.Tenant == tenant))
            {
                var query = new GetRoleSeedStateQuery(role.RoleId, role.BuiltInKey, role.Scope, Manifest.Version,
                    Manifest.RoleFingerprint(tenant.TenantId, role.RoleId, role.BuiltInKey, role.Scope));
                var state = (await SendAsync(new SeedIdentityTenantContext(tenant.TenantId, null), query, ct)).State;
                _ = MatchesRole(state, role);
                roles.Add((role, state));
            }
            var members = new List<(SeedFoundationMembership Declaration, MembershipSeedDto? State)>();
            foreach (var member in SeedData.IdentityMemberships.Where(member => member.Identity.Tenant == tenant))
            {
                var state = (await SendAsync(Context(member), MembershipQuery(member), ct)).State;
                _ = MatchesMembership(state, member);
                members.Add((member, state));
            }
            // Completion is named immutable roots plus valid current graphs, never mutable demo equality.
            if (roles.All(value => value.State is not null) && members.All(value => value.State is { Version: > 0 })) continue;
            // Every retained predecessor of an incomplete installation must still be the original v1 act.
            if (roles.Any(value => value.State is { } row && !SeedIdentityInstallationEvidence.RoleIsPristine(row))
                || members.Any(value => value.State is { } row && !SeedIdentityInstallationEvidence.MembershipIsPristine(
                    row, value.Declaration.Role.RoleId, value.Declaration.DisplayName, value.Declaration.PreferredLocale)))
                throw Refused();
            if (roles.Any(value => value.State is null) && members.Any(value => value.State is not null)) throw Refused();
        }
    }

    private static string MembershipFingerprint(SeedFoundationMembership declaration) =>
        Manifest.MembershipFingerprint(declaration.Identity.Tenant.TenantId, declaration.OrganizationId,
            declaration.MembershipId, declaration.Identity.UserId, declaration.Role.RoleId);

    private static GetMembershipSeedStateQuery MembershipQuery(SeedFoundationMembership declaration) =>
        new(declaration.MembershipId, declaration.Identity.UserId, declaration.Role.RoleId,
            declaration.Scope, Manifest.Version, MembershipFingerprint(declaration));

    private static SeedIdentityTenantContext Context(SeedFoundationMembership declaration) =>
        new(declaration.Identity.Tenant.TenantId, declaration.OrganizationId);

    private async Task<T> SendAsync<T>(ITenantContext context, IRequest<Result<T>> request, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        var act = Declaration(context, request);
        if (!await admission.AllowsFoundationAsync(act, budget.Token)) throw Refused();
        await using var provider = compose(context);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, budget.Token);
        if (!result.IsSuccess || result.Value is null) throw Refused();
        return result.Value;
    }

    private static SeedIdentityFoundationAct Declaration<T>(ITenantContext context, IRequest<Result<T>> request) => request switch
    {
        InitializeSeedRoleCommand value => Act(context, SeedIdentityFoundationOperation.InitializeRole,
            value.RoleId, null, null, value.BuiltInKey, value.CapabilityScope, value.ManifestVersion, value.ExpectedFingerprint),
        GetRoleSeedStateQuery value => Act(context, SeedIdentityFoundationOperation.VerifyRole,
            value.RoleId, null, null, value.BuiltInKey, value.CapabilityScope, value.ManifestVersion, value.ExpectedFingerprint),
        CreateFirstSeedAdminMembershipCommand value => Act(context, SeedIdentityFoundationOperation.CreateFirstAdmin,
            value.MembershipId, value.UserId, value.RoleId, null, PermissionScope.Tenant, value.ManifestVersion, value.ExpectedFingerprint),
        CreateSeedMembershipCommand value => Act(context, SeedIdentityFoundationOperation.CreateMembership,
            value.MembershipId, value.UserId, value.RoleId, null, value.MembershipScope, value.ManifestVersion, value.ExpectedFingerprint),
        GetMembershipSeedStateQuery value => Act(context, SeedIdentityFoundationOperation.VerifyMembership,
            value.MembershipId, value.UserId, value.RoleId, null, value.MembershipScope, value.ManifestVersion, value.ExpectedFingerprint),
        SetSeedMembershipProfileCommand value => Act(context, SeedIdentityFoundationOperation.SetProfile,
            value.MembershipId, value.ExpectedUserId, value.ExpectedRoleId, null, value.MembershipScope, value.ManifestVersion, value.ExpectedFingerprint),
        _ => throw Refused(),
    };

    private static SeedIdentityFoundationAct Act(ITenantContext context, SeedIdentityFoundationOperation operation,
        Guid rootId, UserId? target, Guid? roleId, BuiltInRoleKey? key, PermissionScope scope, int version, string fingerprint) =>
        new(operation, context.TenantId, context.OrganizationId, context.UserId!.Value, rootId,
            target, roleId, key, scope, version, fingerprint);

    private static InvalidOperationException Refused() => new("Development identity foundation was refused.");
}

/// <summary>The trusted declaration supplies exact SQL scope; provider organization claims never select it.</summary>
public sealed class SeedIdentityTenantContext(TenantId tenantId, OrganizationId? organizationId) : ITenantContext
{
    public bool IsResolved => true;
    public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
    public TenantId TenantId => tenantId;
    public OrganizationId? OrganizationId => organizationId;
    public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
    public string? CorrelationId => null;
    public string? ModuleName => null;
}
