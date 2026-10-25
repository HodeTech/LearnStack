using LearnStack.SharedKernel.DataProtection;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LearnStack.Infrastructure.DataProtection;

/// <summary>A diagnostic containing model names and a closed issue code, never property values.</summary>
public sealed record PersonalDataClassificationIssue(string EntityType, string Property, string Code);

/// <summary>Pure completeness validation of actual EF scalar metadata, including inherited and shadow properties.</summary>
public static class PersonalDataModelCensus
{
    public const string FailureMessage =
        "Personal-data classification is missing, invalid or conflicting. Fix: explicitly classify every mapped property with a valid category and purpose.";

    public static IReadOnlyList<PersonalDataClassificationIssue> Inspect(IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var issues = new List<PersonalDataClassificationIssue>();
        foreach (var entity in model.GetEntityTypes().OrderBy(type => type.Name, StringComparer.Ordinal))
            InspectType(entity, entity.Name, string.Empty, issues);
        return issues;
    }

    private static void InspectType(IReadOnlyTypeBase type, string entityName, string prefix,
        List<PersonalDataClassificationIssue> issues)
    {
        foreach (var property in type.GetProperties().OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            var category = property.FindAnnotation(PersonalDataClassificationExtensions.CategoryAnnotation);
            var purpose = property.FindAnnotation(PersonalDataClassificationExtensions.PurposeAnnotation);
            if (category is null)
                issues.Add(new(entityName, prefix + property.Name, "missing_category"));
            else if (category.Value is not PersonalDataCategory value || !Enum.IsDefined(value))
                issues.Add(new(entityName, prefix + property.Name, "invalid_category"));
            if (purpose is null)
                issues.Add(new(entityName, prefix + property.Name, "missing_purpose"));
            else if (!PersonalDataClassificationExtensions.IsValidPurpose(purpose.Value))
                issues.Add(new(entityName, prefix + property.Name, "invalid_purpose"));
        }

        foreach (var complex in type.GetComplexProperties().OrderBy(property => property.Name, StringComparer.Ordinal))
            InspectType(complex.ComplexType, entityName, prefix + complex.Name + ".", issues);
    }
}
