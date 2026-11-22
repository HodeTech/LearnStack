using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.DataProtection;

namespace LearnStack.Application.DataProtection;

/// <summary>
/// The composition root's closed collection of every registered <see cref="IUserDataParticipant"/>
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 1</see>).
/// </summary>
/// <remarks>
/// <para>
/// Built from the container's registrations, the way the audit catalogue and the operation policy
/// catalogue are, and refused when a key is malformed or registered twice: two participants writing
/// the same progress-record key would make a resumed job skip work it never did.
/// </para>
/// <para>
/// Ordered ordinally by key, so the order a job runs participants in, and records them in, does not
/// depend on registration order. An empty registry is valid; no module registers a participant
/// until its first writer ships.
/// </para>
/// </remarks>
public sealed class UserDataParticipantRegistry : IUserDataParticipantRegistry
{
    private readonly FrozenDictionary<string, IUserDataParticipant> _byKey;

    /// <exception cref="InvalidOperationException">A participant is missing, malformed or duplicated.</exception>
    public UserDataParticipantRegistry(IEnumerable<IUserDataParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        var byKey = new Dictionary<string, IUserDataParticipant>(StringComparer.Ordinal);
        foreach (var participant in participants)
        {
            if (participant is null || !DataRequestLimits.IsParticipantKey(participant.Key)
                || !byKey.TryAdd(participant.Key, participant))
            {
                throw new InvalidOperationException("User data participant registrations are missing, malformed or conflicting.");
            }
        }

        if (byKey.Count > DataRequestLimits.MaximumParticipants)
        {
            throw new InvalidOperationException("User data participant registrations exceed the progress record's bound.");
        }

        _byKey = byKey.ToFrozenDictionary(StringComparer.Ordinal);
        Participants = [.. byKey.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value)];
    }

    /// <inheritdoc />
    public IReadOnlyList<IUserDataParticipant> Participants { get; }

    /// <inheritdoc />
    public bool TryGet(string key, [NotNullWhen(true)] out IUserDataParticipant? participant)
    {
        if (key is not null && _byKey.TryGetValue(key, out var found))
        {
            participant = found;
            return true;
        }

        participant = null;
        return false;
    }
}
