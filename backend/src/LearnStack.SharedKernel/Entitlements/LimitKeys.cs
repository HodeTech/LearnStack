using System.Collections.Frozen;

namespace LearnStack.SharedKernel.Entitlements;

/// <summary>
/// Every numeric ceiling the platform knows, with its floor and its enforcement mode.
/// </summary>
/// <remarks>
/// <para>
/// <b>The spellings are the Hub's, verbatim, and the intersection with LearnStack's own
/// earlier names was empty.</b> Four LearnStack documents named
/// <c>tenancy.max_learners</c>, <c>classroom.minutes_per_month</c> and
/// <c>media.storage_gb</c>; the Hub's merged <c>LimitKeys</c> names nine keys under a
/// <c>limits.</c> prefix and its plan validators reject a plan whose limits are not from
/// that set. A key the Hub never sends misses on every real projection and falls through
/// to the floor below — a paid tenant silently reading as unentitled
/// (<see href="../../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md">ADR-0045
/// Amendment 1 § 1</see>).
/// </para>
/// <para>
/// <b>The floors are NOT the Hub's Starter row, and that is a decision rather than a
/// transcription error.</b>
/// <see href="../../../../docs/architecture/26-hybrid-license-model.md">Hybrid License
/// Model</see> requires the fallback to be "the Starter-tier defaults compiled into the
/// binary. Never <c>-1</c>, never <c>0</c>" — and the Hub's Starter row carries
/// <c>0</c> for three of the nine. Both rules cannot hold. Copying the row would ship the
/// exact failure that sentence exists to prevent: a floor of <c>0</c> on
/// <c>limits.api_rate_per_minute</c> denies every API call the moment a projection is
/// late. The six non-zero Starter values are taken verbatim; the three zeros are replaced
/// with LearnStack's own smallest working allowance, and each says why below.
/// </para>
/// <para>
/// Unlimited is a gift and zero is an outage. The floor keeps a tenant working at the
/// smallest plan's ceiling until the real answer arrives.
/// </para>
/// </remarks>
public static class LimitKeys
{
    /// <summary>How many users the tenant may have.</summary>
    public static readonly LimitKey MaxUsers = new("limits.max_users");

    /// <summary>How many organizations the tenant may create.</summary>
    public static readonly LimitKey MaxOrganizations = new("limits.max_organizations");

    /// <summary>Live-class minutes per calendar month.</summary>
    public static readonly LimitKey ClassroomMinutesPerMonth = new("limits.classroom_minutes_per_month");

    /// <summary>Stored recording, in gigabytes.</summary>
    public static readonly LimitKey RecordingStorageGb = new("limits.recording_storage_gb");

    /// <summary>Stored media, in gigabytes.</summary>
    public static readonly LimitKey MediaStorageGb = new("limits.media_storage_gb");

    /// <summary>Media egress per calendar month, in gigabytes.</summary>
    public static readonly LimitKey MediaBandwidthGbPerMonth = new("limits.media_bandwidth_gb_per_month");

    /// <summary>API requests per minute.</summary>
    public static readonly LimitKey ApiRatePerMinute = new("limits.api_rate_per_minute");

    /// <summary>How many content types the tenant may author.</summary>
    public static readonly LimitKey MaxCustomContentTypes = new("limits.max_custom_content_types");

    /// <summary>How many page-block definitions the tenant may author.</summary>
    public static readonly LimitKey MaxPageBlockDefinitions = new("limits.max_page_block_definitions");

    /// <summary>Every descriptor, by key.</summary>
    public static readonly FrozenDictionary<LimitKey, LimitDescriptor> All =
        new LimitDescriptor[]
        {
            // Hub Starter, verbatim. Hard: creating the twenty-sixth user is a clean
            // refusal at a moment the caller chose.
            new(MaxUsers, 25, LimitEnforcement.Hard),
            new(MaxOrganizations, 1, LimitEnforcement.Hard),

            // Hub Starter is 0 here. Sixty minutes: one class. Soft, because the limit is
            // reached DURING a session nobody chose the timing of — cutting a class off
            // mid-lesson is a worse outcome than an overage banner and a usage alert.
            new(ClassroomMinutesPerMonth, 60, LimitEnforcement.Soft),

            // Hub Starter is 0 here. One gigabyte holds a class or two of recording. Soft
            // for the same reason: the recording is produced by the platform mid-class,
            // so refusing it destroys work the tenant cannot retry.
            new(RecordingStorageGb, 1, LimitEnforcement.Soft),

            // Hub Starter, verbatim. Hard: an upload is a caller-initiated act, and
            // refusing one is a clean 403 the client can act on.
            new(MediaStorageGb, 5, LimitEnforcement.Hard),

            // Hub Starter, verbatim. Soft: egress is spent by a visitor loading a page, so
            // a hard stop takes the tenant's public site down rather than refusing an
            // operator's action.
            new(MediaBandwidthGbPerMonth, 50, LimitEnforcement.Soft),

            // Hub Starter is 0 here. Sixty a minute is one request a second — enough for a
            // real integration to work while a projection is late. Hard, necessarily: a
            // rate limit that does not refuse is not a rate limit.
            new(ApiRatePerMinute, 60, LimitEnforcement.Hard),

            // Hub Starter, verbatim. Hard: both are authoring acts with a clean refusal
            // point, and both are what customization.unlimited_content_types lifts.
            new(MaxCustomContentTypes, 5, LimitEnforcement.Hard),
            new(MaxPageBlockDefinitions, 5, LimitEnforcement.Hard),
        }.ToFrozenDictionary(descriptor => descriptor.Key);

    /// <summary>The sentinel meaning "no ceiling".</summary>
    /// <remarks>
    /// <c>-1</c> is unlimited, <c>0</c> is denied outright, and anything greater is the
    /// allowance. It is a named constant because the three-way meaning is not guessable
    /// from a bare integer at a call site (ADR-0045 § 3).
    /// </remarks>
    /// <remarks>
    /// The VALUE is the contract, not just the name: it is what the Hub's wire payload
    /// carries and what every enforcement point will compare against. A test pins the
    /// literal rather than reading this constant back, because a constant read back agrees
    /// with whatever it became — and becoming <see cref="Denied"/> would turn "no ceiling"
    /// into "no allowance" on every tenant in every deployment mode at once.
    /// </remarks>
    public const long Unlimited = -1;

    /// <summary>The value meaning "the plan grants no allowance at all".</summary>
    public const long Denied = 0;
}
