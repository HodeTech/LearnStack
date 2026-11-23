namespace LearnStack.SharedKernel.Identity;

/// <summary>
/// The fail-closed default while the account-removal client is not configured: every call answers
/// <see cref="AccountRemovalOutcome.Refused"/> without any network or provider effect, so no closure finishes on a
/// deployment that has not provisioned the confined realm client
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 7</see>).
/// </summary>
/// <remarks>
/// The subject is never read, logged or stored. Its preflight answers false for every subject, so the closure's Begin
/// refuses before any person is moved to Closing on a deployment whose Finalize could never finish.
/// </remarks>
public sealed class RefusingIdentityProviderAccountRemoval : IIdentityProviderAccountRemoval
{
    public Task<AccountRemovalOutcome> RemoveAsync(string providerSubject, CancellationToken cancellationToken = default) =>
        Task.FromResult(AccountRemovalOutcome.Refused);

    /// <summary>Nothing is configured, so no deletion could ever be sent.</summary>
    public bool CanRemove(string providerSubject) => false;

    public override string ToString() => nameof(RefusingIdentityProviderAccountRemoval);
}
