using LearnStack.SharedKernel.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LearnStack.Infrastructure.DataProtection;

/// <summary>Reviewed property declarations, separate from snapshot redaction markers.</summary>
public static class PersonalDataClassificationExtensions
{
    public const string CategoryAnnotation = "LearnStack:PersonalData:Category";
    public const string PurposeAnnotation = "LearnStack:PersonalData:Purpose";
    public const string ValidatedModelAnnotation = "LearnStack:PersonalData:Validated";
    public const int MaximumPurposeLength = 256;

    public static PropertyBuilder<TProperty> HasPersonalData<TProperty>(
        this PropertyBuilder<TProperty> builder, PersonalDataCategory category, string purpose)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Declare(builder.Metadata, category, purpose);
        return builder;
    }

    public static PropertyBuilder HasPersonalData(
        this PropertyBuilder builder, PersonalDataCategory category, string purpose)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Declare(builder.Metadata, category, purpose);
        return builder;
    }

    /// <summary>Classifies existing scalar mappings; a misspelling never creates a shadow property.</summary>
    public static EntityTypeBuilder<TEntity> DeclarePersonalData<TEntity>(
        this EntityTypeBuilder<TEntity> builder, PersonalDataCategory category,
        string purpose, params string[] propertyNames) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(propertyNames);
        if (propertyNames.Length == 0) Refuse();
        var properties = propertyNames.Select(name => builder.Metadata.FindProperty(name)
            ?? throw new InvalidOperationException(PersonalDataModelCensus.FailureMessage)).ToArray();
        // Validate the whole declaration before changing any property.
        foreach (var property in properties) ValidateDeclaration(property, category, purpose);
        foreach (var property in properties) SetDeclaration(property, category, purpose);
        return builder;
    }

    /// <summary>Opts this context into completeness validation before its model can be used.</summary>
    public static ModelConfigurationBuilder UsePersonalDataClassification(this ModelConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Conventions.Add(_ => new PersonalDataClassificationConvention());
        return builder;
    }

    internal static bool IsValidPurpose(object? purpose) =>
        purpose is string text && !string.IsNullOrWhiteSpace(text) && text.Length <= MaximumPurposeLength;

    private static void Declare(IMutableProperty property, PersonalDataCategory category, string purpose)
    {
        ValidateDeclaration(property, category, purpose);
        SetDeclaration(property, category, purpose);
    }

    private static void ValidateDeclaration(IMutableProperty property, PersonalDataCategory category, string purpose)
    {
        if (!Enum.IsDefined(category) || !IsValidPurpose(purpose)) Refuse();
        var previousCategory = property.FindAnnotation(CategoryAnnotation);
        var previousPurpose = property.FindAnnotation(PurposeAnnotation);
        if ((previousCategory is not null || previousPurpose is not null)
            && (!Equals(previousCategory?.Value, category)
                || !string.Equals(previousPurpose?.Value as string, purpose, StringComparison.Ordinal)))
            Refuse();
    }

    private static void SetDeclaration(IMutableProperty property, PersonalDataCategory category, string purpose)
    {
        property.SetAnnotation(CategoryAnnotation, category);
        property.SetAnnotation(PurposeAnnotation, purpose);
    }

    private static void Refuse() => throw new InvalidOperationException(PersonalDataModelCensus.FailureMessage);
}
