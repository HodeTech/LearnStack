using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Api.Identity;

/// <summary>A fresh DI scope's immutable context binding; never an ambient accessor writer.</summary>
internal sealed class ProviderEventProductionBinding
{
    private ProviderEventRecipient? _recipient;
    private bool _resolved;

    internal void Bind(ProviderEventRecipient recipient, ITenantContextAccessor ambient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        if (_recipient is not null || _resolved || ambient.Current is { IsResolved: true })
            throw new InvalidOperationException("Provider production requires a fresh unbound scope.");
        _recipient = recipient;
    }

    internal ProviderEventRecipient? Resolve()
    {
        _resolved = true;
        return _recipient;
    }
}
