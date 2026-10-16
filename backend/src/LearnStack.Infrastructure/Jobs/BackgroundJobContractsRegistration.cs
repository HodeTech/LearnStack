using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Shared API/Seeder registration of submission and exact framework admission.</summary>
public static class BackgroundJobContractsRegistration
{
    /// <summary>Registers zero business definitions, two framework requests and submission; no vendor or worker activation.</summary>
    public static IServiceCollection AddLearnStackBackgroundJobContracts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(_ => BackgroundJobDefinitionRegistry.CreateEmpty());
        services.TryAddSingleton<IBackgroundJobDefinitionCatalog>(provider =>
            provider.GetRequiredService<BackgroundJobDefinitionRegistry>());
        services.TryAddSingleton<IBackgroundJobRequestCatalog>(provider =>
            provider.GetRequiredService<BackgroundJobDefinitionRegistry>());
        services.TryAddSingleton<BackgroundJobExecutionAdmission>();
        services.TryAddSingleton<BackgroundJobRuntimeOptions>();
        services.TryAddSingleton<JobExecutor>();
        services.TryAddScoped<IBackgroundJobScheduler, PostgresBackgroundJobScheduler>();
        services.TryAddScoped<BackgroundJobCompletionParticipant, PostgresBackgroundJobCompletion>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCatalogSource, BackgroundJobAuditCatalogSource>());
        services.TryAddTransient<IRequestHandler<ReadBackgroundJobEnvelopeQuery, Result<BackgroundJobEnvelope>>, ReadBackgroundJobEnvelopeQueryHandler>();
        services.TryAddTransient<IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>, AdmitBackgroundJobAttemptCommandHandler>();
        return services;
    }
}
