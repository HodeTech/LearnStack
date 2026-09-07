using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using LearnStack.SharedKernel.Domain;

namespace LearnStack.SharedKernel.Localization;

/// <summary>
/// A short string carried in every locale a tenant has authored it in — the
/// <see href="../../../../docs/standards/08-localization.md">Pattern B</see>
/// shape, stored as one <c>jsonb</c> column.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pattern B, not a translation satellite.</b>
/// <see href="../../../../docs/standards/08-localization.md">Localization Standards
/// § Choosing between patterns</see> routes a short atomic string with few fields
/// here and a content-shaped entity with slugs and SEO metadata to a side table.
/// A taxonomy's display name is named in that table explicitly, and a content
/// type's is the same shape: one string, no slug, listed many at a time — which
/// is the case a join would make an N+1 out of.
/// </para>
/// <para>
/// <b>The keys are canonical.</b> Every locale runs through
/// <see cref="LocaleTag.Canonicalize"/> on the way in, for the reason
/// <c>TenantLocale</c> gives for the same call: case is not significant in
/// BCP-47, so <c>en-US</c> and <c>en-us</c> would otherwise be two entries naming
/// one locale, and a lookup would find whichever the author happened to type.
/// </para>
/// <para>
/// <b>It never resolves to null.</b> <see cref="Resolve"/> walks the documented
/// chain and, having exhausted it, returns the first authored value rather than
/// an empty string: this type holds a <i>label</i>, and a taxonomy item rendered
/// with no label is a worse answer than one rendered in the wrong language. The
/// empty-string branch of
/// <see href="../../../../docs/architecture/12-localization.md">§ Fallback Rules</see>
/// belongs to fields that may legitimately be absent; a
/// <see cref="LocalizedText"/> cannot be constructed empty, so that branch is
/// unreachable here by construction.
/// </para>
/// </remarks>
public sealed class LocalizedText : IEquatable<LocalizedText>
{
    /// <summary>The width one translated value maps.</summary>
    public const int MaxValueLength = 200;

    /// <summary>
    /// Beyond this a display name is not a label, and the row is a smell. It also
    /// bounds the <c>jsonb</c> document a tenant can write through one field.
    /// </summary>
    public const int MaxLocales = 50;

    private readonly ImmutableSortedDictionary<string, string> _values;

    private LocalizedText(ImmutableSortedDictionary<string, string> values) => _values = values;

    /// <summary>The authored locales, in canonical case, ordinal-ordered.</summary>
    public IReadOnlyCollection<string> Locales => _values.Keys.ToArray();

    /// <summary>
    /// Builds from locale/value pairs, canonicalizing every tag.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// No pairs, a malformed tag, a blank value, a value past
    /// <see cref="MaxValueLength"/>, more than <see cref="MaxLocales"/> entries, or
    /// two pairs that canonicalize to one locale.
    /// </exception>
    public static LocalizedText From(
        IEnumerable<KeyValuePair<string, string>> values,
        string parameterName = "values")
    {
        ArgumentNullException.ThrowIfNull(values);

        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var (locale, text) in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(locale, parameterName);
            MappedLength.EnsureAtMost(locale, LocaleTag.MaxLength, parameterName);
            LocaleTag.EnsureWellFormed(locale, parameterName);

            var canonical = LocaleTag.Canonicalize(locale);

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException(
                    $"The value for '{canonical}' is blank. A locale a tenant has not "
                    + "translated is an absent key, not an empty string — the fallback "
                    + "chain answers for it.",
                    parameterName);
            }

            MappedLength.EnsureAtMost(text, MaxValueLength, parameterName);

            // The column is jsonb and ToJson is how this value reaches it. A NUL
            // is 22P05 on the insert, three layers from here; an unpaired
            // surrogate is worse than that, because JsonSerializer rewrites it to
            // U+FFFD — measured — so the value stored is quietly not the value
            // submitted, and no error is raised at all.
            if (!JsonValue.IsStorableText(text))
            {
                throw new ArgumentException(
                    $"The value for '{canonical}' carries a character a jsonb column "
                    + "cannot store — U+0000, or an unpaired surrogate.",
                    parameterName);
            }

