using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Education.Domain;

internal static class EducationFailures
{
    internal static Result<None> BusinessRule(string field, string reason) =>
        Result<None>.Fail(Field("lockey_business_rule_violation", field, reason));

    internal static Error Validation(string field, string reason) =>
        Field("lockey_validation_failed", field, reason);

    private static Error Field(string code, string field, string reason) =>
        new(new LocalizedMessage(code),
            new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
            {
                [field] = [new LocalizedMessage(reason)],
            });
}
