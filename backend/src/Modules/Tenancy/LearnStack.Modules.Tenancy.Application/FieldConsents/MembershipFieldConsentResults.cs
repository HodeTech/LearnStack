using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Domain;

namespace LearnStack.Modules.Tenancy.Application.FieldConsents;

internal static class MembershipFieldConsentResults
{
    internal static MembershipFieldConsentReceipt Receipt(MembershipFieldConsent row) => new(row.Id.Value, row.Sequence,
        row.State == MembershipFieldConsentState.Granted);
    internal static string Map(MembershipFieldConsentStorageFailure failure) => failure switch
    {
        MembershipFieldConsentStorageFailure.Stale or MembershipFieldConsentStorageFailure.Conflict => "lockey_concurrency_conflict",
        MembershipFieldConsentStorageFailure.Cooldown or MembershipFieldConsentStorageFailure.State => "lockey_business_rule_violation",
        _ => "lockey_dependency_unavailable",
    };
}
