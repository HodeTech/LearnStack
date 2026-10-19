namespace LearnStack.SharedKernel.Tenancy;

/// <summary>Closed asynchronous origins whose diagnostics cannot include exception or payload text.</summary>
public static class AsyncExecutionDiagnostics
{
    public static bool IsTrusted(ITenantContext? context) => context is EventTenantContext or JobTenantContext
        || Jobs.JobApplicationSourceOwner.Current is not null || Jobs.JobApplicationSourceOwner.IsExternal;
}
