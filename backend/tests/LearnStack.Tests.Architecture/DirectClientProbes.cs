namespace LearnStack.Tests.Architecture.Probes
{
    /// <summary>
    /// Holds a direct cache client, for <c>The_Direct_Client_Bans_Can_Actually_Fail</c>.
    /// Never constructed.
    /// </summary>
    internal sealed class CacheClientHolder(Microsoft.Extensions.Caching.Distributed.IDistributedCache cache)
    {
        public Microsoft.Extensions.Caching.Distributed.IDistributedCache Cache { get; } = cache;
    }

    /// <summary>
    /// Holds a type from a Hub namespace, for <c>The_Direct_Client_Bans_Can_Actually_Fail</c>.
    /// Never constructed.
    /// </summary>
    internal sealed class HubClientHolder(LearnStack.Hub.Probes.HubClientStandIn client)
    {
        public LearnStack.Hub.Probes.HubClientStandIn Client { get; } = client;
    }
}

namespace LearnStack.Hub.Probes
{
    /// <summary>
    /// Stands in for a Hub client in the namespace the Hub's own assemblies use. This test
    /// project is the only place such a namespace may exist in this repository.
    /// </summary>
    internal sealed class HubClientStandIn;
}
