using LearnStack.SharedKernel.Entitlements;

namespace LearnStack.SharedKernel.Jobs;

/// <summary>
/// A finite module.resource group declared in Application. The runtime intersects
/// its positive operational ceiling with the optional registered Hard plan limit.
/// </summary>
public sealed class BackgroundJobCapacityGroupRegistration(
    string key, Type declaringType, int operationalCeiling = 1, LimitKey? planLimit = null)
{
    public string Key { get; } = key ?? throw new ArgumentNullException(nameof(key));
    public Type DeclaringType { get; } = declaringType ?? throw new ArgumentNullException(nameof(declaringType));
    public int OperationalCeiling { get; } = operationalCeiling;
    public LimitKey? PlanLimit { get; } = planLimit;
}
