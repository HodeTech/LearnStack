using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Scope-bound, uncached retained evidence; authorization belongs to its caller.</summary>
public sealed class OwnMembershipFieldConsentHistory(TenancyDbContext db, ITenantContext context) : IOwnMembershipFieldConsentHistory
{
    public Task<MembershipFieldConsentDetails?> ReadEvidenceAsync(UserId subject, Guid membershipId, Guid evidenceId,
        CancellationToken cancellationToken = default) => ReadAsync(() => Base(subject, membershipId)
            .Where(row => row.Id == MembershipFieldConsentId.From(evidenceId))
            .Select(row => Project(row)).SingleOrDefaultAsync(cancellationToken));

    public Task<MembershipFieldConsentDetails?> ReadExactAsync(UserId subject, Guid membershipId, Guid definitionId,
        int definitionVersion, CancellationToken cancellationToken = default) => ReadAsync(() => Base(subject, membershipId)
            .Where(row => row.DefinitionId == definitionId && row.DefinitionVersion == definitionVersion)
            .OrderByDescending(row => row.Sequence).Select(row => Project(row)).FirstOrDefaultAsync(cancellationToken));

    public async Task<MembershipFieldConsentPage?> ListLatestAsync(UserId subject, Guid membershipId, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 20) return null;
        if (!context.IsResolved || context.UserId != subject || subject == UserId.SystemActor || membershipId == Guid.Empty)
            throw new InvalidOperationException("Consent history owner is invalid.");
        var scope = ConsentHistoryCursor.Scope(context, subject, membershipId);
        Guid? after = null;
        if (cursor is not null && !ConsentHistoryCursor.TryDecode(cursor, scope, out after)) return null;
        var query = Base(subject, membershipId);
        if (after is { } anchor) query = query.Where(row => row.DefinitionId.CompareTo(anchor) > 0);
        var rows = await ReadAsync(async () => await query
            .Where(row => !db.MembershipFieldConsents.Any(later => later.TenantId == row.TenantId
                && later.OrganizationId == row.OrganizationId && later.SubjectUserId == row.SubjectUserId
                && later.MembershipId == row.MembershipId && later.DefinitionId == row.DefinitionId
                && later.DefinitionVersion == row.DefinitionVersion && later.PurposeKey == row.PurposeKey
                && later.Sequence > row.Sequence))
            .OrderBy(row => row.DefinitionId).Take(limit + 1).Select(row => Project(row)).ToArrayAsync(cancellationToken));
        var page = rows.Take(limit).ToArray();
        return new MembershipFieldConsentPage(page, rows.Length > limit ? ConsentHistoryCursor.Encode(scope, page[^1].DefinitionId) : null);
    }

    private IQueryable<MembershipFieldConsent> Base(UserId subject, Guid membershipId)
    {
        if (!context.IsResolved || context.UserId != subject || subject == UserId.SystemActor || membershipId == Guid.Empty)
            throw new InvalidOperationException("Consent history owner is invalid.");
        return db.MembershipFieldConsents.AsNoTracking().Where(row => row.SubjectUserId == subject
            && row.MembershipId == membershipId && row.OrganizationId == context.OrganizationId);
    }

    // EF permits this client-side final projection after selecting bounded rows; the
    // predicate and limit remain in SQL.
    private static MembershipFieldConsentDetails Project(MembershipFieldConsent row) => new(row.Id.Value, row.Sequence,
        row.State == MembershipFieldConsentState.Granted, row.MembershipId, row.DefinitionId, row.DefinitionVersion,
        row.PurposeKey, row.NoticeLocale, row.NoticeHash);

    private async Task<T> ReadAsync<T>(Func<Task<T>> read)
    {
        using var scope = TenancyAdministrationStorageScope.Enter(db);
        return await TenancyAdministrationStorageBoundary.ReadAsync(read);
    }
}

internal static class ConsentHistoryCursor
{
    private static readonly UTF8Encoding Strict = new(false, true);
    internal static string Scope(ITenantContext context, UserId actor, Guid membershipId) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            1, "tenancy.membership_field_consent.history", context.TenantId.Value.ToString("D"),
            context.OrganizationId?.Value.ToString("D"), actor.Value.ToString("D"), membershipId.ToString("D"),
        })));
    internal static string Encode(string scope, Guid id) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
    { v = 1, scope, id = id.ToString("D") })).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static bool TryDecode(string raw, string scope, out Guid? after)
    {
        after = null;
        if (raw.Length is < 1 or > 256 || raw.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) return false;
        try
        {
            var bytes = Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/') + new string('=', (4 - raw.Length % 4) % 4));
            using var document = JsonDocument.Parse(Strict.GetString(bytes), new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(p => p.Name).SequenceEqual(["v", "scope", "id"])
                || !root.GetProperty("v").TryGetInt32(out var version) || version != 1
                || root.GetProperty("scope").GetString() != scope || root.GetProperty("id").GetString() is not { } text
                || !Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || id.Version != 7 || id.ToString("D") != text
                || Encode(scope, id) != raw) return false;
            after = id;
            return true;
        }
        catch (Exception failure) when (failure is FormatException or JsonException or DecoderFallbackException or InvalidOperationException)
        {
            return false;
        }
    }
}
