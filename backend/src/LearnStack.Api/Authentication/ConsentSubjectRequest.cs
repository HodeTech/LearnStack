using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;

namespace LearnStack.Api.Authentication;

/// <summary>One selected immutable request; this feature grants no scope or persistence authority.</summary>
internal sealed class ConsentSubjectRequest
{
    internal ConsentSubjectRequest(object request)
    {
        if (request is not (ListOwnFieldConsentScopesQuery or ListOwnMembershipFieldConsentsQuery
            or WithdrawOwnMembershipFieldConsentCommand or ReadOwnWithdrawnFieldConsentReceiptQuery))
            throw new ArgumentException("Consent request refused.");
        Request = request;
    }
    internal object Request { get; }
    public override string ToString() => nameof(ConsentSubjectRequest);
}
