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
    /// <summary>Registers zero business definitions, exact framework requests and submission; no vendor or worker activation.</summary>
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
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<BackgroundJobObservation>();
        services.TryAddSingleton<JobExecutor>();
        services.TryAddScoped<PostgresBackgroundJobScheduler>();
        services.TryAddScoped<IBackgroundJobScheduler>(provider => provider.GetRequiredService<PostgresBackgroundJobScheduler>());
        services.TryAddScoped<BackgroundJobCompletionParticipant, PostgresBackgroundJobCompletion>();
        services.TryAddScoped<BackgroundJobRecipientCompletionParticipant, PostgresBackgroundJobRecipientCompletion>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCatalogSource, BackgroundJobAuditCatalogSource>());
        services.TryAddTransient<IRequestHandler<ReadBackgroundJobEnvelopeQuery, Result<BackgroundJobEnvelope>>, ReadBackgroundJobEnvelopeQueryHandler>();
        services.TryAddTransient<IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>, AdmitBackgroundJobAttemptCommandHandler>();
        services.TryAddTransient<IRequestHandler<RecordBackgroundJobCapacityCleanupCommand, Result<None>>, RecordBackgroundJobCapacityCleanupCommandHandler>();
        services.TryAddTransient<IRequestHandler<ReadBackgroundJobExternalStateQuery, Result<BackgroundJobExternalSnapshot>>, ReadBackgroundJobExternalStateQueryHandler>();
        services.TryAddTransient<IRequestHandler<RecordBackgroundJobArmCommand, Result<None>>, RecordBackgroundJobArmCommandHandler>();
        services.TryAddTransient<IRequestHandler<RecordBackgroundJobOutcomeCommand, Result<None>>, RecordBackgroundJobOutcomeCommandHandler>();
        services.TryAddTransient<IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>, SubmitBackgroundJobRecipientCommandHandler>();
        return services;
    }
}
