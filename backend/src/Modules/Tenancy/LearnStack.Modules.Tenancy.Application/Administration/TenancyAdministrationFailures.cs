using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Tenancy.Application.Administration;

internal static class TenancyAdministrationFailures
{
    internal static Result<T> Code<T>(string key) => Result<T>.Fail(new Error(LocalizedMessage.Of(key)));
}
