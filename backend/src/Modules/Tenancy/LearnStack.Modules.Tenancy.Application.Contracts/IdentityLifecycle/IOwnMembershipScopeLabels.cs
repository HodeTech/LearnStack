using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;

/// <summary>Tenancy's live public labels and admitted host for one subject-confined captured relationship.</summary>
public sealed record OwnMembershipScopeLabel(string TenantName, string? OrganizationName, string EntryHost)
{
    public override string ToString() => nameof(OwnMembershipScopeLabel);
}

public interface IOwnMembershipScopeLabels
{
    Task<OwnMembershipScopeLabel?> ReadAsync(OwnMembershipScopeWitness witness,
        CancellationToken cancellationToken = default);
}
