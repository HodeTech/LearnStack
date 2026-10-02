using FluentAssertions;
using FluentValidation;
using LearnStack.Modules.Education.Application;
using LearnStack.Modules.Education.Application.Contracts.Courses;
using LearnStack.Modules.Education.Application.Contracts.Lessons;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Modules.Education;

public sealed class EducationWriterValidationTests : IDisposable
{
    private readonly ServiceProvider _provider = Build();

    public void Dispose() => _provider.Dispose();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Public")]
    [InlineData("unknown")]
    public void Course_creation_requires_explicit_closed_policy(string? policy)
    {
        Validator<CreateCourseCommand>().Validate(new(Guid.CreateVersion7(), "course", policy)).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("public")]
    [InlineData("enrollment_required")]
    public void Both_explicit_policies_and_complete_optional_pins_are_admitted(string policy)
    {
        var validator = Validator<CreateCourseCommand>();
        validator.Validate(new(Guid.CreateVersion7(), "course", policy)).IsValid.Should().BeTrue();
        validator.Validate(new(Guid.CreateVersion7(), "course", policy, "level", 1, "basic")).IsValid.Should().BeTrue();
        validator.Validate(new(Guid.CreateVersion7(), "course", policy, "level")).IsValid.Should().BeFalse();
        validator.Validate(new(Guid.CreateVersion7(), "course", policy, "level", 0, "basic")).IsValid.Should().BeFalse();
        validator.Validate(new(Guid.CreateVersion7(), "course", policy, "LEVEL", 1, "basic")).IsValid.Should().BeFalse();
        validator.Validate(new(Guid.Empty, "course", policy)).IsValid.Should().BeFalse();
        validator.Validate(new(Guid.CreateVersion7(), Guid.CreateVersion7().ToString(), policy)).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(-1L, false)]
    [InlineData(0L, true)]
    [InlineData(42L, true)]
    public void Every_existing_root_write_requires_an_exact_nonnegative_version(long? version, bool valid)
    {
        var id = Guid.CreateVersion7();
        Validator<PublishCourseCommand>().Validate(new(id, version)).IsValid.Should().Be(valid);
        Validator<PublishLessonCommand>().Validate(new(id, version)).IsValid.Should().Be(valid);
        Validator<AddCourseTranslationCommand>().Validate(new(id, version, "EN-us", "Title", null, "course")).IsValid.Should().Be(valid);
        Validator<AddLessonTranslationCommand>().Validate(new(id, version, "en", "Title", "lesson", "{}")).IsValid.Should().Be(valid);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{")]
    [InlineData("{\"text\":\"\\u0000\"}")]
    public void A_lesson_translation_body_must_be_a_storable_json_object(string body)
    {
        Validator<AddLessonTranslationCommand>().Validate(new(Guid.CreateVersion7(), 0, "en", "Title", "lesson", body))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Text_locale_and_pin_guards_refuse_malformed_application_inputs()
    {
        var id = Guid.CreateVersion7();
        Validator<CreateLessonCommand>().Validate(new(id, Guid.Empty, 0, "card", 1)).IsValid.Should().BeFalse();
        Validator<CreateLessonCommand>().Validate(new(id, id, -1, "card", 1)).IsValid.Should().BeFalse();
        Validator<CreateLessonCommand>().Validate(new(id, id, 0, "card", 0)).IsValid.Should().BeFalse();
        Validator<CreateLessonCommand>().Validate(new(id, id, 0, "Card", 1)).IsValid.Should().BeFalse();
        var validator = Validator<AddCourseTranslationCommand>();
        validator.Validate(new(id, 0, "en_US", "Title", null, "course")).IsValid.Should().BeFalse();
        validator.Validate(new(id, 0, "en", " ", null, "course")).IsValid.Should().BeFalse();
        validator.Validate(new(id, 0, "en", "Title", "\0", "course")).IsValid.Should().BeFalse();
        validator.Validate(new(id, 0, "en", "Title", null, "UPPER")).IsValid.Should().BeFalse();
        Validator<PublishCourseCommand>().Validate(new(Guid.Empty, 0)).IsValid.Should().BeFalse();
        Validator<PublishLessonCommand>().Validate(new(Guid.Empty, 0)).IsValid.Should().BeFalse();
    }

    private IValidator<T> Validator<T>() => _provider.GetRequiredService<IValidator<T>>();

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddValidatorsFromAssembly(typeof(AssemblyMarker).Assembly, includeInternalTypes: true);
        return services.BuildServiceProvider();
    }
}
