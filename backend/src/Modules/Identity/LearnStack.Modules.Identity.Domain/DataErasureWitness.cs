using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Identity.Domain;

/// <summary>
/// The erasure witness of
/// <see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 5</see>:
/// one subject's data in one scope group may be erased because that subject's <c>erasure</c> request for that group
/// is <c>fulfilling</c>. The erasure operations of <see cref="Membership"/> and <see cref="Invitation"/> run only
/// under it, and they leave every ordinary method's rules as they are.
/// </summary>
/// <remarks>
/// <para>
/// <b>Privately constructed.</b> Only a <see cref="MintingSource"/> captures one, from the fulfilling erasure request
/// itself, and the architecture census pins its one production deriver, so no other caller holds the authority the
/// erasure operations require.
/// </para>
/// <para>
/// <b>Live only with its evidence.</b> A witness answers <see cref="IsActive"/> only while its source still holds what
/// it was minted from; a witness kept past the erasure that minted it erases nothing.
/// </para>
/// </remarks>
public sealed class DataErasureWitness
{
    private readonly MintingSource _source;

    private DataErasureWitness(MintingSource source, DataSubjectRequest request)
    {
        _source = source;
        RequestId = request.Id;
        SubjectUserId = request.SubjectUserId;
        TenantId = request.TenantId;
        OrganizationId = request.OrganizationId;
    }

    /// <summary>The fulfilling erasure request the witness was minted from.</summary>
    public DataSubjectRequestId RequestId { get; }

    /// <summary>The person whose data the witness lets the erasure operations change.</summary>
    public UserId SubjectUserId { get; }

    public TenantId TenantId { get; }

    /// <summary>The group's organization, or <see langword="null"/> for the tenant-wide group.</summary>
    public OrganizationId? OrganizationId { get; }

    public bool IsActive => _source.IsCurrent;

    /// <summary>Whether the witness is live and covers a row of exactly this tenant, group and person.</summary>
    internal bool Covers(TenantId tenant, OrganizationId? organization, UserId person) =>
        IsActive && TenantId == tenant && OrganizationId == organization && SubjectUserId == person;

    public override string ToString() => nameof(DataErasureWitness);

#pragma warning disable CA1034 // A closed source census owns the sole production minter.
    /// <summary>The only way to capture a witness: from an <c>erasure</c> request that is <c>fulfilling</c>.</summary>
    public abstract class MintingSource
#pragma warning restore CA1034
    {
        internal bool IsCurrent => IsEvidenceCurrent;

        /// <summary>Whether the evidence the witness was minted from still holds.</summary>
        protected abstract bool IsEvidenceCurrent { get; }

        /// <exception cref="InvalidOperationException">
        /// The request is not a fulfilling erasure: a programming error, because the source decides that before it captures.
        /// </exception>
        protected DataErasureWitness Capture(DataSubjectRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Kind != DataRequestKind.Erasure || request.Status != DataRequestStatus.Fulfilling || request.IsDeleted)
            {
                throw new InvalidOperationException("An erasure witness is captured only from a fulfilling erasure request.");
            }

            return new DataErasureWitness(this, request);
        }
    }
}
