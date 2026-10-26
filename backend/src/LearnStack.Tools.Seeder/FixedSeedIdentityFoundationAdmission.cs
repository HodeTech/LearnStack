using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Tools.Seeder;

/// <summary>Confined console capability: exactly four witnessed persons and exact retained v1 acts.</summary>
public sealed class FixedSeedIdentityFoundationAdmission : ISeedIdentityFoundationAdmission
{
    private readonly HashSet<UserId> _adopted = [];
    private readonly HashSet<SeedIdentityFoundationAct> _acts = [];

    public FixedSeedIdentityFoundationAdmission(SeedProviderWitness witness)
    {
        ArgumentNullException.ThrowIfNull(witness);
        Witness = witness;
        var manifest = IdentityFoundationManifest.Version1;
        if (SeedData.IdentityUsers.Length != 4 || witness.Subjects.Count != 4
            || SeedData.IdentityRoles.Length != 16 || SeedData.IdentityMemberships.Length != 4
            || SeedData.IdentityRoles.Select(role => role.RoleId).Distinct().Count() != 16
            || SeedData.IdentityMemberships.Select(member => member.MembershipId).Distinct().Count() != 4
            || SeedData.IdentityMemberships.Select(member => member.Identity.UserId).Distinct().Count() != 4
            || SeedData.IdentityUsers.Any(person => !witness.Subjects.ContainsKey(person.UserId))) throw Refused();
        foreach (var tenant in SeedData.All)
        {
            var roles = SeedData.IdentityRoles.Where(role => role.Tenant == tenant).ToArray();
            if (roles.Length != 8 || !roles.Select(role => (role.BuiltInKey, role.Scope)).ToHashSet()
                    .SetEquals(manifest.Templates.Select(template => (template.BuiltInKey, template.Scope)))) throw Refused();
        }
        foreach (var role in SeedData.IdentityRoles)
            foreach (var operation in new[] { SeedIdentityFoundationOperation.InitializeRole, SeedIdentityFoundationOperation.VerifyRole })
                _acts.Add(new(operation, role.Tenant.TenantId, null, UserId.SystemActor, role.RoleId,
                    null, null, role.BuiltInKey, role.Scope, manifest.Version,
                    manifest.RoleFingerprint(role.Tenant.TenantId, role.RoleId, role.BuiltInKey, role.Scope)));
        foreach (var member in SeedData.IdentityMemberships)
        {
            if (!witness.Subjects.ContainsKey(member.Identity.UserId) || member.Identity.Tenant != member.Role.Tenant
                || member.Scope != member.Role.Scope) throw Refused();
            foreach (var operation in new[] { member.IsFirstAdmin ? SeedIdentityFoundationOperation.CreateFirstAdmin
                    : SeedIdentityFoundationOperation.CreateMembership, SeedIdentityFoundationOperation.VerifyMembership })
                _acts.Add(new(operation, member.Identity.Tenant.TenantId, member.OrganizationId, UserId.SystemActor,
                    member.MembershipId, member.Identity.UserId, member.Role.RoleId, null, member.Scope, manifest.Version,
                    manifest.MembershipFingerprint(member.Identity.Tenant.TenantId, member.OrganizationId,
                        member.MembershipId, member.Identity.UserId, member.Role.RoleId)));
            _acts.Add(new(SeedIdentityFoundationOperation.SetProfile, member.Identity.Tenant.TenantId, member.OrganizationId,
                UserId.SystemActor, member.MembershipId, null, null, null, member.Scope, manifest.Version,
                manifest.ProfileFingerprint(member.Identity.Tenant.TenantId, member.OrganizationId, member.MembershipId,
                    member.DisplayName, member.PreferredLocale)));
        }
    }

    public SeedProviderWitness Witness { get; }

    public ValueTask<bool> AllowsAsync(GlobalIdentitySubject subject, UserId declaredActor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(subject is not null && Witness.Subjects.TryGetValue(declaredActor, out var expected)
            && subject.AuthorityId == expected.AuthorityId && string.Equals(subject.Issuer, expected.Issuer, StringComparison.Ordinal)
            && string.Equals(subject.Subject, expected.Subject, StringComparison.Ordinal));
    }

    public ValueTask<bool> AllowsFoundationAsync(SeedIdentityFoundationAct act, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_adopted.Count == 4 && _acts.Contains(act));
    }

    internal void RecordAdopted(UserId actor)
    {
        if (!Witness.Subjects.ContainsKey(actor)) throw Refused();
        _adopted.Add(actor);
    }

    public override string ToString() => nameof(FixedSeedIdentityFoundationAdmission);
    private static InvalidOperationException Refused() => new("Development identity declaration was refused.");
}
