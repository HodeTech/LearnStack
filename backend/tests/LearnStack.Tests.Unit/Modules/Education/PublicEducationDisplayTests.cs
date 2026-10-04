using System.Collections.Immutable;
using FluentAssertions;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.Modules.Education.Application.Abstractions;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Education.Application.PublicReads;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnStack.Tests.Unit.Modules.Education;

public sealed class PublicEducationDisplayTests
{
    private static readonly DefinitionRevision Pin = new("content", 7);
    private static readonly PublicLocaleContext Locale = new("tr-TR", "en", ["tr-TR", "en"]);
    private static readonly PublicLessonReadRow Lesson = new(CourseId.From(Guid.NewGuid()), "course", "Course", LessonId.From(Guid.NewGuid()), "lesson", "Lesson", Pin.Key, Pin.SchemaVersion, "{}");
    private static readonly ImmutableArray<TextCardDisplayField> Fields =
        [new("required", new ResolvedLocalizedText("Required", "en"), true), new("optional", new ResolvedLocalizedText("İsteğe bağlı", "tr-TR"))];

    [Fact]
    public async Task Display_preserves_descriptor_order_actual_label_locale_and_plain_strings_and_omits_unselected_data()
    {
        var reader = new Reader(Fields);
        var display = new PublicEducationDisplay(reader, NullLogger<PublicEducationDisplay>.Instance);
        var content = (await display.ContentAsync(Lesson with { Body = """{"secret":"never project","optional":"<script>https://example.org</script>","required":""}""" }, Locale, CancellationToken.None))
            .Should().BeOfType<PublicReadyContent>().Subject;
        content.Fields.Select(field => field.Name).Should().Equal("required", "optional");
        content.Fields[0].Value.Should().BeEmpty("read projection does not repeat write-time minLength validation");
        content.Fields[0].Label.Locale.Should().Be("en");
        content.Fields[1].Label.Locale.Should().Be("tr-TR");
        content.Fields[1].Value.Should().Be("<script>https://example.org</script>", "P02d-6 owns safe text rendering");
        reader.Request!.ContentTypes.Should().Equal(Pin);
        reader.Request.RequestedLocale.Should().Be("tr-TR");
        reader.Request.TenantDefaultLocale.Should().Be("en");
        var withoutOptional = (await display.ContentAsync(Lesson with { Body = """{"required":"value"}""" }, Locale, CancellationToken.None))
            .Should().BeOfType<PublicReadyContent>().Subject;
        withoutOptional.Fields.Should().ContainSingle().Which.Name.Should().Be("required");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"required\":42}")]
    [InlineData("{\"required\":null}")]
    [InlineData("{\"required\":\"ok\",\"optional\":[]}")]
    public async Task Missing_required_nonobject_or_nonstring_selected_content_is_wholly_unavailable(string body)
    {
        var display = new PublicEducationDisplay(new Reader(Fields), NullLogger<PublicEducationDisplay>.Instance);
        (await display.ContentAsync(Lesson with { Body = body }, Locale, CancellationToken.None)).Should().BeOfType<PublicUnavailableContent>();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unsupported")]
    [InlineData("empty")]
    public async Task Exact_pin_missing_unsupported_renderer_and_zero_descriptors_never_rebind(string mode)
    {
        var reader = new Reader(mode == "empty" ? [] : Fields, mode);
        var display = new PublicEducationDisplay(reader, NullLogger<PublicEducationDisplay>.Instance);
        (await display.ContentAsync(Lesson with { Body = """{"required":"value"}""" }, Locale, CancellationToken.None)).Should().BeOfType<PublicUnavailableContent>();
        reader.Request!.ContentTypes.Should().Equal(Pin);
    }

    [Fact]
    public async Task Level_absence_is_null_missing_pin_is_unavailable_and_distinct_pins_are_one_batch()
    {
        var reader = new Reader(Fields);
        var display = new PublicEducationDisplay(reader, NullLogger<PublicEducationDisplay>.Instance);
        var row = new PublicCourseReadRow(Lesson.CourseId, DateTimeOffset.UtcNow, "course", "Course", null, CourseContentAccess.Public, null, null, null);
        (await display.SummariesAsync([row], Locale, CancellationToken.None)).Single().Level.Should().BeNull();
        reader.Request.Should().BeNull("no pin means no definition call");
        var summaries = await display.SummariesAsync([row with { TaxonomyKey = "levels", TaxonomyVersion = 9, BandKey = "first" },
            row with { TaxonomyKey = "levels", TaxonomyVersion = 9, BandKey = "second" }], Locale, CancellationToken.None);
        summaries.Should().OnlyContain(item => item.Level!.State == PublicDisplayState.Unavailable && item.Level.Label == null && item.Summary == null);
        reader.Request!.Taxonomies.Should().Equal(new DefinitionRevision("levels", 9));
        reader.Calls.Should().Be(1);
    }

    private sealed class Reader(ImmutableArray<TextCardDisplayField> fields, string mode = "ready") : ICustomizationDefinitionProjectionReader
    {
        public DefinitionProjectionRequest? Request { get; private set; }
        public int Calls { get; private set; }
        public Task<Result<DefinitionProjection>> ReadAsync(DefinitionProjectionRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            Calls++;
            var types = ImmutableDictionary<DefinitionRevision, ContentTypeDisplayDefinition>.Empty;
            if (mode != "missing") types = types.Add(Pin, new ContentTypeDisplayDefinition(Guid.NewGuid(), Pin, DefinitionStatus.Deprecated,
                new ResolvedLocalizedText("Content", "en"), mode == "unsupported" ? "other" : "default-card", fields));
            return Task.FromResult(Result.Ok(new DefinitionProjection(1, types, ImmutableDictionary<DefinitionRevision, TaxonomyDisplayDefinition>.Empty, [], [])));
        }
    }
}
