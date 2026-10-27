using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Identity.Domain;

/// <summary>Deployment-owned singleton. Runtime has no writer or rebind operation.</summary>
public sealed class IdentityAuthority
{
    private IdentityAuthority() { Issuer = null!; }

    public int Id { get; private set; }
    public IdentityAuthorityId AuthorityId { get; private set; }
    public string Issuer { get; private set; }
}
