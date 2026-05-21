using System.Collections.ObjectModel;

namespace LearnStack.SharedKernel.Localization;

/// <summary>
/// Localization-key carrier for every user-facing message LearnStack
/// returns to the frontend. The backend never returns raw English text;
/// it returns a <see cref="LocalizedMessage"/> whose <see cref="Key"/>
/// resolves to a translation on the client.
/// </summary>
/// <remarks>
/// <para>
/// The <c>lockey_</c> prefix invariant is enforced at the constructor:
/// every key must match the format the frontend's
/// <c>i18n</c> bundles ship under. Mis-prefixed keys fail loud at the
/// point of construction rather than silently resolving to "missing
/// translation" at render time. Per Phase 02a Packet 2.
/// </para>
/// <para>
/// <see cref="Params"/> values are interpolated by the frontend as plain
/// text (React text nodes, ICU MessageFormat substitution). Backend code
/// must <strong>not</strong> place HTML / Markdown / template syntax into
/// these values — the frontend resolves messages via text nodes, never
/// <c>dangerouslySetInnerHTML</c>. Standards 11 § XSS covers the frontend
/// side; this contract closes the backend side.
/// </para>
/// </remarks>
public sealed record LocalizedMessage
{
    /// <summary>
    /// The required prefix for every localization key. The frontend's
    /// translation catalogues use the same prefix.
    /// </summary>
    public const string RequiredPrefix = "lockey_";

    public LocalizedMessage(string key, IReadOnlyDictionary<string, string>? @params = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!key.StartsWith(RequiredPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Localization key must start with '{RequiredPrefix}'. Got: '{key}'.",
                nameof(key));
        }

        Key = key;
        // Snapshot + wrap: the caller may mutate the original dictionary
        // after construction, and IReadOnlyDictionary does not enforce
        // immutability on its own (callers can downcast). A ReadOnlyDictionary
        // wrapper over a defensive copy gives both: cast-safe at the API
        // level and snapshot-stable at the data level. The empty case is
        // normalised to null so serializers do not emit an unused
        // "params": {} field.
        Params = @params is { Count: > 0 }
            ? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(@params))
            : null;
    }

    /// <summary>
    /// The localization key (always begins with <see cref="RequiredPrefix"/>).
    /// Routing logic and error-tracking provider tags use this verbatim;
    /// <c>Error.Code</c> projects from this key with the prefix stripped.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Optional ICU MessageFormat parameter set. Frontend interpolates
    /// these into the resolved string as plain text (see <see cref="LocalizedMessage"/>
    /// remarks for the safety contract). <c>null</c> when the message takes
    /// no parameters.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Params { get; }

    /// <summary>
    /// Convenience factory equivalent to <c>new LocalizedMessage(key, params)</c>.
    /// </summary>
    public static LocalizedMessage Of(
        string key,
        IReadOnlyDictionary<string, string>? @params = null) =>
        new(key, @params);

    // Structural equality. The default record equality compares Params by
    // reference (IReadOnlyDictionary has no structural equality contract);
    // after the defensive-copy invariant two LocalizedMessages constructed
    // from the same source dictionary hold separate copies and would no
    // longer be equal. Override Equals + GetHashCode to compare the params
    // key-by-key so equality matches the semantic contract.
    public bool Equals(LocalizedMessage? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (!string.Equals(Key, other.Key, StringComparison.Ordinal))
        {
            return false;
        }

        if (Params is null && other.Params is null)
        {
            return true;
        }

        if (Params is null || other.Params is null || Params.Count != other.Params.Count)
        {
            return false;
        }

        foreach (var (k, v) in Params)
        {
            if (!other.Params.TryGetValue(k, out var otherValue) ||
                !string.Equals(v, otherValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Key, StringComparer.Ordinal);
        if (Params is not null)
        {
            // Order-independent: XOR each entry's hash so dictionary iteration
            // order does not affect the bucket.
            var paramsHash = 0;
            foreach (var (k, v) in Params)
            {
                paramsHash ^= HashCode.Combine(k, v);
            }

            hash.Add(paramsHash);
        }

        return hash.ToHashCode();
    }
}
