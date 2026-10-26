using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Identity.Domain;

public static class IdentityFailures
{
    public static Error Refused() => new(LocalizedMessage.Of("lockey_identity_foundation_refused"));
    internal static Result<None> BusinessRule() => Result<None>.Fail(
        new Error(LocalizedMessage.Of("lockey_business_rule_violation")));
}
