using System.Globalization;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace LearnStack.Api.Common;

/// <summary>ADR-0079's single canonical strong decimal precondition; generic EntityTag is unchanged.</summary>
public static class ProtectedEntityTag
{
    public static bool TryRead(HttpRequest request, out long version)
    {
        ArgumentNullException.ThrowIfNull(request);
        version = 0;
        var values = request.Headers.IfMatch;
        return values.Count == 1 && TryParse(values[0], out version);
    }

    internal static bool TryParse(string? tag, out long version)
    {
        version = 0;
        return tag is { Length: >= 3 and <= 21 } && tag[0] == '"' && tag[^1] == '"'
            && TryParseVersion(tag[1..^1], out version) && EntityTag.For(version) == tag;
    }

    internal static bool TryParseVersion(string? value, out long version)
    {
        version = 0;
        return value is { Length: > 0 and <= 19 }
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out version)
            && version >= 0 && version.ToString(CultureInfo.InvariantCulture) == value;
    }

    public static Error Invalid() => new(LocalizedMessage.Of("lockey_validation_failed"),
        new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
        {
            ["ifMatch"] = [LocalizedMessage.Of("lockey_invalid_value")],
        });
}
