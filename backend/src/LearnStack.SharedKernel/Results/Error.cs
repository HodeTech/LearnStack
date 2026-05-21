using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Localization;

namespace LearnStack.SharedKernel.Results;

/// <summary>
/// Result-pattern error payload used by <see cref="Result{T}"/>.
/// <see cref="Message"/> is the localised payload the frontend resolves;
/// <see cref="Code"/> is the stable machine-readable identifier API
/// consumers route on (Standards 04 § Problem Details and Standards 09
/// § Forbidden — codes do not get localized, only resolved messages do).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Code"/> is derived from <c>Message.Key</c> by stripping the
/// invariant <see cref="LocalizedMessage.RequiredPrefix"/>: a key of
/// <c>"lockey_validation_failed"</c> projects as <c>"validation_failed"</c>.
/// This keeps the two contracts in sync by construction — there is no way
/// to ship an <see cref="Error"/> whose Code disagrees with the
/// localization key the frontend resolves.
/// </para>
/// <para>
/// The constructor takes a defensive snapshot of <see cref="Details"/>
/// (dictionary copy + per-key <see cref="ReadOnlyCollection{T}"/> wrap) so
/// callers cannot mutate the validation map after the Result is in flight
/// — Problem Details bodies, audit rows, and structured logs all read
/// <see cref="Details"/> after the handler returns, and a mutation
/// between handler-return and serialisation would produce inconsistent
/// output across the three sinks.
/// </para>
/// <para>
/// Structural equality is overridden because <see cref="IReadOnlyDictionary{TKey,TValue}"/>
/// uses reference equality by default — two Errors with the same Message
/// and equivalent Details (built from different dictionary instances)
/// would otherwise compare unequal.
/// </para>
/// <para>
/// CA1716 (avoid reserved language keywords as type names) is intentionally
/// suppressed: the project's Result+Error pattern follows the FluentResults /
/// Ardalis.Result lineage where the type is canonically named <c>Error</c>.
/// LearnStack is C#-only — there is no VB consumer to which the "Error"
/// keyword collision would surface. Per ADR-0032 § Error Model.
/// </para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Result+Error pattern — C#-only codebase per ADR-0032; no VB consumer affected.")]
public sealed record Error
{
    public Error(
        LocalizedMessage message,
        IReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>>? details = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        Message = message;
        Details = SnapshotDetails(details);
    }

    public LocalizedMessage Message { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>>? Details { get; }

    /// <summary>
    /// Stable machine-readable code derived from <see cref="Message"/>'s
    /// <c>Key</c> with the <see cref="LocalizedMessage.RequiredPrefix"/>
    /// stripped. Used by <c>Result.ToActionResult()</c> and Problem Details
    /// writers as the RFC 7807 <c>code</c> field — never localized, never
    /// changes per locale (Standards 09 § Forbidden).
    /// </summary>
    public string Code => Message.Key[LocalizedMessage.RequiredPrefix.Length..];

    public bool Equals(Error? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Message.Equals(other.Message) && DetailsEqual(Details, other.Details);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Message);

        if (Details is not null)
        {
            var detailsHash = 0;
            foreach (var (key, list) in Details)
            {
                var listHash = 0;
                foreach (var msg in list)
                {
                    listHash ^= msg.GetHashCode();
                }

                detailsHash ^= HashCode.Combine(key, listHash);
            }

            hash.Add(detailsHash);
        }

        return hash.ToHashCode();
    }

    private static ReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>>? SnapshotDetails(
        IReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>>? source)
    {
        if (source is not { Count: > 0 })
        {
            return null;
        }

        var snapshot = new Dictionary<string, IReadOnlyList<LocalizedMessage>>(source.Count);
        foreach (var (key, list) in source)
        {
            ArgumentNullException.ThrowIfNull(list);

            // Per-element null check: a null inside the list would later NPE
            // in GetHashCode / DetailsEqual. Fail fast at construction with
            // the offending key in the message so the caller can locate the
            // bug rather than chasing a NullReferenceException down the
            // serialisation path.
            var copy = new LocalizedMessage[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                copy[i] = list[i] ?? throw new ArgumentException(
                    $"Error.Details['{key}'][{i}] is null. Every field-level entry must be a non-null LocalizedMessage.",
                    nameof(source));
            }

            snapshot[key] = new ReadOnlyCollection<LocalizedMessage>(copy);
        }

        return new ReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>>(snapshot);
    }

    private static bool DetailsEqual(
        IReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>>? a,
        IReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>>? b)
    {
        if (a is null && b is null)
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, listA) in a)
        {
            if (!b.TryGetValue(key, out var listB) || listA.Count != listB.Count)
            {
                return false;
            }

            for (var i = 0; i < listA.Count; i++)
            {
                if (!listA[i].Equals(listB[i]))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
