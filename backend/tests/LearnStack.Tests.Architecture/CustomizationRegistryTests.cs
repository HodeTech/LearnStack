using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Modules.Customization.Domain;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The closed renderer registries exist twice — once in C#, once in TypeScript —
/// and this is what stops them from drifting.
/// </summary>
/// <remarks>
/// <para>
/// Two copies are unavoidable:
/// <see href="../../../docs/architecture/32-tenant-customization-model.md">§ 8.1</see>
/// requires a <c>renderer_key</c> to be resolved <b>on saving</b>, which is a
/// backend check, while the resolution that actually draws the component is a
/// frontend map. What is avoidable is the two disagreeing, and the corpus has
/// already paid for that once: the same document's primitive list carried three
/// keys the ADR does not grant and a spelling (<c>embed_html</c>) that no tenant
/// row could ever have matched, unnoticed until Packet 8 reconciled it.
/// </para>
/// <para>
/// The rule is deliberately <b>containment</b> and not equality. The composite set
/// is nine; <c>composites.ts</c> registers four, and the five shells land with the
/// phases that render them
/// (<see href="../../../docs/roadmap/phase-05-education-learning-content.md">05</see>,
/// <see href="../../../docs/roadmap/phase-08a-assessment-notifications.md">08a</see>,
/// <see href="../../../docs/roadmap/phase-08c-classroom.md">08c</see>). A key that
/// is declared and not yet registered renders <c>UnknownBlock</c> per
/// <see href="../../../docs/decisions/0013-page-block-schema-versioning.md">ADR-0013</see>.
/// A key the frontend registers and the backend does not is the direction that
/// breaks: the save is refused for a renderer that exists.
/// </para>
/// </remarks>
public sealed class CustomizationRegistryTests
{
    [Fact]
    public void Composite_Renderer_Keys_Match_The_Frontend_Registry()
    {
        var registered = FrontendKeys("composites.ts", "COMPOSITE_KEYS");

        registered.Should().NotBeEmpty(
            "a rule that finds nothing to check passes for the wrong reason");

        registered.Should().BeSubsetOf(
            CompositeRendererKey.All,
            "the backend refuses a renderer_key it does not know, so a composite the "
            + "frontend registers and the backend has not declared cannot be saved — "
            + "the author is told the renderer does not exist while the page can draw it");
    }

    [Fact]
    public void The_frontend_primitive_set_is_the_twelve_ADR_0018_grants()
    {
        // Not a composite-renderer rule, but the same failure mode and the same
        // file pair. ADR-0018 § Renderer architecture is the decision; the
        // architecture doc is its deep dive and drifted from it. Stated here as
        // literals so a fourteenth primitive fails rather than being absorbed.
        FrontendKeys("primitives.ts", "PRIMITIVE_KEYS")
            .Should().BeEquivalentTo(GrantedPrimitiveKeys);
    }

    /// <summary>
    /// The twelve primitives ADR-0018 § Renderer architecture grants, written out
    /// rather than read from either registry.
    /// </summary>
    private static readonly string[] GrantedPrimitiveKeys =
    [
        "text", "markdown", "image", "video", "audio", "pdf",
        "code", "math", "link", "list", "tabs", "embed-html",
    ];

    /// <summary>
    /// The string literals inside one <c>as const</c> array in a customization
    /// registry file.
    /// </summary>
    /// <remarks>
    /// A regex over TypeScript rather than a parse, and bounded to the one
    /// declaration named — the file also carries prose and type aliases, and a
    /// whole-file literal scan would pick up words from the comment that explains
    /// the rule.
    /// </remarks>
    private static List<string> FrontendKeys(string fileName, string declaration)
    {
        var path = Path.Combine(
            RepositoryPaths.FrontendApps(),
            "web", "src", "lib", "customization", fileName);

        File.Exists(path).Should().BeTrue($"{fileName} is where the registry lives");

        var source = File.ReadAllText(path);
        var block = Regex.Match(
            source,
            $@"export\s+const\s+{Regex.Escape(declaration)}\s*=\s*\[(?<body>.*?)\]\s*as\s+const",
            RegexOptions.Singleline);

        block.Success.Should().BeTrue(
            $"{fileName} must declare `{declaration}` as a const array — a renamed or "
            + "restructured declaration silently empties this rule");

        return Regex.Matches(block.Groups["body"].Value, @"'(?<key>[^']+)'")
            .Select(match => match.Groups["key"].Value)
            .ToList();
    }
}
