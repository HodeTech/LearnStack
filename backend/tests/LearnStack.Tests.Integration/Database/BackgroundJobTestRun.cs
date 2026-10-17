using LearnStack.Application.Jobs;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Explicit memory authority for standalone app-role owner fixtures. Native runtime
/// tests use the actual facade's process cell and never call this fixture helper.
/// </summary>
internal static class BackgroundJobTestRun
{
    internal static BackgroundJobRunGate Create() => new(new BackgroundJobRunGate.RuntimeStop());
}
