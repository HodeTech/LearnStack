using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Seeding;

/// <summary>The closed development topology upgrade; no caller-selected host or tenant.</summary>
public static class SeedAdministrationOriginManifest
{
    public static ImmutableArray<SeedAdministrationOriginDeclaration> All { get; } =
    [
        new(TenantId.From(Guid.Parse("01930000-0000-7000-8000-000000000001")),
            "demo-english", "manage.demo-english.learnstack.local"),
        new(TenantId.From(Guid.Parse("01930000-0000-7000-8000-000000000002")),
            "demo-yoga", "manage.demo-yoga.learnstack.local"),
    ];

    public static SeedAdministrationOriginDeclaration? Find(TenantId tenantId) =>
        All.SingleOrDefault(declaration => declaration.TenantId == tenantId);
}
