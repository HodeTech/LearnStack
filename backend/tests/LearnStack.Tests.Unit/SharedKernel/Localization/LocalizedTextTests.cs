using FluentAssertions;
using LearnStack.SharedKernel.Localization;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Localization;

/// <summary>
/// <see cref="LocalizedText"/> — the Pattern B shape every taxonomy label and
/// content-type name is stored in.
/// </summary>
/// <remarks>
/// The column is <c>jsonb</c> and PostgreSQL will store any well-formed object in
/// it, so every rule this type states is a rule only this type keeps. The
/// resolution tests matter most: a label that silently answers in the wrong
/// language is a defect no constraint can catch and no page renders differently
/// enough to notice.
/// </remarks>
public sealed class LocalizedTextTests
{
    [Fact]
    public void Locales_are_canonicalized_so_two_spellings_cannot_be_two_entries()
    {
        var text = LocalizedText.From(("EN-us", "Beginner"), ("TR", "Başlangıç"));

        text.Locales.Should().BeEquivalentTo(["en-US", "tr"]);
        text.Resolve("en-us").Should().Be("Beginner");
        text.Resolve("EN-US").Should().Be("Beginner");
    }

    [Fact]
    public void Two_spellings_of_one_locale_are_refused_rather_than_merged()
    {
        // Keeping the last would silently discard the first translation and leave
        // the author believing both were stored.
        var act = () => LocalizedText.From(("en-US", "Beginner"), ("en-us", "Novice"));

        act.Should().Throw<ArgumentException>().WithMessage("*appears twice*");
    }

    [Fact]
    public void An_empty_map_is_refused()
    {
        var act = () => LocalizedText.From([]);

        act.Should().Throw<ArgumentException>().WithMessage("*at least one locale*");
    }

    [Fact]
    public void A_blank_value_is_refused_because_an_untranslated_locale_is_an_absent_key()
    {
        var act = () => LocalizedText.From(("en", "Beginner"), ("tr", "   "));

        act.Should().Throw<ArgumentException>().WithMessage("*blank*");
    }

    [Fact]
    public void A_malformed_locale_tag_is_refused()
    {
        var act = () => LocalizedText.From(("not a tag", "Beginner"));

        act.Should().Throw<ArgumentException>().WithMessage("*BCP-47*");
    }

    [Fact]
    public void A_value_past_the_mapped_width_is_refused_at_the_factory()
    {
        var act = () => LocalizedText.From(("en", new string('x', LocalizedText.MaxValueLength + 1)));

        act.Should().Throw<ArgumentException>().WithMessage("*the column holds*");
    }

    [Fact]
    public void More_locales_than_the_cap_are_refused()
    {
        // Distinct well-formed tags, generated rather than typed: the cap is what
        // is under test, not the tag vocabulary. A primary subtag is letters only,
        // so these are two-letter pairs rather than anything carrying a digit.
        var pairs = Enumerable.Range(0, LocalizedText.MaxLocales + 1)
            .Select(i => ($"{(char)('a' + (i / 26))}{(char)('a' + (i % 26))}", "value"))
            .ToArray();

        var act = () => LocalizedText.From(pairs);

        act.Should().Throw<ArgumentException>().WithMessage("*the cap is*");
    }

    [Fact]
    public void Resolve_prefers_the_exact_tag()
    {
        var text = LocalizedText.From(("en", "Colour"), ("en-US", "Color"));

        text.Resolve("en-US").Should().Be("Color");
        text.Resolve("en").Should().Be("Colour");
    }

    [Fact]
    public void Resolve_falls_back_to_the_language_subtag_but_never_widens_the_other_way()
    {
        var languageOnly = LocalizedText.From(("en", "Colour"));
        var regionOnly = LocalizedText.From(("en-US", "Color"));

        // A request for en-US is answered by en …
        languageOnly.Resolve("en-US").Should().Be("Colour");

        // … but a request for `en` is NOT answered by `en-US` through this step.
        // Narrowing is documented; widening would pick a region the caller did not
        // ask for, and with two regions authored it would pick one arbitrarily.
        regionOnly.Resolve("en", ["fr"]).Should().Be("Color",
            "the chain is exhausted, so the last resort answers — not the subtag step");
    }

    [Fact]
    public void Resolve_walks_the_supplied_chain_in_order()
    {
        var text = LocalizedText.From(("de", "Anfänger"), ("en", "Beginner"));

        // The tenant default comes before the platform default, and the first hit wins.
        text.Resolve("fr", ["de", "en"]).Should().Be("Anfänger");
        text.Resolve("fr", ["es", "en"]).Should().Be("Beginner");
    }

    [Fact]
    public void Resolve_ignores_blank_entries_in_the_chain()
    {
        var text = LocalizedText.From(("en", "Beginner"));

        // A tenant with no default locale set yields a chain with a hole in it;
        // the hole must not short-circuit the rest.
        text.Resolve("fr", ["", "en"]).Should().Be("Beginner");
    }

    [Fact]
    public void Resolve_returns_an_authored_value_rather_than_an_empty_string()
    {
        var text = LocalizedText.From(("tr", "Başlangıç"));

        // A label is not an optional field: rendering nothing is worse than
        // rendering the only translation the tenant wrote.
        text.Resolve("ja", ["ko"]).Should().Be("Başlangıç");
    }

    [Fact]
    public void Json_round_trips_through_the_column_form()
    {
        var text = LocalizedText.From(("en", "Beginner"), ("tr", "Başlangıç"));

        LocalizedText.FromJson(text.ToJson()).Should().Be(text);
    }

    [Fact]
    public void FromJson_re_applies_every_rule_rather_than_trusting_the_column()
    {
        // A hand-written UPDATE, a bad migration or a future importer can put any
        // well-formed object here. Materializing it unchecked is how a bad row
        // becomes a render that silently drops a label.
        var blankValue = () => LocalizedText.FromJson("""{"en":""}""");
        var badTag = () => LocalizedText.FromJson("""{"not a tag":"x"}""");
        var empty = () => LocalizedText.FromJson("{}");
        var notAnObject = () => LocalizedText.FromJson("""["en","Beginner"]""");
        var notJson = () => LocalizedText.FromJson("{");

        blankValue.Should().Throw<ArgumentException>();
        badTag.Should().Throw<ArgumentException>();
        empty.Should().Throw<ArgumentException>();
        notAnObject.Should().Throw<ArgumentException>();
        notJson.Should().Throw<ArgumentException>().WithMessage("*not well-formed JSON*");
    }

    [Fact]
    public void FromJson_refuses_a_json_null()
    {
        var act = () => LocalizedText.FromJson("null");

        act.Should().Throw<ArgumentException>().WithMessage("*not null*");
    }

    [Fact]
    public void Equality_is_by_value_and_independent_of_authoring_order()
    {
        var a = LocalizedText.From(("en", "Beginner"), ("tr", "Başlangıç"));
        var b = LocalizedText.From(("tr", "Başlangıç"), ("EN", "Beginner"));

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.Should().NotBe(LocalizedText.From(("en", "Novice")));
    }

    [Fact]
    public void Has_reports_only_what_was_authored()
    {
        var text = LocalizedText.From(("en-US", "Color"));

        text.Has("en-us").Should().BeTrue();
        text.Has("en").Should().BeFalse("the language subtag is a fallback, not a translation");
        text.Has("  ").Should().BeFalse();
    }
}
