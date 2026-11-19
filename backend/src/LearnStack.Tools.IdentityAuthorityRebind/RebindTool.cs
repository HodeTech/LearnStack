using System.Text;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.Modules.Identity.Application.Audit;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LearnStack.Tools.IdentityAuthorityRebind;

/// <summary>
/// The offline controlled issuer rebind (ADR-0104): validate the human, admit the act, run it, and say how it ended in
/// one fixed line that carries counts and classes and never an identifier.
/// </summary>
/// <remarks>
/// <para>
/// Exit codes: <c>0</c> the issuer was changed or already was; <c>2</c> refused with the state unchanged (a retry
/// repeats the whole proof); <c>3</c> the commit's outcome could not be read back, so the operator reads the stored
/// issuer before anything else.
/// </para>
/// <para>
/// The operator token is validated by the same <see cref="PlatformOperatorTokenValidator"/> the API scheme is
/// configured from, and the grant is read per act from <c>PlatformOperator:Grants</c>, never from a claim. The
/// tool is the second caller of the witness minter's closed census: it mints nothing itself.
/// </para>
/// </remarks>
internal static class RebindTool
{
    internal const int Applied = 0;
    internal const int Refused = 2;
    internal const int Unknown = 3;

    internal static async Task<int> RunAsync(
        RebindToolInput input, INewRealmContinuitySource realm, TextWriter output, CancellationToken cancellationToken)
    {
        // One handle for every outcome from here on, so an operator can quote it whatever the run answered.
        var correlation = "rebind-" + Guid.NewGuid().ToString("N");
        PlatformOperatorSettings? settings;
        try
        {
            settings = PlatformOperatorSettings.FromConfiguration(input.Configuration, input.Mode);
        }
        catch (InvalidOperationException)
        {
            return Refuse(output, "operator_configuration_invalid", correlation);
        }

        if (settings is null)
        {
            return Refuse(output, "operator_issuer_not_configured", correlation);
        }

        try
        {
            return await RunAdmittedAsync(input, realm, output, settings, correlation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The deadline or an interrupt ended the run before the database was touched, or the database half has
            // already rolled back and reported it.
            return Refuse(output, "cancelled", correlation);
        }
    }

    private static async Task<int> RunAdmittedAsync(
        RebindToolInput input,
        INewRealmContinuitySource realm,
        TextWriter output,
        PlatformOperatorSettings settings,
        string correlation,
        CancellationToken cancellationToken)
    {
        var clock = new SystemClock();
        using var backchannel = new PlatformOperatorBackchannel();
        var health = new PlatformOperatorMetadataHealth(clock);
        var manager = PlatformOperatorValidation.CreateConfigurationManager(settings, backchannel, health, NullLogger.Instance);
        var validation = await new PlatformOperatorTokenValidator(settings, manager, health, clock).ValidateAsync(
            input.OperatorToken, PlatformOperatorSettings.RebindToolExecutor, correlation, cancellationToken);
        if (!validation.IsValid)
        {
            return Refuse(output, "operator_token_" + validation.Cause, correlation);
        }

        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock>(clock)
            .AddOptions<PlatformOperatorGrantOptions>()
            .Bind(input.Configuration.GetSection(PlatformOperatorGrantOptions.SectionName)).Services
            .AddSingleton<IValidateOptions<PlatformOperatorGrantOptions>>(new PlatformOperatorGrantOptionsValidator(input.Mode))
            .AddSingleton<IPlatformOperatorGrantSource, ConfigurationPlatformOperatorGrantSource>()
            .AddSingleton<IPlatformOperatorGate, PlatformOperatorGate>()
            .BuildServiceProvider();
        var admitted = await services.GetRequiredService<IPlatformOperatorGate>().AdmitAsync(
            validation.Witness!, PlatformOperations.IdentityAuthorityRebind, input.Ticket, cancellationToken);
        if (!admitted.IsAdmitted)
        {
            return Refuse(output, "operator_" + Cause(admitted.Refusal), correlation);
        }

        await using var store = StoreScope();
        var outcome = await new IdentityIssuerRebind(store.ServiceProvider.GetRequiredService<IAuditStore>(), clock)
            .RunAsync(input.ConnectionString, input.Target, admitted.Admission!, realm, cancellationToken: cancellationToken);
        output.WriteLine(Line(outcome, correlation));
        return outcome.Result switch
        {
            IdentityRebindResult.Applied or IdentityRebindResult.AlreadyApplied => Applied,
            IdentityRebindResult.Unknown => Unknown,
            _ => Refused,
        };
    }

    /// <summary>The closed audit-store graph the rebind rows are written through, on the role's own connection.</summary>
    private static AsyncServiceScope StoreScope() =>
        new ServiceCollection()
            .AddMetrics()
            .AddLogging()
            .AddScoped<IAuditStateCapture, AuditStateCapture>()
            .AddSingleton<IAuditHealth, AuditHealth>()
            .AddSingleton<IAuditCatalog>(new AuditCatalog([new IdentityAuditCatalogSource()]))
            .AddSingleton<ITenantContextAccessor>(StaticTenantContextAccessor.Unresolved)
            .AddScoped<IGlobalIdentityEnrollment, GlobalIdentityEnrollment>()
            .AddSingleton(new Lazy<NpgsqlDataSource>(
                () => throw new InvalidOperationException("The rebind rows are written on the tool's own connection.")))
            .AddScoped<IAuditStore, PostgresAuditStore>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();

    /// <summary>One line: the outcome class, the correlation id that finds the audit rows, and counts.</summary>
    internal static string Line(IdentityRebindOutcome outcome, string correlation)
    {
        var line = new StringBuilder("rebind: outcome=").Append(IdentityRebindMetrics.Label(outcome))
            .Append(" correlation=").Append(correlation);
        if (outcome.LiveSessions is { } sessions)
        {
            line.Append(" sessions=").Append(sessions);
        }

        if (outcome.Continuity is { } counts)
        {
            line.Append(" active=").Append(counts.StoredActive).Append(" closing=").Append(counts.StoredClosing)
                .Append(" closed=").Append(counts.StoredClosed).Append(" reported=").Append(counts.Enumerated)
                .Append(" unkeyable=").Append(counts.Unkeyable).Append(" missing_active=").Append(counts.MissingActive)
                .Append(" reappearing_closed=").Append(counts.ReappearingClosed)
                .Append(" unbootstrapped=").Append(counts.Unbootstrapped);
        }

        return line.ToString();
    }

    internal static int Refuse(TextWriter output, string reason, string? correlation = null)
    {
        output.WriteLine("rebind: outcome=refused_" + reason + (correlation is null ? string.Empty : " correlation=" + correlation));
        return Refused;
    }

    private static string Cause(PlatformRefusal refusal) => refusal switch
    {
        PlatformRefusal.InvalidWitness => "invalid_witness",
        PlatformRefusal.UnknownOperation => "unknown_operation",
        PlatformRefusal.InvalidTicket => "invalid_ticket",
        PlatformRefusal.MissingPermission => "missing_permission",
        _ => "not_admitted",
    };
}
