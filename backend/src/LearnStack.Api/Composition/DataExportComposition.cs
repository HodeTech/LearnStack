using LearnStack.Application.DataProtection;
using LearnStack.Infrastructure.DataProtection;
using LearnStack.Modules.Audit.Infrastructure.DataRequests;
using LearnStack.Modules.Customization.Infrastructure.DataRequests;
using LearnStack.Modules.Education.Infrastructure.DataRequests;
using LearnStack.Modules.Tenancy.Infrastructure.DataRequests;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Api.Composition;

/// <summary>
/// The composition root's half of subject data requests
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 1, § 4</see>):
/// the bundle ceiling, the export key ring, its startup check and the participants of the modules that register
/// through this root. Identity's participant and artifact store come with its contextual foundation.
/// </summary>
internal static class DataExportComposition
{
    internal static IServiceCollection AddDataExportCustody(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // Fail fast: an invalid ceiling refuses composition, and the key ring is checked before workers start.
        services.AddLearnStackUserDataProtection(DataExportOptions.FromSetting(configuration[DataExportOptions.MaxBytesKey]));
        services.TryAddSingleton<DataExportCipher>();
        services.AddHostedService<DataExportStartupService>();
        services.AddTenancyUserDataParticipant();
        services.AddCustomizationUserDataParticipant();
        services.AddEducationUserDataParticipant();
        services.AddAuditUserDataParticipant();
        return services;
    }
}

/// <summary>The export key ring is fixed and validated before workers start, like the invitation key ring.</summary>
internal sealed class DataExportStartupService(DataExportCipher cipher) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Construction validates the complete key ring; nothing is sealed here.
        ArgumentNullException.ThrowIfNull(cipher);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
