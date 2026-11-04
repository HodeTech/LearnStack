using System.Collections.Immutable;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Identity.Application.Contracts.Entry;

public interface IIdentitySelfStateReader
{
    Task<IdentityBindingResult> ReadSelfAsync(GlobalIdentitySubject subject, UserId? claimedActor = null,
        CancellationToken cancellationToken = default);
}

public interface IIdentitySelfDiscovery
{
    Task<OwnMembershipScopePage> ReadScopesAsync(GlobalIdentitySubject subject, UserId? claimedActor,
        Guid? afterMembershipId, int limit, CancellationToken cancellationToken = default);
}

/// <summary>Captured scope provenance closes with this page; it is never a reusable authorization decision.</summary>
public sealed class OwnMembershipScopePage : IDisposable
{
    private readonly OwnMembershipScopeWitness.MintingSource? _source;
    public OwnMembershipScopePage(IdentityBindingResult binding, ImmutableArray<OwnMembershipScopeWitness> scopes,
        OwnMembershipScopeWitness.MintingSource? source, bool hasNext = false)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (scopes.IsDefault || scopes.Length > 100 || (!binding.IsBound && scopes.Length != 0)
            || (scopes.Length != 0 && source is null) || (hasNext && scopes.Length == 0))
            throw new ArgumentException("Own membership page is invalid.");
        Binding = binding;
        Scopes = scopes;
        HasNext = hasNext;
        _source = source;
    }
    public IdentityBindingResult Binding { get; }
    public ImmutableArray<OwnMembershipScopeWitness> Scopes { get; }
    public bool HasNext { get; }
    public void Dispose() => _source?.Dispose();
    public override string ToString() => nameof(OwnMembershipScopePage);
}
