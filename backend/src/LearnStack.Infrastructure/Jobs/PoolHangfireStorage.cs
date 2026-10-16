using Hangfire;
using Hangfire.Logging;
using Hangfire.PostgreSql;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>One pool's lifecycle boundary over the shared, guarded provider.</summary>
internal sealed class PoolHangfireStorage : JobStorage
{
    private readonly PostgreSqlStorage _provider;
    private readonly PoolHangfireLifecycle _lifecycle;
#pragma warning disable CS0618 // The pinned provider exposes its selected stock components through this public seam.
    private readonly IReadOnlyList<IServerComponent> _components;
#pragma warning restore CS0618
    private readonly TimeSpan _expirationTimeout;

#pragma warning disable CS0618
    internal PoolHangfireStorage(PostgreSqlStorage provider, PoolHangfireLifecycle lifecycle,
        IReadOnlyList<IServerComponent> components)
#pragma warning restore CS0618
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        ArgumentNullException.ThrowIfNull(components);
        if (components.Any(component => component is null))
            throw new ArgumentException("The selected storage components cannot contain null.", nameof(components));
        _components = Array.AsReadOnly(components.ToArray());
        _expirationTimeout = provider.JobExpirationTimeout;
        JobExpirationTimeout = _expirationTimeout;
    }

    public override bool LinearizableReads => Provider.LinearizableReads;
    public override IMonitoringApi GetMonitoringApi() => Provider.GetMonitoringApi();
    // A lifecycle acquisition must not throw a drift refusal into Core's restart
    // catch before the owner can contain it. Validation stays inside actual use.
    public override IStorageConnection GetConnection() => new PoolHangfireConnection(_provider, _lifecycle, readOnly: false, this);
    public override IStorageConnection GetReadOnlyConnection() => new PoolHangfireConnection(_provider, _lifecycle, readOnly: true, this);
    public override IEnumerable<IBackgroundProcess> GetServerRequiredProcesses() => Provider.GetServerRequiredProcesses();
    public override IEnumerable<IBackgroundProcess> GetStorageWideProcesses() => Provider.GetStorageWideProcesses();
    public override IEnumerable<IStateHandler> GetStateHandlers() => Provider.GetStateHandlers();
    public override void WriteOptionsToLog(ILog logger) => Provider.WriteOptionsToLog(logger);
    public override bool HasFeature(string featureId) => Provider.HasFeature(featureId);
    public override string ToString() => Provider.ToString();

#pragma warning disable CS0618, CS0672 // Preserve the exact pinned obsolete component contract until its upgrade is qualified.
    public override IEnumerable<IServerComponent> GetComponents()
    {
        _ = Provider;
        return _components;
    }
#pragma warning restore CS0618, CS0672

    private PostgreSqlStorage Provider
    {
        get
        {
            // JobStorage's setter is nonvirtual. Refuse drift at every delegated entry
            // rather than silently letting pool and provider state policies diverge.
            ValidateContract();
            return _provider;
        }
    }

    internal void ValidateContract()
    {
        if (JobExpirationTimeout != _expirationTimeout || _provider.JobExpirationTimeout != _expirationTimeout)
            throw new InvalidOperationException("The shared storage expiration contract changed after composition.");
    }
}
