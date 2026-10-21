using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Docker.DotNet;
using DotNet.Testcontainers.Configurations;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class KeycloakFixture
{
    internal async Task<StockProviderResources> StockProviderResourcesAsync()
    {
        if (_keycloak is null || _database is null || _network is null)
            throw new InvalidOperationException("Stock proof fixture resources are not initialized.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.NewGuid()).Build();
        var provider = await docker.Containers.InspectContainerAsync(_keycloak.Id, deadline.Token);
        var image = await docker.Images.InspectImageAsync(provider.Image, deadline.Token);
        if (provider.Image != image.ID || !image.ID.StartsWith("sha256:", StringComparison.Ordinal)
            || image.RepoDigests is null || image.RepoDigests.Count == 0)
            throw new InvalidOperationException("Stock proof provider image identity is unavailable.");
        if (provider.NetworkSettings?.Networks is not { } networks
            || !networks.TryGetValue(_network.Name, out var network) || string.IsNullOrEmpty(network?.NetworkID))
            throw new InvalidOperationException("Stock proof owned network identity is unavailable.");
        return new StockProviderResources(_keycloak.Id, _database.Id, network.NetworkID,
            image.ID, image.RepoDigests.Order(StringComparer.Ordinal).ToArray(), image.Os, image.Architecture,
            image.Descriptor?.Digest, image.Descriptor?.MediaType, provider.ImageManifestDescriptor?.Digest);
    }

    internal async Task<string> StockProviderVersionAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = KeycloakHttp.Create(new Uri(BaseAddress, "admin/"), RootCertificate);
        // The authenticated server-info exception has a separate fixed 512 KiB bound.
        client.MaxResponseContentBufferSize = 512 * 1024;
        var credential = await AuthenticateAdministratorAsync(deadline.Token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        using var response = await KeycloakHttp.SendAsync(client, HttpMethod.Get, "serverinfo", cancellationToken: deadline.Token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("Stock proof provider version request was refused.");
        var body = await response.Content.ReadAsByteArrayAsync(deadline.Token);
        if (body.Length > 512 * 1024) throw new InvalidOperationException("Stock proof provider version exceeded its bound.");
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("systemInfo").GetProperty("version").GetString()
                ?? throw new InvalidOperationException("Stock proof provider version is absent.");
        }
        catch (JsonException) { throw new InvalidOperationException("Stock proof provider version is malformed."); }
    }

    internal static async Task AssertStockResourcesAbsentAsync(StockProviderResources resources)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.NewGuid()).Build();
        foreach (var id in new[] { resources.ProviderContainerId, resources.DatabaseContainerId })
        {
            try { await docker.Containers.InspectContainerAsync(id, deadline.Token); }
            catch (DockerApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { continue; }
            throw new InvalidOperationException("Stock proof owned container remains after joined cleanup.");
        }
        try { await docker.Networks.InspectNetworkAsync(resources.NetworkId, cancellationToken: deadline.Token); }
        catch (DockerApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return; }
        throw new InvalidOperationException("Stock proof owned network remains after joined cleanup.");
    }
}

internal sealed record StockProviderResources(string ProviderContainerId, string DatabaseContainerId,
    string NetworkId, string ImageId, string[] RepoDigests, string OperatingSystem, string Architecture,
    string? ImageDescriptorDigest, string? ImageDescriptorMediaType, string? PlatformManifestDigest);
