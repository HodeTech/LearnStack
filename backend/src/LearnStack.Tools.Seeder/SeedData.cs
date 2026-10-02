using System.Collections.Immutable;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Tools.Seeder;

/// <summary>The sole declaration of demo identities, content and expected inventory.</summary>
/// <remarks>Fixed UUIDv7 identities survive reruns; domain differences are data, never branches.</remarks>
public static class SeedData
{
    private static readonly SeedOrganization FirstBranch = new(
        OrganizationId.From(Id("01930000-0000-7000-8000-0000000000a1")), "kadikoy", "Kadıköy Branch");
    private static readonly SeedOrganization SecondBranch = new(
        OrganizationId.From(Id("01930000-0000-7000-8000-0000000000a2")), "besiktas", "Beşiktaş Branch");
    private static readonly SeedOrganization FirstStudio = new(
        OrganizationId.From(Id("01930000-0000-7000-8000-0000000000b1")), "studio-one", "Studio One");
    private static readonly SeedOrganization SecondStudio = new(
        OrganizationId.From(Id("01930000-0000-7000-8000-0000000000b2")), "studio-two", "Studio Two");

    public static readonly SeedTenant English = new(
        TenantId.From(Id("01930000-0000-7000-8000-000000000001")), "demo-english", "English Hero",
        FirstBranch, SecondBranch, "demo-english.learnstack.local", false,
        Id("01930000-0000-7000-8000-0000000000c1"), Id("01930000-0000-7000-8000-0000000000d1"),
        EnglishCurriculum());
    public static readonly SeedTenant Yoga = new(
        TenantId.From(Id("01930000-0000-7000-8000-000000000002")), "demo-yoga", "Anatolia Yoga",
        FirstStudio, SecondStudio, "demo-yoga.learnstack.local", true,
        Id("01930000-0000-7000-8000-0000000000c2"), Id("01930000-0000-7000-8000-0000000000d2"),
        YogaCurriculum());
    public static readonly IReadOnlyList<SeedTenant> All = [English, Yoga];

    // Provisioning audits both sanctioned roots; follow-on organization and host each add one.
    public static long ExpectedAuditWrites => Inventory.Tenants * 4L + Inventory.Locales
        + 2L * (Inventory.ContentTypes + Inventory.Taxonomies) + Inventory.Themes
        + Inventory.Courses + Inventory.Lessons + Inventory.Translations
        + All.Sum(tenant => tenant.Curriculum?.Courses.Sum(course => (course.Status == "Published" ? 1 : 0)
            + course.Lessons.Count(lesson => lesson.Status == "Published")) ?? 0);

    public static SeedInventory Inventory => new(
        All.Count, All.Count * 2, All.Count,
        All.Sum(tenant => tenant.Curriculum?.Locales.Length ?? 0),
        All.Sum(tenant => ContentTypes(tenant).Count()), All.Sum(tenant => Taxonomies(tenant).Count()),
        All.Sum(tenant => Taxonomies(tenant).Sum(taxonomy => taxonomy.Bands.Length)),
        All.Count(tenant => tenant.Curriculum is not null),
        All.Sum(tenant => tenant.Curriculum?.Courses.Length ?? 0),
        All.Sum(tenant => tenant.Curriculum?.Courses.Sum(course => course.Lessons.Length) ?? 0),
        All.Sum(tenant => tenant.Curriculum?.Courses.Sum(course => course.Translations.Length
            + course.Lessons.Sum(lesson => lesson.Translations.Length)) ?? 0));

    public static IEnumerable<SeedContentType> ContentTypes(SeedTenant tenant)
    {
        yield return new(tenant.BuiltInContentTypeId, BuiltInCustomizations.Card.Key,
            BuiltInCustomizations.SchemaVersion, BuiltInCustomizations.Card.DisplayName,
            BuiltInCustomizations.Card.JsonSchema, BuiltInCustomizations.Card.RendererKey);
        if (tenant.Curriculum is { } curriculum) yield return curriculum.ContentType;
    }
    public static IEnumerable<SeedTaxonomy> Taxonomies(SeedTenant tenant)
    {
        yield return new(tenant.BuiltInTaxonomyId, BuiltInCustomizations.Plain.Key,
            BuiltInCustomizations.SchemaVersion, BuiltInCustomizations.Plain.DisplayName,
            [.. BuiltInCustomizations.Plain.Bands.Select(band => new SeedBand(band.Key, band.DisplayName, band.Sort))]);
        if (tenant.Curriculum is { } curriculum) yield return curriculum.Taxonomy;
    }
    public static string[] CustomizationProjection(SeedTenant tenant) =>
        [.. ContentTypes(tenant).Select(type => $"content-type:{type.Key}@{tenant.TenantId}"),
            .. Taxonomies(tenant).Select(taxonomy => $"taxonomy:{taxonomy.Key}@{tenant.TenantId}"),
            .. Taxonomies(tenant).SelectMany(taxonomy => taxonomy.Bands.Select(band => $"band:{band.Key}@{tenant.TenantId}"))];
    public static long CustomizationGeneration(SeedTenant tenant) =>
        2L * (ContentTypes(tenant).Count() + Taxonomies(tenant).Count());

