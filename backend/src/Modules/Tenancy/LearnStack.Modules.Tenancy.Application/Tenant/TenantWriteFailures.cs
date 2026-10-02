using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Tenancy.Application.Tenant;

internal static class TenantWriteFailures
{
    internal static Result<T>? Scope<T>(ITenantContext context) => !context.IsResolved
        ? Code<T>("lockey_tenant_mismatch")
        : context.OrganizationId is not null ? Code<T>("lockey_resource_scope_violation") : null;

    internal static Result<T> Code<T>(string code) => Result<T>.Fail(new Error(new LocalizedMessage(code)));

    internal static Result<T> Field<T>(string code, string field, string reason) => Result<T>.Fail(
        new Error(new LocalizedMessage(code), new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
        {
            [field] = [new LocalizedMessage(reason)],
        }));
}
