using System.Data.Common;
using System.Diagnostics;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Application.Jobs;

/// <summary>One acknowledged recipient acquisition; no child identity or caller-selected route.</summary>
public sealed class BackgroundJobRecipientClaim
{
    private readonly long _observedAt;
    private readonly TimeSpan _leaseBudget;
    private int _entered;

    private BackgroundJobRecipientClaim(DbDataReader row, long observedAt, Type declarationType)
    {
        ArgumentNullException.ThrowIfNull(declarationType);
        if (row.FieldCount != 14) throw Refused();
        RecipientId = BackgroundJobRecipientId.From(row.GetGuid(0));
        OccurrenceId = BackgroundJobOccurrenceId.From(row.GetGuid(1));
        TenantId = TenantId.From(row.GetGuid(2));
        OrganizationId = row.IsDBNull(3) ? null : LearnStack.SharedKernel.Identifiers.OrganizationId.From(row.GetGuid(3));
        RecurrenceKey = row.GetString(4);
        Revision = row.GetInt64(5);
        ManifestHash = row.GetString(6);
        DefinitionKey = row.GetString(7);
        RecipientPolicy = row.GetString(8) switch
        {
            "provisioned_non_archived_tenants" => BackgroundJobRecipientPolicy.ProvisionedNonArchivedTenants,
            "serving_tenants" => BackgroundJobRecipientPolicy.ServingTenants,
            "serving_organizations" => BackgroundJobRecipientPolicy.ServingOrganizations,
            _ => throw Refused(),
        };
        ScheduledAt = row.GetFieldValue<DateTimeOffset>(9);
        DueAt = row.GetFieldValue<DateTimeOffset>(10);
        Token = row.GetGuid(11);
        OwnershipUntil = row.GetFieldValue<DateTimeOffset>(12);
        DbNow = row.GetFieldValue<DateTimeOffset>(13);
        DeclarationType = declarationType;
        _observedAt = observedAt;
        _leaseBudget = OwnershipUntil - DbNow;
        if (RecipientId.Value.Version != 7 || OccurrenceId.Value.Version != 7 || TenantId.Value == Guid.Empty
            || TenantId == TenantId.PlatformSentinel || OrganizationId?.Value == Guid.Empty
            || (OrganizationId is not null) != (RecipientPolicy == BackgroundJobRecipientPolicy.ServingOrganizations)
            || RecurrenceKey.Length is < 3 or > 150 || DefinitionKey.Length is < 5 or > 150 || Revision < 1
            || ManifestHash.Length != 64 || ManifestHash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || Token == Guid.Empty || DueAt < ScheduledAt || DueAt > DbNow
            || observedAt > Stopwatch.GetTimestamp() || _leaseBudget <= TimeSpan.Zero || _leaseBudget > TimeSpan.FromMinutes(10))
            throw Refused();
    }

    public BackgroundJobRecipientId RecipientId { get; }
    public BackgroundJobOccurrenceId OccurrenceId { get; }
    public TenantId TenantId { get; }
    public OrganizationId? OrganizationId { get; }
    public string RecurrenceKey { get; }
    public long Revision { get; }
    public string ManifestHash { get; }
    public string DefinitionKey { get; }
    /// <summary>Trusted code identity from the compatible registered manifest, never stored CLR text.</summary>
    public Type DeclarationType { get; }
    public BackgroundJobRecipientPolicy RecipientPolicy { get; }
    public DateTimeOffset ScheduledAt { get; }
    public DateTimeOffset DueAt { get; }
    public Guid Token { get; }
    public DateTimeOffset OwnershipUntil { get; }
    public DateTimeOffset DbNow { get; }
    public bool HasLiveLease => Stopwatch.GetElapsedTime(_observedAt) < _leaseBudget;

    internal void EnterOnce()
    {
        if (!HasLiveLease || Interlocked.Exchange(ref _entered, 1) != 0) throw Refused();
    }

    private static InvalidOperationException Refused() =>
        new("Recipient admission requires its exact live stored-row acquisition.");

#pragma warning disable CA1034 // Only the confined actual-row source mints this opaque capability.
    public abstract class MintingSource
    {
        protected static BackgroundJobRecipientClaim MintRecipient(DbDataReader returnedRow, long observedAt, Type declarationType) =>
            new(returnedRow, observedAt, declarationType);
    }
#pragma warning restore CA1034
}