            // Two spellings of one locale is the defect canonicalization exists to
            // prevent; silently keeping the last would hide the author's mistake.
            if (!builder.TryAdd(canonical, text))
            {
                throw new ArgumentException(
                    $"'{canonical}' appears twice; two spellings of one locale name one "
                    + "translation.",
                    parameterName);
            }
        }

        if (builder.Count == 0)
        {
            throw new ArgumentException(
                "A localized value carries at least one locale.", parameterName);
        }

        if (builder.Count > MaxLocales)
        {
            throw new ArgumentException(
                $"{builder.Count} locales; the cap is {MaxLocales}.", parameterName);
        }

        return new LocalizedText(builder.ToImmutable());
    }

    /// <summary>
    /// Builds, or answers <see langword="false"/> for input
    /// <see cref="From(IEnumerable{KeyValuePair{string,string}}, string)"/> would
    /// refuse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a validator, which owes the caller a refusal rather than a <c>500</c>. It
    /// runs the factory itself for the reason
    /// <c>MapHostToTenantCommandValidator</c> runs <c>EffectiveHost.Normalize</c>:
    /// "what this validator accepts" and "what the aggregate accepts" are then the
    /// same set <b>by construction</b>, rather than two rule sets somebody has to
    /// keep in agreement. Every rule
    /// <see cref="From(IEnumerable{KeyValuePair{string,string}}, string)"/>'s
    /// <c>exception</c> doc lists is one a validator restating them would have to
    /// restate — and one more it could then drift on. The count is deliberately not
    /// repeated here; that list is where it lives.
    /// </para>
    /// <para>
    /// The cost is a caught exception on the refusal path, which is the trade this
    /// makes deliberately: the alternative is a second copy of six rules, and the
    /// valid path — every request that is going to succeed — pays nothing.
    /// <see cref="ArgumentNullException"/> is not caught, because a null map is a
    /// programmer error rather than input a tenant can send.
    /// </para>
    /// </remarks>
    public static bool TryFrom(
        IEnumerable<KeyValuePair<string, string>> values,
        [NotNullWhen(true)] out LocalizedText? result)
    {
        ArgumentNullException.ThrowIfNull(values);

        try
        {
            result = From(values);
            return true;
        }
        catch (ArgumentException)
        {
            result = null;
            return false;
        }
    }

    /// <inheritdoc cref="From(IEnumerable{KeyValuePair{string,string}}, string)"/>
    public static LocalizedText From(params (string Locale, string Text)[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return From(values.Select(pair => new KeyValuePair<string, string>(pair.Locale, pair.Text)));
    }

    /// <summary>
    /// Reads the <c>jsonb</c> form back.
    /// </summary>
    /// <remarks>
    /// Every rule <see cref="From(IEnumerable{KeyValuePair{string,string}}, string)"/>
    /// applies is re-applied here rather than trusted. The column is written by this
    /// type today, and a bad migration or a manual <c>UPDATE</c> is exactly the case
    /// where a materializer that trusts its input turns a data problem into a render
    /// that silently drops a label.
    /// </remarks>
    public static LocalizedText FromJson(string json, string parameterName = "json")
    {
        JsonValue.EnsureWellFormed(json, parameterName);

        // Enumerated rather than deserialized into a dictionary. A dictionary
        // collapses `{"en":"a","en":"b"}` to the last value silently, which is the
        // one duplication From() refuses — a reader that accepts what the writer
        // rejects is a gap the next importer walks straight through.
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Null)
        {
            throw new ArgumentException(
                "A localized value is a JSON object of locale to string, not null.",
                parameterName);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                $"A localized value is a JSON object of locale to string, not {root.ValueKind}.",
                parameterName);
        }

        var pairs = new List<KeyValuePair<string, string>>();

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException(
                    $"'{property.Name}' holds {property.Value.ValueKind}; a localized value "
                    + "is a JSON object of locale to string.",
                    parameterName);
            }

            pairs.Add(new KeyValuePair<string, string>(property.Name, property.Value.GetString()!));
        }

        return From(pairs, parameterName);
    }

    /// <summary>The <c>jsonb</c> form: a flat object of canonical locale to value.</summary>
    public string ToJson() => JsonSerializer.Serialize(_values);

    /// <summary>
    /// The value for <paramref name="requestedLocale"/>, or the first entry the
    /// documented fallback chain reaches.
    /// </summary>
    /// <remarks>
    /// The chain is
    /// <see href="../../../../docs/architecture/12-localization.md">§ Fallback Rules</see>:
    /// the requested tag, its language subtag, the tenant's default, the platform
    /// default. <paramref name="fallbackChain"/> carries the third and fourth,
    /// because this type knows neither — the tenant's default lives in
    /// <c>tenant_locales</c> and is resolved once per request, not once per label.
    /// </remarks>
    public string Resolve(string requestedLocale, IReadOnlyList<string>? fallbackChain = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedLocale);

        var requested = LocaleTag.Canonicalize(requestedLocale);

        if (_values.TryGetValue(requested, out var direct))
        {
            return direct;
        }

        // Narrow one subtag at a time, never widen: `zh-Hant-TW` asks `zh-Hant`
        // before `zh`, and `tr` is never treated as a request for `tr-TR`. Jumping
        // straight to the primary subtag — which this did — answers a request for
        // Traditional Chinese in Simplified whenever a tenant authored both, and
        // the type's own error message advertises `zh-Hans-CN` as a supported
        // shape. This is RFC 4647's lookup, and the corpus's "try the language
        // part" is its last iteration rather than its only one.
        for (var cut = requested.LastIndexOf('-'); cut > 0; cut = requested.LastIndexOf('-', cut - 1))
        {
            if (_values.TryGetValue(requested[..cut], out var narrower))
            {
                return narrower;
            }
        }

        if (fallbackChain is not null)
        {
            foreach (var candidate in fallbackChain)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (_values.TryGetValue(LocaleTag.Canonicalize(candidate), out var fallback))
                {
                    return fallback;
                }
            }
        }

        // Ordinal-first rather than empty: see the remarks on the type.
        return _values.Values.First();
    }

    /// <summary>Whether <paramref name="locale"/> was authored, exactly.</summary>
    public bool Has(string locale) =>
        !string.IsNullOrWhiteSpace(locale)
        && _values.ContainsKey(LocaleTag.Canonicalize(locale));

    public bool Equals(LocalizedText? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (_values.Count == other._values.Count
                && _values.All(pair =>
                    other._values.TryGetValue(pair.Key, out var value)
                    && string.Equals(pair.Value, value, StringComparison.Ordinal))));

    public override bool Equals(object? obj) => Equals(obj as LocalizedText);

    /// <remarks>
    /// Defined for the reason <c>Entity&lt;TId&gt;</c> defines them: without the
    /// pair, <c>a == b</c> binds to reference equality and answers <c>false</c> for
    /// two instances holding the same map, while <c>a.Equals(b)</c> answers
    /// <c>true</c>. The compiler warns about the reverse omission and not this one,
    /// so nothing but a test catches it.
    /// </remarks>
    public static bool operator ==(LocalizedText? left, LocalizedText? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(LocalizedText? left, LocalizedText? right) =>
        !(left == right);

    public override int GetHashCode()
    {
        var hash = default(HashCode);

        // Ordinal-sorted, so the order is the same for two equal instances however
        // they were built.
        foreach (var (locale, text) in _values)
        {
            hash.Add(locale, StringComparer.Ordinal);
            hash.Add(text, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => ToJson();
}
