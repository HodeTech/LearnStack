namespace LearnStack.SharedKernel.Identity;

public enum IdentityProviderEventKind
{
    Login, LoginFailure, Logout, LogoutFailure,
    PasswordResetRequested, PasswordResetRequestFailure, PasswordReset, PasswordResetFailure,
    CredentialUpdated, CredentialUpdateFailure, CredentialRemoved, CredentialRemovalFailure,
    PasswordUpdated, PasswordUpdateFailure, TotpUpdated, TotpUpdateFailure,
    TotpRemoved, TotpRemovalFailure, TemporaryLockout, PermanentLockout,
}

/// <summary>Minimized provider observation; the affected account is not its human initiator.</summary>
public sealed record IdentityProviderEvent
{
    public IdentityProviderEvent(Guid sourceEventId, DateTimeOffset occurredAt,
        IdentityProviderEventKind kind, IdentitySubjectKey? subjectKey)
    {
        if (sourceEventId == Guid.Empty || !Enum.IsDefined(kind) || occurredAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Identity provider observation is invalid.");
        SourceEventId = sourceEventId; OccurredAt = occurredAt; Kind = kind; SubjectKey = subjectKey;
    }
    public Guid SourceEventId { get; }
    public DateTimeOffset OccurredAt { get; }
    public IdentityProviderEventKind Kind { get; }
    public IdentitySubjectKey? SubjectKey { get; }
    public override string ToString() => nameof(IdentityProviderEvent);
}
