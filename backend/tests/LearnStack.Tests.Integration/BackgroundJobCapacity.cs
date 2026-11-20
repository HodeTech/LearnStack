using LearnStack.Infrastructure.Jobs;

namespace LearnStack.Tests.Integration;

/// <summary>
/// The smallest <c>Maximum Pool Size</c> the default background-job topology accepts. The
/// fixtures derive it from the production manifest instead of repeating a literal, so a
/// new connection slot in a messaging store or the job runtime cannot leave a test below
/// the floor. The literal is pinned once, in <c>BackgroundJobPhysicalCapacityTests</c>.
/// </summary>
internal static class BackgroundJobCapacity
{
    public static readonly int Floor = BackgroundJobPhysicalCapacity
        .Calculate(BackgroundJobInfrastructureRegistry.CreateDefault()).RequiredConnections;
}
