using System.Linq.Expressions;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

public sealed class MembershipFieldConsentReader(TenancyDbContext context) : IMembershipFieldConsentReader
{
    public async Task<MembershipFieldConsentSnapshot?> ReadCurrentAsync(MembershipFieldConsentTuple tuple,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        var results = await ReadCurrentBatchAsync([tuple], cancellationToken);
        return results[0];
    }

    public async Task<IReadOnlyList<MembershipFieldConsentSnapshot?>> ReadCurrentBatchAsync(
        IReadOnlyList<MembershipFieldConsentTuple> tuples, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        if (tuples.Count > 20 || tuples.Any(tuple => !IsValid(tuple)) || tuples.Distinct().Count() != tuples.Count)
            throw new ArgumentException("Consent tuples are invalid.", nameof(tuples));
        if (tuples.Count == 0) return [];

        // Build an OR of complete tuple predicates. Member access on each captured
        // tuple lets EF parameterize the values rather than embedding personal data
        // as SQL literals. The correlated anti-existence test selects only the row
        // with the greatest sequence for each tuple; history is never materialized.
        var parameter = Expression.Parameter(typeof(MembershipFieldConsent), "consent");
        Expression predicate = Expression.Constant(false);
        foreach (var tuple in tuples)
        {
            var captured = Expression.Constant(tuple);
            Expression Match(string property) => Expression.Equal(
                Expression.Property(parameter, property), Expression.Property(captured, property));
            var exact = Expression.AndAlso(Match(nameof(MembershipFieldConsent.MembershipId)),
                Expression.AndAlso(Match(nameof(MembershipFieldConsent.SubjectUserId)),
                Expression.AndAlso(Match(nameof(MembershipFieldConsent.DefinitionId)),
                Expression.AndAlso(Match(nameof(MembershipFieldConsent.DefinitionVersion)),
                    Match(nameof(MembershipFieldConsent.PurposeKey))))));
            predicate = Expression.OrElse(predicate, exact);
        }
        var matches = Expression.Lambda<Func<MembershipFieldConsent, bool>>(predicate, parameter);
        using var scope = TenancyAdministrationStorageScope.Enter(context);
        var rows = await TenancyAdministrationStorageBoundary.ReadAsync(() => context.MembershipFieldConsents
            .AsNoTracking().Where(matches)
            .Where(row => !context.MembershipFieldConsents.Any(later =>
                later.TenantId == row.TenantId && later.OrganizationId == row.OrganizationId
                && later.MembershipId == row.MembershipId && later.SubjectUserId == row.SubjectUserId
                && later.DefinitionId == row.DefinitionId && later.DefinitionVersion == row.DefinitionVersion
                && later.PurposeKey == row.PurposeKey && later.Sequence > row.Sequence))
            .ToListAsync(cancellationToken));
        var current = rows.ToDictionary(row => new MembershipFieldConsentTuple(row.MembershipId, row.SubjectUserId,
                row.DefinitionId, row.DefinitionVersion, row.PurposeKey),
            row => new MembershipFieldConsentSnapshot(row.Id.Value, row.Sequence,
                row.State == MembershipFieldConsentState.Granted, row.NoticeLocale, row.NoticeHash));
        return tuples.Select(tuple => current.GetValueOrDefault(tuple)).ToArray();
    }

    private static bool IsValid(MembershipFieldConsentTuple? tuple) => tuple is not null
        && tuple.MembershipId != Guid.Empty && tuple.DefinitionId != Guid.Empty && tuple.DefinitionVersion > 0
        && tuple.SubjectUserId.IsInitialized() && tuple.SubjectUserId.Value != Guid.Empty
        && tuple.SubjectUserId != UserId.SystemActor && !string.IsNullOrEmpty(tuple.PurposeKey)
        && tuple.PurposeKey.Length <= 64
        && tuple.PurposeKey.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
