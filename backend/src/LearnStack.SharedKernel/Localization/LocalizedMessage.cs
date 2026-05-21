namespace LearnStack.SharedKernel.Localization;

/// <summary>
/// Localization-key carrier for every user-facing message LearnStack
/// returns to the frontend. The backend never returns raw English text;
/// it returns a <see cref="LocalizedMessage"/> whose <see cref="Key"/>
/// resolves to a translation on the client.
/// </summary>
/// <remarks>
/// The <c>lockey_</c> prefix invariant is enforced at the constructor:
/// every key must match the format the frontend's
/// <c>i18n</c> bundles ship under. Mis-prefixed keys fail loud at the
/// point of construction rather than silently resolving to "missing
/// translation" at render time. Per Phase 02a Packet 2.
/// </remarks>
public sealed record LocalizedMessage
{
    /// <summary>
    /// The required prefix for every localization key. The frontend's
    /// translation catalogues use the same prefix.
    /// </summary>
    public const string RequiredPrefix = "lockey_";

    private readonly IReadOnlyDictionary<string, string>? _params;

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
        _params = @params;
    }

    /// <summary>
    /// The localization key (always begins with <see cref="RequiredPrefix"/>).
    /// Routing logic and error-tracking provider tags use this verbatim.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Optional ICU MessageFormat parameter set. Frontend interpolates
    /// these into the resolved string. <c>null</c> when the message takes
    /// no parameters.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Params => _params;

    /// <summary>
    /// Convenience factory equivalent to <c>new LocalizedMessage(key, params)</c>.
    /// </summary>
    public static LocalizedMessage Of(
        string key,
        IReadOnlyDictionary<string, string>? @params = null) =>
        new(key, @params);
}
