using Docker.DotNet.Models;

namespace LearnStack.Tests.Integration;

/// <summary>One publication address for concurrently started, locally consumed fixture ports.</summary>
internal static class ContainerLoopbackPorts
{
    internal static void Configure(CreateContainerParameters parameters, string port)
    {
        var bindings = parameters.HostConfig?.PortBindings;
        if (bindings is null || bindings.Count != 1 || !bindings.TryGetValue(port, out var values) || values.Count != 1)
            throw new InvalidOperationException("Fixture port inventory is invalid.");
        values[0].HostIP = "127.0.0.1";
    }

    internal static bool IsExclusive(ContainerInspectResponse container, string port) =>
        container.HostConfig?.PortBindings is { Count: 1 } configured
        && configured.TryGetValue(port, out var intended) && intended.Count == 1 && intended[0].HostIP == "127.0.0.1"
        && container.NetworkSettings?.Ports is { } actual
        && actual.All(entry => entry.Key == port || entry.Value is null or { Count: 0 })
        && actual.TryGetValue(port, out var published) && published is { Count: 1 }
        && published[0].HostIP == "127.0.0.1" && ushort.TryParse(published[0].HostPort, out var mapped) && mapped != 0;
}