    private static SeedCurriculum EnglishCurriculum() => new(
        [new("en", true, true, 0)],
        new(Id("01930000-0000-7000-8000-0000000000c3"), "grammar-topic", 1, Labels("Grammar topic"),
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object",
             "properties":{"concept":{"type":"string","minLength":1},"example":{"type":"string","minLength":1}},
             "required":["concept","example"],"additionalProperties":false,
             "x-fields":[{"name":"concept","label":{"en":"Concept"}},{"name":"example","label":{"en":"Example"}}]}
            """, "default-card"),
        new(Id("01930000-0000-7000-8000-0000000000d3"), "cefr", 1, Labels("CEFR"),
            [new("a1", Labels("A1 · Beginner"), 0), new("a2", Labels("A2 · Elementary"), 1),
             new("b1", Labels("B1 · Intermediate"), 2), new("b2", Labels("B2 · Upper intermediate"), 3),
             new("c1", Labels("C1 · Advanced"), 4), new("c2", Labels("C2 · Proficient"), 5)]),
        new(Id("01930000-0000-7000-8000-0000000000e1"),
            """{"primary":"#1d4ed8","background":"#ffffff","foreground":"#111827","muted":"#4b5563"}"""),
        [EnglishCourse("01930000-0000-7000-8000-000000000e11", null, "foundations", "public", "Published", "a1",
            "English foundations", "Build confidence with everyday English.", "foundation",
            [EnglishLesson("01930000-0000-7000-8000-000000000e21", 0, "Published", "Present simple", "present-simple",
                """{"concept":"Use the present simple for habits.","example":"I practise English every morning."}"""),
             EnglishLesson("01930000-0000-7000-8000-000000000e22", 1, "Draft", "Questions", "questions",
                """{"concept":"Begin questions with an auxiliary verb.","example":"Do you speak English?"}""")]),
         EnglishCourse("01930000-0000-7000-8000-000000000e12", null, "next-steps", "public", "Draft", "a2",
            "Next steps", "An upcoming course for curious learners.", "next-steps",
            [EnglishLesson("01930000-0000-7000-8000-000000000e23", 0, "Published", "Past simple", "past-simple",
                """{"concept":"Use the past simple for finished actions.","example":"We visited the library yesterday."}""")]),
         EnglishCourse("01930000-0000-7000-8000-000000000e13", null, "guided-practice", "enrollment_required", "Published", "b1",
            "Guided practice", "A published course reserved for enrolled learners.", "guided-practice",
            [EnglishLesson("01930000-0000-7000-8000-000000000e24", 0, "Published", "Giving reasons", "giving-reasons",
                """{"concept":"Connect an idea and its reason with because.","example":"I study English because I enjoy meeting people."}""")]),
         EnglishCourse("01930000-0000-7000-8000-000000000e14", FirstBranch.OrganizationId, "branch-conversation", "public", "Published", "a1",
            "Branch conversation", "Practice everyday exchanges at our branch.", "branch-conversation",
            [EnglishLesson("01930000-0000-7000-8000-000000000e25", 0, "Published", "Introductions", "introductions",
                """{"concept":"Introduce yourself with a greeting and your name.","example":"Hello, my name is Deniz."}""")])]);

    private static SeedCurriculum YogaCurriculum() => new(
        [new("tr-TR", true, true, 0), new("en", true, false, 1)],
        new(Id("01930000-0000-7000-8000-0000000000c4"), "asana-pose", 1, Labels("Asana pose", "Asana duruşu"),
            """
            {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object",
             "properties":{"pose":{"type":"string","minLength":1},"instruction":{"type":"string","minLength":1},"breathing":{"type":"string","minLength":1}},
             "required":["pose","instruction","breathing"],"additionalProperties":false,
             "x-fields":[{"name":"pose","label":{"tr-TR":"Duruş","en":"Pose"}},
                         {"name":"instruction","label":{"tr-TR":"Yönerge","en":"Instruction"}},
                         {"name":"breathing","label":{"tr-TR":"Nefes","en":"Breathing"}}]}
            """, "default-card"),
        new(Id("01930000-0000-7000-8000-0000000000d4"), "yoga-difficulty", 1, Labels("Practice difficulty", "Pratik düzeyi"),
            [new("foundation", Labels("Foundation", "Temel"), 0), new("developing", Labels("Developing", "Gelişen"), 1),
             new("advanced", Labels("Advanced", "İleri"), 2)]),
        new(Id("01930000-0000-7000-8000-0000000000e2"),
            """{"primary":"#166534","background":"#fffbeb","foreground":"#292524","muted":"#57534e"}"""),
        [YogaCourse("01930000-0000-7000-8000-000000000f11", null, "shared-practice", "public", "foundation",
            "Ortak pratik", "Dengeli bir başlangıç için temel hareketler.", "ortak-pratik",
            "Shared practice", "Simple movements for a balanced beginning.", "shared-practice",
            [YogaLesson("01930000-0000-7000-8000-000000000f21", 0, "Dağ duruşu", "dag-durusu",
                """{"pose":"Dağ duruşu","instruction":"Ayaklarını dengeli yerleştir ve sakin nefes al.","breathing":"Nefesini tutmadan doğal ritmini koru."}""",
                "Mountain pose", "mountain-pose", """{"pose":"Mountain pose","instruction":"Stand evenly on both feet and breathe calmly.","breathing":"Keep a natural rhythm without holding your breath."}""")]),
         YogaCourse("01930000-0000-7000-8000-000000000f12", FirstStudio.OrganizationId, "studio-foundations", "public", "foundation",
            "Stüdyo temelleri", "Birinci stüdyoda temel duruşları keşfet.", "studyo-temelleri",
            "Studio foundations", "Explore foundational poses in our first studio.", "foundation",
            [YogaLesson("01930000-0000-7000-8000-000000000f22", 0, "Ağaç duruşu", "agac-durusu",
                """{"pose":"Ağaç duruşu","instruction":"Bakışını sabit bir noktaya yönelt ve dengeni koru.","breathing":"Nefesini tutmadan doğal ritmini koru."}""",
                "Tree pose", "tree-pose", """{"pose":"Tree pose","instruction":"Focus on a steady point and maintain your balance.","breathing":"Keep a natural rhythm without holding your breath."}"""),
             YogaLesson("01930000-0000-7000-8000-000000000f23", 1, "Çocuk duruşu", "cocuk-durusu",
                """{"pose":"Çocuk duruşu","instruction":"Gövdeni rahatlat ve nefesini yavaşlat.","breathing":"Nefesini tutmadan doğal ritmini koru."}""",
                "Child pose", "child-pose", """{"pose":"Child pose","instruction":"Relax your torso and slow your breathing.","breathing":"Keep a natural rhythm without holding your breath."}""")]),
         YogaCourse("01930000-0000-7000-8000-000000000f13", SecondStudio.OrganizationId, "studio-flow", "public", "developing",
            "Stüdyo akışı", "İkinci stüdyoda hareketleri nefesle birleştir.", "studyo-akisi",
            "Studio flow", "Connect movement with breath in our second studio.", "studio-flow",
            [YogaLesson("01930000-0000-7000-8000-000000000f24", 0, "Savaşçı duruşu", "savasci-durusu",
                """{"pose":"Savaşçı duruşu","instruction":"Dizini ayağınla hizala ve kollarını aç.","breathing":"Nefesini tutmadan doğal ritmini koru."}""",
                "Warrior pose", "warrior-pose", """{"pose":"Warrior pose","instruction":"Align your knee with your foot and open your arms.","breathing":"Keep a natural rhythm without holding your breath."}""")]),
         YogaCourse("01930000-0000-7000-8000-000000000f14", FirstStudio.OrganizationId, "guided-flow", "enrollment_required", "developing",
            "Rehberli akış", "Kayıtlı öğrenciler için yayımlanmış pratik.", "rehberli-akis",
            "Guided flow", "Published practice for enrolled learners.", "guided-flow",
            [YogaLesson("01930000-0000-7000-8000-000000000f25", 0, "Köprü duruşu", "kopru-durusu",
                """{"pose":"Köprü duruşu","instruction":"Kalçanı yavaşça kaldır ve omuzlarını gevşet.","breathing":"Nefesini tutmadan doğal ritmini koru."}""",
                "Bridge pose", "bridge-pose", """{"pose":"Bridge pose","instruction":"Lift your hips slowly and relax your shoulders.","breathing":"Keep a natural rhythm without holding your breath."}""")])]);

    private static SeedCourse EnglishCourse(string id, OrganizationId? organization, string key, string access,
        string status, string band, string title, string summary, string slug, ImmutableArray<SeedLesson> lessons) =>
        new(Id(id), organization, key, access, "cefr", 1, band, status,
            [new("en", title, summary, slug)], lessons);
    private static SeedLesson EnglishLesson(string id, int sort, string status, string title, string slug, string body) =>
        new(Id(id), sort, "grammar-topic", 1, status, [new("en", title, slug, body)]);
    private static SeedCourse YogaCourse(string id, OrganizationId? organization, string key, string access,
        string band, string trTitle, string trSummary, string trSlug, string enTitle, string enSummary, string enSlug,
        ImmutableArray<SeedLesson> lessons) => new(Id(id), organization, key, access, "yoga-difficulty", 1, band, "Published",
            [new("tr-TR", trTitle, trSummary, trSlug), new("en", enTitle, enSummary, enSlug)], lessons);
    private static SeedLesson YogaLesson(string id, int sort, string trTitle, string trSlug, string trBody,
        string enTitle, string enSlug, string enBody) => new(Id(id), sort, "asana-pose", 1, "Published",
            [new("tr-TR", trTitle, trSlug, trBody), new("en", enTitle, enSlug, enBody)]);
    private static Guid Id(string value) => Guid.Parse(value);
    private static ImmutableDictionary<string, string> Labels(string en, string? tr = null)
    {
        var labels = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal).Add("en", en);
        return tr is null ? labels : labels.Add("tr-TR", tr);
    }
}

public sealed record SeedTenant(TenantId TenantId, string Slug, string DisplayName,
    SeedOrganization DefaultOrganization, SeedOrganization SecondOrganization, string Host,
    bool MapHostToDefaultOrganization, Guid BuiltInContentTypeId, Guid BuiltInTaxonomyId,
    SeedCurriculum? Curriculum = null);
public sealed record SeedOrganization(OrganizationId OrganizationId, string Slug, string DisplayName);
public sealed record SeedCurriculum(ImmutableArray<SeedLocale> Locales, SeedContentType ContentType,
    SeedTaxonomy Taxonomy, SeedTheme Theme, ImmutableArray<SeedCourse> Courses);
public sealed record SeedLocale(string Locale, bool IsEnabled, bool IsDefault, short Sort);
public sealed record SeedContentType(Guid Id, string Key, int SchemaVersion,
    IReadOnlyDictionary<string, string> DisplayName, string JsonSchema, string RendererKey);
public sealed record SeedTaxonomy(Guid Id, string Key, int SchemaVersion,
    IReadOnlyDictionary<string, string> DisplayName, ImmutableArray<SeedBand> Bands);
public sealed record SeedBand(string Key, IReadOnlyDictionary<string, string> DisplayName, short Sort, string? Metadata = null);
public sealed record SeedTheme(Guid Id, string Value);
public sealed record SeedCourse(Guid Id, OrganizationId? OrganizationId, string SlugKey, string ContentAccess,
    string LevelTaxonomyKey, int LevelTaxonomySchemaVersion, string LevelBandKey, string Status,
    ImmutableArray<SeedCourseTranslation> Translations, ImmutableArray<SeedLesson> Lessons);
public sealed record SeedCourseTranslation(string Locale, string Title, string? Summary, string Slug);
public sealed record SeedLesson(Guid Id, int Sort, string ContentTypeKey, int ContentTypeSchemaVersion,
    string Status, ImmutableArray<SeedLessonTranslation> Translations);
public sealed record SeedLessonTranslation(string Locale, string Title, string Slug, string Body);
public sealed record SeedInventory(int Tenants, int Organizations, int Hosts, int Locales,
    int ContentTypes, int Taxonomies, int Bands, int Themes, int Courses, int Lessons, int Translations);
