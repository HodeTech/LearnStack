using FluentAssertions;
using LearnStack.SharedKernel.Tenancy;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel;

/// <summary>
/// The normalisation
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Effective host and the trusted hop</see> specifies — the sole producer of
/// both the <c>platform_host_to_tenant</c> lookup key and
/// <c>app.resolving_host</c>.
/// </summary>
public sealed class EffectiveHostTests
{
    [Theory]
    [InlineData("english.example.com", "english.example.com")]
    [InlineData("ENGLISH.Example.Com", "english.example.com")]
    [InlineData("english.example.com:8443", "english.example.com")]
    [InlineData("ENGLISH.Example.Com.:8443", "english.example.com")]
    [InlineData("english.example.com.", "english.example.com")]
    public void A_Host_Normalises_To_One_Form(string raw, string expected) =>
        EffectiveHost.Normalize(raw).Should().Be(expected);

    [Fact]
    public void A_Unicode_Host_Is_Stored_As_A_Labels()
    {
        // ADR-0036's own example. The row holds the A-label, so the lookup key
        // has to be produced the same way or a tenant's own domain misses.
        EffectiveHost.Normalize("türkçe.example.com")
            .Should().Be("xn--trke-2oa7j.example.com");
    }

    [Fact]
    public void Lowering_Is_Invariant_Not_Cultural()
    {
        // Under tr-TR, ToLower() maps 'I' to 'ı', which would turn every host
        // containing a capital I into a key matching no row. Asserted against
        // the culture this team actually runs.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("tr-TR");

            EffectiveHost.Normalize("ISTANBUL.example.com")
                .Should().Be("istanbul.example.com");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData("", "empty")]
    [InlineData("   ", "whitespace only")]
    [InlineData("a b.example.com", "internal whitespace")]
    [InlineData("example.com/path", "a path")]
    [InlineData("user@example.com", "userinfo")]
    [InlineData("ex%41mple.com", "a percent-escape, which gives one host two spellings")]
    [InlineData("example.com\\x", "a backslash")]
    [InlineData("example.com?q=1", "a query")]
    [InlineData("example.com#f", "a fragment")]
    [InlineData("[::1]", "an IPv6 literal")]
    [InlineData("[::1]:5080", "a bracketed IPv6 literal with a port")]
    [InlineData("1.2.3.4", "an IPv4 literal")]
    [InlineData("1.2.3.4:443", "an IPv4 literal with a port")]
    [InlineData("example.com..", "two trailing dots")]
    [InlineData(".", "a bare dot")]
    [InlineData("a..b.com", "an empty label")]
    [InlineData("example.com:", "a colon with no port")]
    [InlineData("example.com:80x", "a non-numeric port")]
    [InlineData(":8443", "a port with no host")]
    [InlineData("xn--", "the IDNA edge that makes GetAscii throw")]
    [InlineData("xn--a", "another IDNA edge")]
    [InlineData("a.xn--.b", "a third IDNA edge")]
    public void An_Input_That_Cannot_Name_A_Host_Returns_Null(string? raw, string why) =>
        EffectiveHost.Normalize(raw).Should().BeNull(why);

    [Fact]
    public void A_Host_At_The_DNS_Limit_Is_Accepted_And_One_Over_Is_Not()
    {
        // Built from 63-character labels, because a single label cannot exceed
        // 63 either — a 249-character label plus ".com" is 253 long and still
        // invalid, which is what a first version of this test got wrong.
        var label = new string('a', 63);
        var atLimit = $"{label}.{label}.{label}.{new string('a', 61)}";
        atLimit.Length.Should().Be(EffectiveHost.MaxLength);

        EffectiveHost.Normalize(atLimit).Should().Be(atLimit);
        EffectiveHost.Normalize(atLimit + "a").Should().BeNull();
    }

    [Fact]
    public void A_Label_Over_63_Characters_Is_Refused()
    {
        EffectiveHost.Normalize(new string('a', 64) + ".com").Should().BeNull();
    }

    [Fact]
    public void A_NUL_Is_Refused()
    {
        // Not a theory row: xUnit's InlineData mangles an embedded NUL, and a
        // string that truncates in whatever reads it next is the one input
        // most worth pinning.
        EffectiveHost.Normalize("example.com\0evil").Should().BeNull();
    }

    [Theory]
    [InlineData("xn--trke-2oa7j.example.com")]
    [InlineData("a-b.example.com")]
    [InlineData("sub.branch-istanbul.example.edu")]
    public void An_Already_Normal_Host_Is_Returned_Unchanged(string raw) =>
        EffectiveHost.Normalize(raw).Should().Be(raw);

    [Theory]
    // Fullwidth forms. GetAscii applies a compatibility mapping, so each of
    // these arrives as the literal ASCII character AFTER the raw-input scan has
    // already run — U+FF0F as '/', U+FF20 as '@', U+FF05 as '%'.
    [InlineData("example\uFF0Fcom", "fullwidth solidus becomes '/'")]
    [InlineData("example\uFF20com", "fullwidth commercial at becomes '@'")]
    [InlineData("example\uFF05com", "fullwidth percent becomes '%'")]
    [InlineData("example\uFF3Ccom", "fullwidth less-than")]
    // Plain ASCII that was never on the raw denylist at all.
    [InlineData("example;com", "a semicolon")]
    [InlineData("example'com", "an apostrophe")]
    [InlineData("example\"com", "a double quote")]
    [InlineData("example,com", "a comma")]
    [InlineData("example(com", "a parenthesis")]
    [InlineData("-example.com", "a label starting with a hyphen")]
    [InlineData("example-.com", "a label ending with a hyphen")]
    public void No_Non_LDH_Character_Survives_Into_The_Output(string raw, string why)
    {
        // The output is what becomes a platform_host_to_tenant lookup key and
        // the app.resolving_host session variable, so validating the INPUT was
        // never enough — the mapping happens in between.
        EffectiveHost.Normalize(raw).Should().BeNull(why);
    }

    [Theory]
    [InlineData("example\uFF0Fcom")]
    [InlineData("example\uFF20com")]
    [InlineData("example;com")]
    public void A_Confusable_Cannot_Break_Idempotence(string raw)
    {
        // Before the output check, the first call returned "example/com" and
        // the second returned null — the raw scan finally saw the character the
        // first call had produced. A cached key and a fresh one would then
        // disagree.
        var once = EffectiveHost.Normalize(raw);
        EffectiveHost.Normalize(once).Should().Be(once);
    }

    [Fact]
    public void Normalisation_Is_Idempotent()
    {
        // The value is written to app.resolving_host and used as a lookup key;
        // if normalising twice differed, a cached key and a fresh one could
        // disagree.
        foreach (var raw in new[]
                 {
                     "ENGLISH.Example.Com.:8443", "türkçe.example.com", "a-b.example.com",
                 })
        {
            var once = EffectiveHost.Normalize(raw);
            EffectiveHost.Normalize(once).Should().Be(once, "input was '{0}'", raw);
        }
    }

    [Fact]
    public void Nothing_Throws_For_Any_Input()
    {
        // The property that matters most: this runs on an anonymous request,
        // so an exception is a remote client writing into the error tracker.
        var hostile = new[]
        {
            "�", "\uD800", "..", "-", "--", "xn--0", "a" + new string('.', 60) + "b",
            new string('́', 100), "example.com:99999999999999999999",
            "‎.example.com", "ex\u0000ample.com",
        };

        foreach (var raw in hostile)
        {
            var act = () => EffectiveHost.Normalize(raw);
            act.Should().NotThrow("input was '{0}'", raw);
        }
    }
}
