using LearnStack.SharedKernel.Platform;

namespace LearnStack.SharedKernel.Entitlements;

/// <summary>How a toggle request ended.</summary>
public enum KillswitchToggleOutcome
{
    /// <summary>The switch was set and recorded.</summary>
    Applied = 1,

    /// <summary>The key is not in <see cref="KillswitchKeys"/>, so nothing was written.</summary>
    UnknownKey = 2,
}

/// <summary>What a toggle changed.</summary>
/// <param name="Outcome">How the request ended.</param>
/// <param name="Key">The key the request named.</param>
/// <param name="Before">The state before the request; the registry default when no row existed.</param>
/// <param name="After">The state after the request.</param>
public sealed record KillswitchToggleResult(
    KillswitchToggleOutcome Outcome, KillswitchKey Key, bool Before, bool After);

/// <summary>
/// The killswitch writer: the service ADR-0045 Amendment 1 withheld until a Platform gate
/// existed (ADR-0101 § 13).
/// </summary>
/// <remarks>
/// It is a service entered with a gate admission, not a MediatR request, so there is no
/// request type to key a catalogue entry on and the matrix row stays off-path. The caller
/// holds an admission for <see cref="PlatformOperations.KillswitchToggle"/> and no other.
/// </remarks>
public interface IKillswitchToggle
{
    /// <summary>Sets <paramref name="key"/> to <paramref name="enabled"/> for every tenant.</summary>
    Task<KillswitchToggleResult> ToggleAsync(
        PlatformAdmission admission,
        KillswitchKey key,
        bool enabled,
        CancellationToken cancellationToken = default);
}
