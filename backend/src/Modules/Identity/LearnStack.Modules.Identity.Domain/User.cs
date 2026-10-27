using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Identity.Domain;

public enum UserStatus { Active = 1, Closing = 2, Closed = 3 }

/// <summary>A permanent provider-subject binding; it contains no tenant-authored profile.</summary>
public sealed class User : AuditableEntity<UserId>, IAggregateRoot<UserId>
{
    private User() { SubjectKey = null!; }

    public IdentityAuthorityId AuthorityId { get; private set; }
    public IdentitySubjectKey SubjectKey { get; private set; }
    [PiiSensitive]
    public string? ProviderSubject { get; private set; }
    public UserStatus Status { get; private set; }
    public AuditEntryId CreationAuditId { get; private set; }
    public DateTimeOffset CreationAuditTimestamp { get; private set; }

    public static User Create(GlobalIdentitySubject subject, GlobalIdentityCreationReference creation,
        DateTimeOffset at, UserId createdBy)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(creation);
        creation.RequireSubject(subject);
        var user = new User
        {
            Id = creation.TargetUserId,
            AuthorityId = subject.AuthorityId,
            SubjectKey = subject.SubjectKey,
            ProviderSubject = subject.Subject,
            Status = UserStatus.Active,
            CreationAuditId = creation.AuditId,
            CreationAuditTimestamp = creation.Timestamp,
        };
        user.MarkCreated(at, createdBy);
        return user;
    }
}
