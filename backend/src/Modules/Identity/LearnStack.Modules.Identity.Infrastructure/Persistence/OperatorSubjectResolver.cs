using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// Resolves an operator-named provider subject to its Active User through the confined definer
/// (ADR-0102 § 5), on the tenant transaction the act already owns.
/// </summary>
/// <remarks>
/// The stored authority comes from the deployment's qualified registration and the key is derived
/// here, so the request supplies the provider subject and nothing else. A subject the key derivation
/// refuses, an unknown subject and a non-Active user all answer null.
/// </remarks>
public sealed class OperatorSubjectResolver(IdentityDbContext db, IdentityAuthorityQualification qualification)
    : IOperatorSubjectResolver
{
    /// <inheritdoc />
    public async Task<UserId?> ResolveActiveUserAsync(string providerSubject, CancellationToken cancellationToken = default)
    {
        var registration = qualification.Current
            ?? throw new InvalidOperationException("Identity authority qualification is unavailable.");
        IdentitySubjectKey key;
        try
        {
            key = IdentitySubjectKey.Compute(registration.AuthorityId, providerSubject);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var authority = registration.AuthorityId.Value;
        var bytes = key.ToByteArray();
        var found = await db.Database
            .SqlQuery<Guid?>($"SELECT public.fn_identity_operator_resolve_user({authority}, {bytes}) AS \"Value\"")
            .SingleAsync(cancellationToken);
        return found is { } id ? UserId.From(id) : null;
    }
}
