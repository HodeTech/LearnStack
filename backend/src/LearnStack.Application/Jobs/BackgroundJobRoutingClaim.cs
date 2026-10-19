using System.Data.Common;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Application.Jobs;

/// <summary>Opaque route minted from the confined routing UPDATE, before any payload read.</summary>
public sealed class BackgroundJobRoutingClaim
{
    private int _entered;

    private BackgroundJobRoutingClaim(DbDataReader row)
    {
        if (row.FieldCount != 12) throw new InvalidOperationException("The routing claim requires its exact returned projection.");
        JobId = BackgroundJobId.From(row.GetGuid(0));
        TenantId = TenantId.From(row.GetGuid(1));
        OrganizationId = row.IsDBNull(2) ? null : LearnStack.SharedKernel.Identifiers.OrganizationId.From(row.GetGuid(2));
        DefinitionKey = row.GetString(3);
        Queue = row.GetString(4);
        WakeGeneration = row.GetInt64(5);
        Token = row.GetGuid(6);
        OwnershipUntil = row.GetFieldValue<DateTimeOffset>(7);
        OriginalDueAt = row.GetFieldValue<DateTimeOffset>(8);
        Profile = row.GetString(9) switch
        {
            "transactional" => BackgroundJobExecutionProfile.Transactional,
            "external" => BackgroundJobExecutionProfile.External,
            _ => throw new InvalidOperationException("The routing claim has an unknown profile."),
        };
        LedgerStage = row.GetString(10) switch
        {
            "pending" => BackgroundJobLedgerStage.Normal,
            "finalize_pending" when Profile == BackgroundJobExecutionProfile.External => BackgroundJobLedgerStage.Finalize,
            _ => throw new InvalidOperationException("The routing claim has no counted eligible stage."),
        };
        DbNow = row.GetFieldValue<DateTimeOffset>(11);
        if (!JobId.IsInitialized() || JobId.Value.Version != 7 || TenantId.Value == Guid.Empty
            || TenantId == TenantId.PlatformSentinel || OrganizationId?.Value == Guid.Empty
            || Token == Guid.Empty || WakeGeneration < 1 || OwnershipUntil <= DbNow || OriginalDueAt > DbNow)
            throw new InvalidOperationException("The routing claim has invalid identity.");
    }

    public BackgroundJobId JobId { get; }
    public TenantId TenantId { get; }
    public OrganizationId? OrganizationId { get; }
    public string DefinitionKey { get; }
    public string Queue { get; }
    public long WakeGeneration { get; }
    public Guid Token { get; }
    public DateTimeOffset OwnershipUntil { get; }
    public DateTimeOffset OriginalDueAt { get; }
    public BackgroundJobExecutionProfile Profile { get; }
    public BackgroundJobLedgerStage LedgerStage { get; }
    public DateTimeOffset DbNow { get; }

    internal void EnterOnce()
    {
        if (Interlocked.Exchange(ref _entered, 1) != 0)
            throw new InvalidOperationException("A routing claim enters one executor only.");
    }

#pragma warning disable CA1034 // Private construction follows the closed terminal-transition minting pattern.
    public abstract class MintingSource
#pragma warning restore CA1034
    {
        protected static BackgroundJobRoutingClaim MintClaim(DbDataReader returnedRow) => new(returnedRow);
    }
}
