namespace LearnStack.SharedKernel.Identity;

/// <summary>How the identity provider answered a request to delete one person's account.</summary>
public enum AccountRemovalOutcome
{
    /// <summary>The provider deleted the account.</summary>
    Removed = 1,

    /// <summary>
    /// The provider holds no such account, which is success: the closure's goal already holds. Only the provider's own
    /// answer counts; a "not found" that is not the provider's (a hop in front of it) is <see cref="Refused"/>.
    /// </summary>
    Absent = 2,

    /// <summary>The provider definitively refused the deletion (a configuration or authority fault). Nothing changed.</summary>
    Refused = 3,

    /// <summary>
    /// The provider could not be reached, or it or a hop in front of it answered with a server fault or a throttle. No
    /// deletion was confirmed: a repeat finds the account absent if one had landed.
    /// </summary>
    Unavailable = 4,

    /// <summary>The request may have been applied: its answer was lost. A repeat is safe, because the deletion is idempotent.</summary>
    Uncertain = 5,
}

/// <summary>
/// Deletes one person's account at the identity provider, the last effect of a global account closure
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 7</see>).
/// </summary>
/// <remarks>
/// <para>
/// The port is bound to <c>IProviderResilience&lt;IIdentityProviderAccountRemoval&gt;</c>. Its default implementation
/// refuses every call, so a deployment that has not configured the adapter's confined realm client fails closed and
/// no closure finishes; the Keycloak adapter is selected at the composition root when its secret is present. The
/// adapter's client holds <c>manage-users</c>, which can also create and update users in the shared realm: that
/// authority is why the port has this one method and a single caller, the closure's Finalize.
/// </para>
/// <para>
/// A missing account is success. The raw provider subject is the provider's own user identifier; it is passed in and
/// never logged, audited or stored by the port or its adapter.
/// </para>
/// <para>
/// <b>The preflight.</b> <see cref="CanRemove"/> answers, with no network or provider effect, whether a deletion of the
/// subject could be sent at all: the closure's Begin asks it before the person leaves Active, so a closure that could
/// never finish is refused while nothing has changed.
/// </para>
/// </remarks>
public interface IIdentityProviderAccountRemoval
{
    /// <summary>Deletes the account whose provider subject is <paramref name="providerSubject"/>.</summary>
    Task<AccountRemovalOutcome> RemoveAsync(string providerSubject, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this port would send the deletion of <paramref name="providerSubject"/>: false when the port is not
    /// configured, or when it refuses the subject before any network call. A pure, local answer: nothing is sent, read
    /// from the provider, logged or stored, and a true answer promises nothing about the provider's own reply.
    /// </summary>
    bool CanRemove(string providerSubject);
}
