namespace LearnStack.SharedKernel.Jobs;

/// <summary>
/// Tenancy-declared status meanings for the framework's closed lifecycle projection.
/// No SQL, service factory, route selector or executable module callback is carried.
/// </summary>
public sealed class BackgroundJobLifecycleProjection
{
    public BackgroundJobLifecycleProjection(string trial, string active, string suspended, string activeOrganization)
    {
        var statuses = new[] { trial, active, suspended, activeOrganization };
        if (statuses.Any(value => string.IsNullOrEmpty(value) || value.Length > 50
                || value.Any(character => character is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z')))
            || new[] { trial, active, suspended }.Distinct(StringComparer.Ordinal).Count() != 3)
            throw new ArgumentException("Lifecycle projection requires distinct, bounded Tenancy status meanings.");
        Trial = trial;
        Active = active;
        Suspended = suspended;
        ActiveOrganization = activeOrganization;
    }

    public string Trial { get; }
    public string Active { get; }
    public string Suspended { get; }
    public string ActiveOrganization { get; }

    public bool AllowsTenant(string status, bool deleted, Guid? defaultOrganization,
        BackgroundJobRecipientPolicy policy)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        return !deleted && defaultOrganization is { } organization && organization != Guid.Empty
            && (status == Trial || status == Active
                || policy == BackgroundJobRecipientPolicy.ProvisionedNonArchivedTenants && status == Suspended);
    }

    public bool AllowsOrganization(string status, bool deleted) => !deleted && status == ActiveOrganization;
}
