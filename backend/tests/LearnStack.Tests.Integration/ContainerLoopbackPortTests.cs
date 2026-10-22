using Docker.DotNet.Models;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>Offline controls for publication intent and independently observed Docker bindings.</summary>
public sealed class ContainerLoopbackPortTests
{
    [Fact]
    public void Publication_requires_one_loopback_binding_in_both_configuration_and_runtime()
    {
        const string port = "5432/tcp";
        var parameters = new CreateContainerParameters
        {
            HostConfig = new HostConfig
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                { [port] = [new() { HostIP = "0.0.0.0", HostPort = "" }] },
            },
        };
        ContainerLoopbackPorts.Configure(parameters, port);
        var inspection = new ContainerInspectResponse
        {
            HostConfig = parameters.HostConfig,
            NetworkSettings = new NetworkSettings
            {
                Ports = new Dictionary<string, IList<PortBinding>>
                { [port] = [new() { HostIP = "127.0.0.1", HostPort = "35432" }] },
            },
        };
        ContainerLoopbackPorts.IsExclusive(inspection, port).Should().BeTrue();
        foreach (var address in new[] { "", "0.0.0.0", "::", "192.0.2.1" })
        {
            inspection.NetworkSettings.Ports[port][0].HostIP = address;
            ContainerLoopbackPorts.IsExclusive(inspection, port).Should().BeFalse();
        }
        inspection.NetworkSettings.Ports[port][0].HostIP = "127.0.0.1";
        foreach (var mapped in new[] { "", "0", "-1", "65536", "invalid" })
        {
            inspection.NetworkSettings.Ports[port][0].HostPort = mapped;
            ContainerLoopbackPorts.IsExclusive(inspection, port).Should().BeFalse();
        }
        inspection.NetworkSettings.Ports[port][0].HostPort = "35432";
        parameters.HostConfig.PortBindings[port][0].HostIP = "0.0.0.0";
        ContainerLoopbackPorts.IsExclusive(inspection, port).Should().BeFalse();
        ContainerLoopbackPorts.Configure(parameters, port);
        inspection.NetworkSettings.Ports["8443/tcp"] = [new() { HostIP = "127.0.0.1", HostPort = "35443" }];
        ContainerLoopbackPorts.IsExclusive(inspection, port).Should().BeFalse();
        inspection.NetworkSettings.Ports.Remove("8443/tcp");
        ContainerLoopbackPorts.IsExclusive(inspection, port).Should().BeTrue();

        foreach (var invalid in new[]
        {
            new CreateContainerParameters(),
            new() { HostConfig = new HostConfig { PortBindings = new Dictionary<string, IList<PortBinding>>() } },
            new() { HostConfig = new HostConfig { PortBindings = new Dictionary<string, IList<PortBinding>> { [port] = [] } } },
            new() { HostConfig = new HostConfig { PortBindings = new Dictionary<string, IList<PortBinding>> { [port] = [new(), new()] } } },
            new() { HostConfig = new HostConfig { PortBindings = new Dictionary<string, IList<PortBinding>> { ["8443/tcp"] = [new()] } } },
        })
        {
            Action configure = () => ContainerLoopbackPorts.Configure(invalid, port);
            configure.Should().Throw<InvalidOperationException>();
        }
    }
}
