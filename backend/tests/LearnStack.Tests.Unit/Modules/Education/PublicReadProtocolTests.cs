using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Modules.Education.Application.Abstractions;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Education.Application.PublicReads;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Xunit;

namespace LearnStack.Tests.Unit.Modules.Education;

public sealed class PublicReadProtocolTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("01930000-0000-7000-8000-000000000001"));
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.Parse("01930000-0000-7000-8000-0000000000a1"));
    private static readonly CourseId Course = CourseId.From(Guid.Parse("01930000-0000-7000-8000-000000000e11"));
    private static readonly LessonId Lesson = LessonId.From(Guid.Parse("01930000-0000-7000-8000-000000000e21"));
    private static readonly string Digest = new('a', 64);
    private static readonly string Scope = PublicCursorCodec.Scope(Tenant, null, Digest, "en", false);
    private static readonly CatalogContinuation Anchor = new(DateTimeOffset.Parse("2026-10-03T12:34:56.123456Z", System.Globalization.CultureInfo.InvariantCulture), Course);

    [Fact]
    public void Cursor_round_trips_microseconds_and_postgres_seek_identifiers_without_anchor_lookup()
    {
        var raw = PublicCursorCodec.EncodeCatalog(Scope, Anchor);
        raw.Should().NotContain("=");
        PublicCursorCodec.TryCatalog(raw, Scope, out var parsed).Should().BeTrue();
        parsed.Should().Be(Anchor);
        var outlineScope = PublicCursorCodec.Scope(Tenant, Organization, Digest, "en", true);
        var outline = new OutlineContinuation(Course, int.MaxValue, Lesson);
        PublicCursorCodec.TryOutline(PublicCursorCodec.EncodeOutline(outlineScope, outline), outlineScope, out var child).Should().BeTrue();
        child.Should().Be(outline);
        PublicCursorCodec.TryCatalog(raw, outlineScope, out _).Should().BeFalse();
        PublicCursorCodec.TryOutline(raw, Scope, out _).Should().BeFalse();
        // Valid seek positions are not credentials and need not name a stored row.
        var altered = Anchor with { Id = CourseId.From(Guid.NewGuid()), CreatedAt = Anchor.CreatedAt.AddDays(1) };
        PublicCursorCodec.TryCatalog(PublicCursorCodec.EncodeCatalog(Scope, altered), Scope, out var arbitrary).Should().BeTrue();
        arbitrary.Should().Be(altered);
    }

    [Fact]
    public void Scope_binds_host_tenant_organization_locale_endpoint_and_order()
    {
        var scopes = new[] { Scope,
            PublicCursorCodec.Scope(TenantId.From(Guid.NewGuid()), null, Digest, "en", false),
            PublicCursorCodec.Scope(Tenant, Organization, Digest, "en", false),
            PublicCursorCodec.Scope(Tenant, null, new string('b', 64), "en", false),
            PublicCursorCodec.Scope(Tenant, null, Digest, "tr-TR", false),
            PublicCursorCodec.Scope(Tenant, null, Digest, "en", true) };
        scopes.Distinct(StringComparer.Ordinal).Should().HaveCount(scopes.Length);
        var narrowed = TenantContextFactory.Create(new TenantResolutionAttempt
        {
            HostTenantId = Tenant,
            ClaimTenantId = Tenant,
            ClaimOrganizationId = Organization,
            HasValidatedPrincipal = true,
            UserId = UserId.From(Guid.NewGuid()),
            MembershipCovers = true,
            ClaimedOrganizationBelongsToTenant = true,
        }).Value!;
        PublicReadValidation.Scope(narrowed, new PublicReadInput("en", null, "1", Digest, []), "en", false).Should().Be(Scope,
            "cursor binding follows host provenance rather than widening or replacing claim scope");
        PublicReadValidation.Scope(narrowed, new PublicReadInput("en", null, "100", Digest, []), "en", false).Should().Be(Scope);
    }

    [Theory]
    [InlineData("v", "\"1\"")]
    [InlineData("v", "2")]
    [InlineData("v", "null")]
    [InlineData("scope", "42")]
    [InlineData("scope", "\"ABC\"")]
    [InlineData("createdAt", "\"2026-10-03T12:34:56Z\"")]
    [InlineData("createdAt", "\"2026-10-03T12:34:56.123456+00:00\"")]
    [InlineData("createdAt", "\"2026-02-30T12:34:56.123456Z\"")]
    [InlineData("id", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("id", "\"01930000000070008000000000000e11\"")]
    [InlineData("id", "\"01930000-0000-7000-8000-000000000E11\"")]
    [InlineData("id", "[]")]
    public void Malformed_catalog_members_refuse_without_throwing(string member, string replacement)
    {
        var payload = Decode(PublicCursorCodec.EncodeCatalog(Scope, Anchor));
        payload[member] = JsonNode.Parse(replacement);
        PublicCursorCodec.TryCatalog(Encode(payload.ToJsonString()), Scope, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("0.5")]
    [InlineData("\"1\"")]
    [InlineData("null")]
    public void Malformed_outline_sort_refuses_without_throwing(string sort)
    {
        var scope = PublicCursorCodec.Scope(Tenant, null, Digest, "en", true);
        var payload = Decode(PublicCursorCodec.EncodeOutline(scope, new OutlineContinuation(Course, 0, Lesson)));
        payload["sort"] = JsonNode.Parse(sort);
        PublicCursorCodec.TryOutline(Encode(payload.ToJsonString()), scope, out _).Should().BeFalse();
    }

    [Fact]
    public void Cursor_refuses_missing_duplicate_extra_deep_oversized_and_noncanonical_envelopes()
    {
        var raw = PublicCursorCodec.EncodeCatalog(Scope, Anchor);
        var payload = Decode(raw);
        payload.Remove("id");
        PublicCursorCodec.TryCatalog(Encode(payload.ToJsonString()), Scope, out _).Should().BeFalse();
        payload = Decode(raw);
        payload["tenant"] = Tenant.Value.ToString("D");
        PublicCursorCodec.TryCatalog(Encode(payload.ToJsonString()), Scope, out _).Should().BeFalse();
        var json = Decode(raw).ToJsonString();
        foreach (var invalid in new[] { "", "a", raw + "=", raw + "+", new string('a', 1025),
                     Encode("[]"), Encode("{\"v\":[[[[[1]]]]]}"), Encode(json.Insert(1, "\"v\":1,")), Encode(json + new string(' ', 769)),
                     Convert.ToBase64String(new byte[] { 0xff }).TrimEnd('=').Replace('+', '-').Replace('/', '_') })
            PublicCursorCodec.TryCatalog(invalid, Scope, out _).Should().BeFalse();
    }

    [Fact]
    public void Both_cursors_refuse_invalid_utf8_and_unpaired_surrogates_in_names_and_values()
    {
        var outline = PublicCursorCodec.EncodeOutline(Scope, new OutlineContinuation(Course, 0, Lesson));
        foreach (var (raw, isOutline) in new[] { (PublicCursorCodec.EncodeCatalog(Scope, Anchor), false), (outline, true) })
        {
            var payload = Decode(raw);
            var json = payload.ToJsonString();
            var invalidUtf8 = Encoding.UTF8.GetBytes(json);
            invalidUtf8[json.IndexOf(Scope, StringComparison.Ordinal)] = 0xff;
            var invalidName = Encoding.UTF8.GetBytes(json);
            invalidName[json.IndexOf("\"v\"", StringComparison.Ordinal) + 1] = 0xff;
            var invalid = new List<string> { Convert.ToBase64String(invalidUtf8).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                Convert.ToBase64String(invalidName).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                Encode(json.Replace("\"v\"", "\"\\uD800\"", StringComparison.Ordinal)) };
            foreach (var member in payload.Where(member => member.Value is JsonValue value && value.TryGetValue<string>(out _)))
                invalid.Add(Encode(json.Replace($"\"{member.Key}\":{JsonSerializer.Serialize(member.Value!.GetValue<string>())}",
                    $"\"{member.Key}\":\"\\uD800\"", StringComparison.Ordinal)));
            foreach (var token in invalid)
                if (isOutline) PublicCursorCodec.TryOutline(token, null, out _).Should().BeFalse();
                else PublicCursorCodec.TryCatalog(token, null, out _).Should().BeFalse();
            var escapedFirst = "\\u" + ((int)Scope[0]).ToString("x4", System.Globalization.CultureInfo.InvariantCulture);
            var validEscaping = Encode(json.Replace(Scope, escapedFirst + Scope[1..], StringComparison.Ordinal));
            if (isOutline) PublicCursorCodec.TryOutline(validEscaping, Scope, out _).Should().BeTrue();
            else PublicCursorCodec.TryCatalog(validEscaping, Scope, out _).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(null, 20, true)]
    [InlineData("1", 1, true)]
    [InlineData("0002", 2, true)]
    [InlineData("101", 100, true)]
    [InlineData("999999999999999999999", 100, true)]
    [InlineData("0", 0, false)]
    [InlineData("", 0, false)]
    [InlineData("-1", 0, false)]
    [InlineData(" 1", 0, false)]
    [InlineData("1.5", 0, false)]
    [InlineData("١", 0, false)]
    public void Limit_is_positive_ascii_and_clamped_without_integer_overflow(string? raw, int expected, bool valid)
    {
        PublicReadValidation.TryLimit(raw, out var limit).Should().Be(valid);
        if (valid) limit.Should().Be(expected);
    }

    [Fact]
    public void Validators_canonicalize_locale_but_refuse_whitespace_repeats_uuid_slugs_and_unknown_fields()
    {
        var context = TenantContextFactory.Create(new TenantResolutionAttempt { HostTenantId = Tenant }).Value!;
        var input = new PublicReadInput("TR-tr", null, null, Digest, []);
        PublicReadValidation.TryLocale(input.Locale, out var locale).Should().BeTrue();
        locale.Should().Be("tr-TR");
        var validator = new GetPublicCoursesQueryValidator(context);
        validator.Validate(new GetPublicCoursesQuery(input)).IsValid.Should().BeTrue();
        foreach (var invalid in new string?[] { null, "", " en", "en ", "en_US", new string('a', 36) })
            validator.Validate(new GetPublicCoursesQuery(input with { Locale = invalid })).Errors.Should().Contain(error => error.PropertyName == "locale");
        validator.Validate(new GetPublicCoursesQuery(input with { InvalidFields = ["locale", "cursor", "tenantId"] }))
            .Errors.Select(error => error.PropertyName).Should().BeEquivalentTo(["locale", "cursor", "query"]);
        var detail = new GetPublicCourseQueryValidator(context);
        foreach (var invalid in new[] { "UPPER", "-bad", "bad--slug", Course.Value.ToString("D"), Course.Value.ToString("N"), new string('a', 161) })
            detail.Validate(new GetPublicCourseQuery(invalid, input)).Errors.Should().Contain(error => error.PropertyName == "Slug");
    }

    private static JsonObject Decode(string raw) => JsonNode.Parse(Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/') + new string('=', (4 - raw.Length % 4) % 4)))!.AsObject();
    private static string Encode(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
