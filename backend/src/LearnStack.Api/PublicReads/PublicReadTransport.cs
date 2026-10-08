using System.Security.Cryptography;
using System.Text;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;

namespace LearnStack.Api.PublicReads;

/// <summary>Transport shape and classified-host fingerprint; never a scope resolver.</summary>
internal static class PublicReadTransport
{
    internal static PublicReadInput Input(HttpContext context, bool outline, bool lesson)
    {
        var query = context.Request.Query;
        string[] allowed = lesson ? ["locale"] : outline ? ["locale", "lessonCursor", "lessonLimit"] : ["locale", "cursor", "limit"];
        var invalid = query.Select(pair => allowed.Contains(pair.Key, StringComparer.Ordinal)
            ? pair.Value.Count == 1 ? null : pair.Key : "query").Where(field => field is not null).Select(field => field!).Distinct(StringComparer.Ordinal).ToArray();
        string? Value(string key) => query.TryGetValue(key, out var values) && values.Count == 1 ? values[0] : null;
        var host = context.Features.Get<HostClassification>()?.Host
            ?? throw new InvalidOperationException("Host classification must precede public transport construction.");
        return new PublicReadInput(Value("locale"), lesson ? null : Value(outline ? "lessonCursor" : "cursor"),
            lesson ? null : Value(outline ? "lessonLimit" : "limit"), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(host))), invalid);
    }
}
