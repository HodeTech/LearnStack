using LearnStack.Api.Authentication;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Tests.Integration;

/// <summary>Exact fixture bindings for proofs whose owned subject is outside identity persistence.</summary>
internal sealed class FixtureTenantIdentityBindingResolver(string issuer, IEnumerable<string> subjects,
    UserId? actor = null) : ITenantIdentityBindingResolver
{
    private readonly HashSet<string> _subjects = new(subjects, StringComparer.Ordinal);
    private readonly UserId _actor = actor ?? UserId.From(TenantAuthenticationIssuer.Actor);

    public Task<IdentityBindingResult> ResolveExistingAsync(VerifiedProviderSubject subject,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = subject.Issuer != issuer || !_subjects.Contains(subject.Subject)
            ? IdentityBindingResult.Refused(IdentityBindingOutcome.NotFound)
            : subject.ClaimedActor is { } claimed && claimed != _actor
                ? IdentityBindingResult.Refused(IdentityBindingOutcome.Conflict)
                : IdentityBindingResult.Bound(_actor, created: false);
        return Task.FromResult(result);
    }
}
