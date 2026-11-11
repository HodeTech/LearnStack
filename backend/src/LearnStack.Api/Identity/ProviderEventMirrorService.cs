using LearnStack.Application.Persistence;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.Modules.Identity.Application.Contracts.ProviderEvents;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Api.Identity;

/// <summary>Retains each actual source/production owner through shutdown; no request path waits for provider history.</summary>
internal sealed class ProviderEventMirrorService(IServiceProvider services, IServiceScopeFactory scopes,
    IClock clock, ProviderEventMirrorHealth health, ILogger<ProviderEventMirrorService> logger) : BackgroundService
{
    private DateTimeOffset _policyDue = DateTimeOffset.MinValue;
    private DateTimeOffset _sweepDue = DateTimeOffset.MinValue;
    private DateTimeOffset _recentDue = DateTimeOffset.MinValue;
    private DateTimeOffset _sourceResumeAfter = DateTimeOffset.MinValue;
    private DateTimeOffset _maintenanceDue = DateTimeOffset.MinValue;
    private IdentityEventRead? _recent;
    private bool _sourceBlocked = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            var stage = ProviderMirrorStage.Activation;
            try
            {
                var source = services.GetRequiredService<ProviderEventIntakeSource>();
                if (clock.UtcNow >= _sourceResumeAfter)
                {
                    try
                    {
                        stage = ProviderMirrorStage.SourcePolicy;
                        await ReadSourceSliceAsync(source, value => stage = value, stoppingToken);
                        _sourceBlocked = false;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                    {
                        _sourceBlocked = true; _sourceResumeAfter = clock.UtcNow.AddSeconds(30);
                        var failure = (error as IdentityEventProviderException)?.Failure;
                        health.Blocked(stage, failure); ProviderEventMirrorLog.Blocked(logger, stage, failure);
                    }
                }
                if (source.IsQualified)
                {
                    // Once activated, provider outage cannot prevent already-accepted targets
                    // from reaching outbox. A failed claimed target cannot monopolize siblings.
                    stage = ProviderMirrorStage.Production;
                    for (var count = 0; count < 16; count++)
                    {
                        var claim = await source.ClaimAsync(Guid.CreateVersion7(), stoppingToken);
                        if (claim is null) break;
                        try { await ProduceAsync(claim, stoppingToken); }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                        {
                            health.ProductionFailed(); ProviderEventMirrorLog.Blocked(logger, stage, null);
                        }
                    }
                    if (clock.UtcNow >= _maintenanceDue)
                    {
                        stage = ProviderMirrorStage.Maintenance;
                        await source.RetireAsync(stoppingToken);
                        var snapshot = await source.ReadHealthAsync(stoppingToken);
                        health.Observe(snapshot, !_sourceBlocked);
                        // Backlog gets another finite slice on the next ordinary
                        // loop, after source reads and target production get a turn.
                        _maintenanceDue = snapshot.HasRetirementBacklog ? clock.UtcNow : clock.UtcNow.AddSeconds(30);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                // Never attach provider/database errors, payloads, query values, tokens or subjects to diagnostics.
                var sourceFailure = (error as IdentityEventProviderException)?.Failure;
                health.Blocked(stage, sourceFailure);
                ProviderEventMirrorLog.Blocked(logger, stage, sourceFailure);
                try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task ReadSourceSliceAsync(ProviderEventIntakeSource source, Action<ProviderMirrorStage> stage, CancellationToken token)
    {
        var provider = services.GetRequiredService<IIdentityEventProvider>();
        var scanner = services.GetRequiredService<ProviderEventScanner>();
        if (clock.UtcNow >= _policyDue || !source.IsQualified)
        {
            var policy = await provider.ReadPolicyAsync(token);
            if (!source.IsQualified) await QualifyAsync(source, policy, token);
            else await source.UpdateSourcePolicyAsync(policy.RetentionSeconds, token);
            _policyDue = clock.UtcNow.AddHours(1);
        }
        if (_recent is null && clock.UtcNow >= _recentDue)
        {
            var state = await source.ReadScanAsync(token);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
            _recent = new(state.ActivationLower > at.AddMinutes(-5) ? MillisecondCeiling(state.ActivationLower) : at.AddMinutes(-5), at, 0);
            _recentDue = at.AddSeconds(30);
        }
        stage(ProviderMirrorStage.Recent);
        if (_recent is not null) _recent = await scanner.RecentPageAsync(_recent, token);
        stage(ProviderMirrorStage.Sweep);
        if (clock.UtcNow >= _sweepDue && await scanner.SweepPageAsync(token)) _sweepDue = clock.UtcNow.AddHours(1);
        stage(ProviderMirrorStage.Retry);
        await scanner.RetryOneAsync(token);
    }

    private async Task QualifyAsync(ProviderEventIntakeSource source, IdentityEventSourcePolicy policy, CancellationToken token)
    {
        var authority = services.GetRequiredService<IdentityAuthorityQualification>();
        await authority.QualifyAsync(token);
        var options = services.GetRequiredService<IdentityEventProviderOptions>();
        if (authority.Current is not { } installed || installed.AuthorityId != options.AuthorityId || installed.Issuer != options.Issuer)
            throw new InvalidOperationException("Provider mirror source does not match installed identity authority.");
        await using var proof = await authority.OpenDatabaseQualificationProofAsync(token);
        await source.QualifyAsync(new(options.AuthorityId, options.RealmId, policy.RetentionSeconds), proof, token);
    }

    private async Task ProduceAsync(ProviderEventRecipient claim, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var scoped = scope.ServiceProvider;
        scoped.GetRequiredService<ProviderEventProductionBinding>().Bind(claim, scoped.GetRequiredService<ITenantContextAccessor>());
        var result = await scoped.GetRequiredService<TransactionExecutionCoordinator>().ExecuteAsync(null, TransactionMode.ReadWrite,
            "provider_event_target", async () =>
            {
                await scoped.GetRequiredService<IProviderEventTargetPublisher>().ProduceAsync(claim, token);
                return Result<bool>.Ok(true);
            }, token);
        if (result.IsFailure) throw new InvalidOperationException("Provider target production was refused.");
    }

    public override Task StopAsync(CancellationToken cancellationToken) => base.StopAsync(CancellationToken.None);

    private static DateTimeOffset MillisecondCeiling(DateTimeOffset value)
    {
        var truncated = DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
        return truncated < value ? truncated.AddMilliseconds(1) : truncated;
    }
}

internal static partial class ProviderEventMirrorLog
{
    [LoggerMessage(EventId = 6060, Level = LogLevel.Error, Message = "Provider event mirror is blocked at {Stage} with source failure {Failure}; durable progress is not claimed.")]
    internal static partial void Blocked(ILogger logger, ProviderMirrorStage stage, IdentityEventProviderFailure? failure);
}
internal enum ProviderMirrorStage { Activation, SourcePolicy, Recent, Sweep, Retry, Production, Maintenance }
