using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Tools.Seeder;

/// <summary>
/// The two demo tenants, in domains chosen to be unrelated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two, and in unrelated domains, is the point rather than a convenience.</b> LearnStack
/// claims one binary and one schema serve a language school and a yoga studio, and the
/// claim is only tested by data that differs. A second tenant in the same domain would
/// exercise isolation and nothing else; these exercise
/// [the genericity boundary](../../../docs/architecture/01-platform-vision.md) as well.
/// </para>
/// <para>
/// <b>The ids are fixed literals, not generated.</b> Re-running the seeder has to land on
/// the same rows or it is not idempotent, and a fixed id is what lets the second run
/// recognise its own first. They are version-7 shaped so they sort like every other
/// identifier in the system.
/// </para>
/// <para>
/// <b>Each tenant gets two organizations</b>, because an organization is where
/// organization-scoped isolation is actually observable: one is the tenant's own default,
/// created by provisioning, and the second is what makes
/// <c>Org_X_cannot_read_Org_Y_within_TenantA</c> a statement about seeded data rather than
/// about a fixture.
/// </para>
/// <para>
/// <b>One host row each, and deliberately of different classes.</b> `demo-english` maps
/// host → tenant with a null organization and `demo-yoga` maps host → organization, so both
/// live classifications are exercised by the seed rather than only by a test. Which tenant
/// takes which is arbitrary on the merits and therefore settled by the corpus:
/// [the seed-tenant skill](../../../.claude/skills/seed-tenant/SKILL.md) named this pairing
/// before the code existed, and two documents disagreeing about a seeded row is how a
/// Phase 02d assertion ends up chasing the wrong host.
/// </para>
/// </remarks>
public static class SeedData
{
    public static readonly SeedTenant English = new(
        TenantId.From(Guid.Parse("01930000-0000-7000-8000-000000000001")),
        "demo-english",
        "English Hero",
        new SeedOrganization(
            OrganizationId.From(Guid.Parse("01930000-0000-7000-8000-0000000000a1")),
            "kadikoy",
            "Kadıköy Branch"),
        new SeedOrganization(
            OrganizationId.From(Guid.Parse("01930000-0000-7000-8000-0000000000a2")),
            "besiktas",
            "Beşiktaş Branch"),
        "demo-english.learnstack.local",
        MapHostToDefaultOrganization: false);

    public static readonly SeedTenant Yoga = new(
        TenantId.From(Guid.Parse("01930000-0000-7000-8000-000000000002")),
        "demo-yoga",
        "Anatolia Yoga",
        new SeedOrganization(
            OrganizationId.From(Guid.Parse("01930000-0000-7000-8000-0000000000b1")),
            "studio-one",
            "Studio One"),
        new SeedOrganization(
            OrganizationId.From(Guid.Parse("01930000-0000-7000-8000-0000000000b2")),
            "studio-two",
            "Studio Two"),
        "demo-yoga.learnstack.local",
        MapHostToDefaultOrganization: true);

    public static readonly IReadOnlyList<SeedTenant> All = [English, Yoga];
}

/// <param name="DefaultOrganization">Created by provisioning, in the same transaction.</param>
/// <param name="SecondOrganization">Created after, by an ordinary command.</param>
/// <param name="MapHostToDefaultOrganization">
/// Whether the host row carries an organization id. One tenant sets it and one leaves it
/// null, so the seed covers both host classifications.
/// </param>
public sealed record SeedTenant(
    TenantId TenantId,
    string Slug,
    string DisplayName,
    SeedOrganization DefaultOrganization,
    SeedOrganization SecondOrganization,
    string Host,
    bool MapHostToDefaultOrganization);

public sealed record SeedOrganization(
    OrganizationId OrganizationId, string Slug, string DisplayName);
