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
    public void Resolve_narrows_one_subtag_at_a_time_rather_than_jumping_to_the_language()
    {
        // Both scripts authored: a request for Traditional must not be answered in
        // Simplified. Jumping straight to the primary subtag did exactly that.
        var chinese = LocalizedText.From(("zh", "简体"), ("zh-Hant", "繁體"));
        chinese.Resolve("zh-Hant-TW").Should().Be("繁體");
        chinese.Resolve("zh-Hans-CN").Should().Be("简体", "zh-Hans is not authored, zh is");

        // And an intermediate tag is reached before the caller's chain is consulted.
        var english = LocalizedText.From(("de", "Deutsch"), ("en-US", "American"));
        english.Resolve("en-US-posix").Should().Be("American");
    }

    [Fact]
    public void Resolve_never_widens_towards_a_region_the_caller_did_not_ask_for()
    {
        // Built so that widening and the last resort give DIFFERENT answers:
        // ordinal-first is `aa`, so a widened lookup returns "US" and the
        // documented behaviour returns "AA". An earlier version of this test used a
        // single-entry fixture where both branches returned the same string, and it
        // passed with the guard deleted.
        var text = LocalizedText.From(("aa", "AA"), ("en-US", "US"));

        text.Resolve("en").Should().Be("AA",
            "`en-US` is not an answer to `en`; narrowing is documented, widening is not");
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
    public void Resolve_steps_over_a_blank_chain_entry_instead_of_stopping_at_it()
    {
        // Two entries, so stepping over the hole and stopping at it give different
        // answers: `aa` is ordinal-first and would win if the walk broke early.
        var text = LocalizedText.From(("aa", "AA"), ("en", "Beginner"));

        // A tenant with no default locale set yields a chain with a hole in it.
        text.Resolve("fr", ["", "en"]).Should().Be("Beginner");
    }

    [Fact]
    public void Resolve_canonicalizes_the_requested_tag_before_looking_it_up()
    {
        // `aa` is ordinal-first, so a lookup that skipped canonicalization would
        // miss `en-US`, exhaust the chain and answer "AA".
        var text = LocalizedText.From(("aa", "AA"), ("en-US", "US"));

        text.Resolve("EN-us").Should().Be("US");
        text.Resolve("en-us").Should().Be("US");
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

        // Same locales, different values — the comparison this test used to skip,
        // and the only one that exercises Equals' value half.
        var sameKeysOtherValues = LocalizedText.From(("en", "Novice"), ("tr", "Acemi"));
        a.Should().NotBe(sameKeysOtherValues);

        // Same values, one extra locale.
        a.Should().NotBe(
            LocalizedText.From(("en", "Beginner"), ("tr", "Başlangıç"), ("de", "Anfänger")));

        // `==` binds to the operator, not to reference identity. Without the
        // operator pair the next line fails while Should().Be(b) passes — which is
        // why it is asserted separately rather than trusted to follow.
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
        (a == sameKeysOtherValues).Should().BeFalse();

        LocalizedText? absent = null;
        (a == absent).Should().BeFalse();
        (absent == null).Should().BeTrue();
        (absent != a).Should().BeTrue();
    }

    [Fact]
    public void FromJson_refuses_a_duplicate_key_exactly_as_From_does()
    {
        // A dictionary deserializer keeps the last value silently. A reader that
        // accepts what the writer refuses is the gap the next importer walks
        // straight through.
        var act = () => LocalizedText.FromJson(DuplicateKeyJson);

        act.Should().Throw<ArgumentException>().WithMessage("*appears twice*");
    }

    [Fact]
    public void FromJson_names_the_locale_whose_value_is_not_a_string()
    {
        var act = () => LocalizedText.FromJson(NumberValueJson);

        act.Should().Throw<ArgumentException>().WithMessage("*'tr' holds Number*");
    }

    private const string DuplicateKeyJson = "{\"en\":\"Beginner\",\"en\":\"Novice\"}";

    private const string NumberValueJson = "{\"en\":\"Beginner\",\"tr\":42}";

    [Fact]
    public void A_value_at_exactly_the_mapped_width_is_accepted()
    {
        // The refusal was pinned one past the cap and the acceptance was not, so
        // `>` could become `>=`, or the constant could shrink, unnoticed. The at-cap
        // input is a fixed literal rather than the constant, so shrinking the
        // constant makes this input over-long and the test fails.
        var act = () => LocalizedText.From(("en", new string('x', 200)));

        act.Should().NotThrow();
        LocalizedText.MaxValueLength.Should().Be(200, "the mapped column width");
    }

    [Fact]
    public void Exactly_the_maximum_number_of_locales_is_accepted()
    {
        var pairs = Enumerable.Range(0, 50)
            .Select(i => ($"{(char)('a' + (i / 26))}{(char)('a' + (i % 26))}", "value"))
            .ToArray();

        var act = () => LocalizedText.From(pairs);

        act.Should().NotThrow();
        LocalizedText.MaxLocales.Should().Be(50);
    }

    [Fact]
    public void A_locale_at_exactly_the_tag_width_is_accepted()
    {
        // 35 characters, well-formed: a primary subtag plus variant subtags.
        var tag = "en-abcde123-fghij456-klmno789-pqrst";
        tag.Length.Should().Be(LocaleTag.MaxLength);

        var act = () => LocalizedText.From((tag, "value"));

        act.Should().NotThrow();
    }

    [Fact]
    public void Script_subtags_keep_BCP_47_title_case_rather_than_being_uppercased()
    {
        // `zh-HANS` and `zh-Hans` would be two entries naming one locale, which is
        // the collision canonicalization exists to prevent — so the script branch
        // has to Title-case rather than uppercase like the region branch does.
        var text = LocalizedText.From(("zh-hans-cn", "简体"), ("zh-hant", "繁體"));

        text.Locales.Should().BeEquivalentTo(["zh-Hans-CN", "zh-Hant"]);
        text.Resolve("ZH-HANS-CN").Should().Be("简体");
    }

    [Fact]
    public void Resolve_steps_over_a_null_chain_entry()
    {
        // The chain is built per request from the tenant's locales; a tenant with no
        // default yields a hole, and the hole can be null rather than empty. Without
        // the skip this is a NullReferenceException on a read path.
        var text = LocalizedText.From(("aa", "AA"), ("en", "Beginner"));

        text.Resolve("fr", [null!, "en"]).Should().Be("Beginner");
    }

    [Fact]
    public void Equality_compares_values_with_case()
    {
        // Two labels differing only in case are two labels. An ordinal-ignore-case
        // comparison would call them equal and a cache keyed on the pair would
        // serve one for the other.
        var lower = LocalizedText.From(("en", "beginner"));
        var upper = LocalizedText.From(("en", "Beginner"));

        lower.Should().NotBe(upper);
        (lower == upper).Should().BeFalse();
    }

    /// <param name="codeUnit">
    /// The offending UTF-16 code unit, as a number. Written out as a character in
    /// the attribute, xUnit's own data serialization rewrites the two surrogates to
    /// U+FFFD before the test sees them — so the case would pass while asserting
    /// nothing, which is how it first ran.
    /// </param>
    [Theory]
    [InlineData(0x0000)]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void A_label_a_jsonb_column_cannot_hold_is_refused(int codeUnit)
    {
        // The column is jsonb and ToJson is the trip into it. Measured on
        // PostgreSQL 18.6: a NUL is 22P05 on the insert. The unpaired surrogate is
        // the worse of the two — JsonSerializer rewrites it to U+FFFD, so the value
        // stored is quietly not the value submitted and nothing raises at all.
        var refusal = () => LocalizedText.From(("en", "a" + (char)codeUnit + "b"));

        refusal.Should().Throw<ArgumentException>().WithParameterName("values");
    }

    [Fact]
    public void A_label_with_a_paired_surrogate_is_a_label()
    {
        // The guard refuses UNPAIRED surrogates. An emoji is two chars and one
        // character, and a rule that counted chars would refuse every one of them.
        LocalizedText.From(("en", "Yoga \ud83e\uddd8")).Resolve("en")
            .Should().Be("Yoga \ud83e\uddd8");
    }

    [Fact]
    public void A_locale_tag_with_a_trailing_newline_is_not_a_locale_tag()
    {
        // `$` in .NET matches at the end of the input OR immediately before a final
        // newline, so "en\n" was well-formed — measured. A locale tag is a JSON
        // member name in every localized column and a segment of a fallback chain.
        var refusal = () => LocalizedText.From(("en\n", "Beginner"));

        refusal.Should().Throw<ArgumentException>().WithParameterName("values");
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
