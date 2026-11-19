using System.Collections.Frozen;

namespace LearnStack.SharedKernel.Entitlements;

/// <summary>
/// Every platform-wide switch, and the one thing each is for.
/// </summary>
/// <remarks>
/// <para>
/// <b>A killswitch gates an expensive code path, not a plan.</b> Its default is
/// <c>true</c> — the enabled state — and it is flipped <c>false</c> for the whole
/// deployment during an incident. That is why an unreadable overlay resolves to
/// <c>true</c>: a cache outage must not disable every gated path platform-wide.
/// </para>
/// <para>
/// <b>One writer flips them.</b> The killswitch toggle service
/// (<see href="../../../../docs/decisions/0101-platform-operator-authority.md">ADR-0101 § 13</see>)
/// sets a key from this registry only, inside an admitted platform scope. Every gated read
/// honours a flipped switch
/// (<see href="../../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md">ADR-0045
/// Amendment 1 § 4</see>).
/// </para>
/// </remarks>
public static class KillswitchKeys
{
    /// <summary>Recording a live class. Flipped off during a storage incident.</summary>
    /// <remarks>
    /// The one switch a <see cref="FeatureDescriptor"/> names —
    /// <see cref="FeatureKeys.ClassroomRecording"/>'s — so the overlay applies it.
    /// </remarks>
    public static readonly KillswitchKey RecordingEnabled = new("killswitch.classroom.recording");

    /// <summary>Outbound email. Flipped off during an upstream provider outage.</summary>
    /// <remarks>
    /// Named by no feature descriptor, and that is correct rather than an omission: email
    /// dispatch is a code path every plan reaches, so there is no entitlement for an
    /// overlay to override. A guarded path reads this switch directly.
    /// </remarks>
    public static readonly KillswitchKey EmailDispatchEnabled = new("killswitch.notifications.email");

    /// <summary>Analytics ingest. Flipped off when the pipeline is back-pressured.</summary>
    /// <remarks>
    /// Deliberately NOT named by <see cref="FeatureKeys.AdvancedReporting"/>'s descriptor.
    /// The two are different things — ingest is the write side, advanced reporting is a
    /// plan feature over the read side — and a correspondence invented here would switch
    /// off a paid capability during an unrelated incident.
    /// </remarks>
    public static readonly KillswitchKey AnalyticsIngestEnabled = new("killswitch.analytics.ingest");

    /// <summary>Every switch, with its default.</summary>
    /// <remarks>
    /// The value is what the overlay answers when the table has no row and when the read
    /// fails. Both are <c>true</c>, and they mean the same thing: nobody has flipped it.
    /// </remarks>
    public static readonly FrozenDictionary<KillswitchKey, bool> All =
        new Dictionary<KillswitchKey, bool>
        {
            [RecordingEnabled] = true,
            [EmailDispatchEnabled] = true,
            [AnalyticsIngestEnabled] = true,
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, KillswitchKey> ByName =
        All.Keys.ToFrozenDictionary(key => key.Value, StringComparer.Ordinal);

    /// <summary>
    /// Resolves a switch the wire names to the registry's own member, so a caller reads a key from here
    /// and never spells one.
    /// </summary>
    /// <param name="name">The key as an operator or a route carries it.</param>
    /// <param name="key">The registry member when the name is one.</param>
    /// <returns><c>false</c> for a name the registry does not declare.</returns>
    public static bool TryResolve(string? name, out KillswitchKey key)
    {
        if (name is not null && ByName.TryGetValue(name, out key))
        {
            return true;
        }

        key = default;
        return false;
    }
}
