using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Identity;

/// <summary>Exact qualified provider facts. Possession of this value does not authorize creation or adoption.</summary>
public sealed class GlobalIdentitySubject
{
    public GlobalIdentitySubject(IdentityAuthorityId authorityId, string issuer, string subject)
    {
        if (!authorityId.IsInitialized() || authorityId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(issuer) || issuer.Length > 2048
            || !Uri.TryCreate(issuer, UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(address.UserInfo) || !string.IsNullOrEmpty(address.Fragment)
            || string.IsNullOrWhiteSpace(subject) || subject.Length > 255 || !JsonValue.IsStorableText(subject))
            throw new ArgumentException("Qualified identity subject facts are invalid.");

        AuthorityId = authorityId;
        Issuer = issuer;
        Subject = subject;
        SubjectKey = IdentitySubjectKey.Compute(authorityId, subject);
    }

    public IdentityAuthorityId AuthorityId { get; }
    public string Issuer { get; }
    public string Subject { get; }
    public IdentitySubjectKey SubjectKey { get; }

    public override string ToString() => nameof(GlobalIdentitySubject);
}
