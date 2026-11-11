using System.Collections.Frozen;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Infrastructure.Identity.ProviderEvents;

public static class IdentityEventKindCodes
{
    private static readonly FrozenDictionary<string, IdentityProviderEventKind> Kinds = new Dictionary<string, IdentityProviderEventKind>(StringComparer.Ordinal)
    {
        ["LOGIN"] = IdentityProviderEventKind.Login,
        ["LOGIN_ERROR"] = IdentityProviderEventKind.LoginFailure,
        ["LOGOUT"] = IdentityProviderEventKind.Logout,
        ["LOGOUT_ERROR"] = IdentityProviderEventKind.LogoutFailure,
        ["SEND_RESET_PASSWORD"] = IdentityProviderEventKind.PasswordResetRequested,
        ["SEND_RESET_PASSWORD_ERROR"] = IdentityProviderEventKind.PasswordResetRequestFailure,
        ["RESET_PASSWORD"] = IdentityProviderEventKind.PasswordReset,
        ["RESET_PASSWORD_ERROR"] = IdentityProviderEventKind.PasswordResetFailure,
        ["UPDATE_CREDENTIAL"] = IdentityProviderEventKind.CredentialUpdated,
        ["UPDATE_CREDENTIAL_ERROR"] = IdentityProviderEventKind.CredentialUpdateFailure,
        ["REMOVE_CREDENTIAL"] = IdentityProviderEventKind.CredentialRemoved,
        ["REMOVE_CREDENTIAL_ERROR"] = IdentityProviderEventKind.CredentialRemovalFailure,
        ["UPDATE_PASSWORD"] = IdentityProviderEventKind.PasswordUpdated,
        ["UPDATE_PASSWORD_ERROR"] = IdentityProviderEventKind.PasswordUpdateFailure,
        ["UPDATE_TOTP"] = IdentityProviderEventKind.TotpUpdated,
        ["UPDATE_TOTP_ERROR"] = IdentityProviderEventKind.TotpUpdateFailure,
        ["REMOVE_TOTP"] = IdentityProviderEventKind.TotpRemoved,
        ["REMOVE_TOTP_ERROR"] = IdentityProviderEventKind.TotpRemovalFailure,
        ["USER_DISABLED_BY_TEMPORARY_LOCKOUT"] = IdentityProviderEventKind.TemporaryLockout,
        ["USER_DISABLED_BY_PERMANENT_LOCKOUT"] = IdentityProviderEventKind.PermanentLockout,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IEnumerable<string> RequiredCodes => Kinds.Keys;
    public static bool TryKind(string code, out IdentityProviderEventKind kind) => Kinds.TryGetValue(code, out kind);
    public static string Code(IdentityProviderEventKind kind) => Kinds.Single(pair => pair.Value == kind).Key;
}
