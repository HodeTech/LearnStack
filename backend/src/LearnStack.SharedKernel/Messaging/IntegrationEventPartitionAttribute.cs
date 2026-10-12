namespace LearnStack.SharedKernel.Messaging;

/// <summary>Names the identifier from which a concrete event derives its partition key.</summary>
/// <remarks>Tenant-wide ordering requires an explicit declaration naming TenantId.</remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class IntegrationEventPartitionAttribute(string sourceProperty, bool tenantWide = false)
    : Attribute
{
    /// <summary>The public GUID or approved strongly-typed identifier property.</summary>
    public string SourceProperty { get; } = sourceProperty;

    /// <summary>Whether this event deliberately partitions on its tenant identifier.</summary>
    public bool TenantWide { get; } = tenantWide;
}
