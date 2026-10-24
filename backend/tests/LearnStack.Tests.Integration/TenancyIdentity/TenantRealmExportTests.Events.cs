using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantRealmExportTests
{
    // Independent observation of pinned EventType.saveByDefault, not derived from our export.
    internal static readonly string[] DefaultSavedEvents = Strings(JsonNode.Parse(File.ReadAllText(RepositoryFile(
        Path.Combine("backend", "tests", "LearnStack.Tests.Integration", "TenancyIdentity", "Fixtures",
            "keycloak-26.6.2-default-events.json"))))!["defaultSaved"]);

    [Fact]
    public void Every_pinned_default_saved_event_is_preserved_and_its_omission_is_detected()
    {
        DefaultSavedEvents.Should().HaveCount(103).And.OnlyHaveUniqueItems();
        var original = ReadRealm();
        Violations(original).Should().BeEmpty();
        foreach (var name in DefaultSavedEvents)
        {
            var variant = original.DeepClone().AsObject();
            var types = variant["enabledEventTypes"]!.AsArray();
            types.Remove(types.Single(value => value!.GetValue<string>() == name));
            Violations(variant).Should().Contain("complete pinned default-saved event selection",
                "removing default-saved type {0} must be independently detected", name);
        }
        Violations(original).Should().BeEmpty("planted omissions never alter the committed declaration");
    }
}
