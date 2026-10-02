using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.Modules.Customization.Infrastructure.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Modules.Customization.Infrastructure;

public static class CustomizationReadRegistration
{
    public static IServiceCollection AddCustomizationProjectionReads(this IServiceCollection services)
    {
        services.TryAddScoped<CustomizationReadState>();
        services.TryAddScoped<DefinitionFamilyCache>();
        services.TryAddScoped<DefinitionSnapshotStore>();
        services.TryAddScoped<ICustomizationDefinitionProjectionReader, CustomizationDefinitionProjectionReader>();
        return services;
    }
}
