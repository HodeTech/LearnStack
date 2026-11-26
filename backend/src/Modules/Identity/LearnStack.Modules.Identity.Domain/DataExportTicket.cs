using System.Security.Cryptography;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Identity.Domain;

/// <summary>
/// One download cursor over an export artifact, bound to the subject's session and stored only as a
/// digest (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 4</see>).
/// </summary>
/// <remarks>
/// <para>
/// A CSRF-protected POST mints it for fifteen minutes; it authorizes the sequential chunk reads of one
/// download and is marked consumed when the final chunk is served. A resumed or repeated download mints
/// a new ticket. Neither digest is a bearer credential on its own: the ticket also binds the session.
/// </para>
/// <para>
/// <b>Append-only but for one write-once marker</b>, so it derives <see cref="Entity{TId}"/> rather than
/// <c>AuditableEntity</c>: it carries no update stamps or row version, and the application role's only
/// update privilege is the consumed marker.
/// </para>
/// </remarks>
[TenantOwned]
[OrganizationScoped]
public sealed class DataExportTicket : Entity<DataExportTicketId>, IAggregateRoot<DataExportTicketId>, IOrganizationScoped
{
    private DataExportTicket() { }

    public TenantId TenantId { get; private set; }
    public OrganizationId? OrganizationId { get; private set; }
    public DataExportArtifactId ArtifactId { get; private set; }

    /// <summary>The SHA-256 digest of the ticket value, lower-case hex; the value itself is never stored.</summary>
    [PiiSensitive]
    public string Digest { get; private set; } = string.Empty;

    /// <summary>The SHA-256 digest binding the ticket to the minting session, lower-case hex.</summary>
    [PiiSensitive]
    public string SessionBindingDigest { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The subject who minted it; only the subject retrieves an export.</summary>
    public UserId CreatedBy { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>Mints a ticket for the artifact's own subject while the artifact is readable.</summary>
    public static Result<DataExportTicket> Mint(DataExportTicketId id, DataExportArtifact artifact, string digest,
        string sessionBindingDigest, DateTimeOffset at, UserId subject)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!StronglyTypedId.IsAssigned(id) || id.Value.Version != 7 || !ValidDigest(digest) || !ValidDigest(sessionBindingDigest)
            || at == default || at.Offset != TimeSpan.Zero || at > DateTimeOffset.MaxValue - DataRequestLimits.TicketLifetime
            || !artifact.IsReadable(at) || at < artifact.CreatedAt || subject != artifact.SubjectUserId
            || !StronglyTypedId.IsAssigned(subject) || subject == UserId.SystemActor)
        {
            return Result<DataExportTicket>.Fail(IdentityFailures.BusinessRuleError());
        }

        return Result<DataExportTicket>.Ok(new DataExportTicket
        {
            Id = id,
            TenantId = artifact.TenantId,
            OrganizationId = artifact.OrganizationId,
            ArtifactId = artifact.Id,
            Digest = digest,
            SessionBindingDigest = sessionBindingDigest,
            CreatedAt = at,
            CreatedBy = subject,
            ExpiresAt = at + DataRequestLimits.TicketLifetime,
        });
    }

    /// <summary>Whether the ticket still authorizes a chunk read at <paramref name="now"/>.</summary>
    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && now >= CreatedAt && now < ExpiresAt;

    /// <summary>Compares a presented ticket digest and session binding in fixed time.</summary>
    public bool Matches(string digest, string sessionBindingDigest) =>
        SameDigest(Digest, digest) & SameDigest(SessionBindingDigest, sessionBindingDigest);

    /// <summary>Marks the download finished when its final chunk is served; a ticket is consumed once.</summary>
    public Result<None> Consume(DateTimeOffset at)
    {
        if (!IsUsable(at) || at.Offset != TimeSpan.Zero)
        {
            return IdentityFailures.BusinessRule();
        }

        ConsumedAt = at;
        return Result<None>.Ok(None.Value);
    }

    private static bool ValidDigest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);

    private static bool SameDigest(string actual, string? supplied) => ValidDigest(actual) && ValidDigest(supplied)
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(supplied!));
}
