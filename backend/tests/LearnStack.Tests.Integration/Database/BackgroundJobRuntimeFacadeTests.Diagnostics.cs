using System.Runtime.ExceptionServices;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRuntimeFacadeTests
{
    private static async Task PreserveNativeBodyFailureAsync(Exception? bodyFailure, Func<Task> cleanup)
    {
        try { await cleanup(); }
        catch (Exception cleanupFailure) when (bodyFailure is not null)
        {
            // Explicit finally cleanup must not replace the original premise or
            // assertion. Both private stacks remain available to the closed mapper.
            throw new AggregateException(bodyFailure, cleanupFailure);
        }
        if (bodyFailure is not null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
    }
}
