using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.SharedKernel.Messaging;
using MediatR;

namespace LearnStack.Modules.Customization.Application.IntegrationEvents;

/// <summary>Coordinates the taxonomy effect inside the transport-owned transaction.</summary>
public sealed class InitializeTaxonomyDefaultHandler(ISender sender)
    : IIntegrationEventHandler<TenantCreatedIntegrationEventV1>
{
    public async Task HandleAsync(TenantCreatedIntegrationEventV1 @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var result = await sender.Send(new InitializeTenantLevelTaxonomyDefaultCommand(), cancellationToken);
        if (result.IsFailure)
            throw new InvalidOperationException("Taxonomy default initialization was refused.");
    }
}
