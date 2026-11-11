using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Identity.Application.ProviderEvents;

public interface IProviderEventTargetStore
{
    Task RequireClaimAsync(ProviderEventRecipient recipient, CancellationToken cancellationToken = default);
    Task MarkEnqueuedAsync(ProviderEventRecipient recipient, CancellationToken cancellationToken = default);
}
