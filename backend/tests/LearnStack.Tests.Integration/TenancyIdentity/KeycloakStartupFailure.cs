using System.Net;
using System.Text.Json;
using Docker.DotNet;
using DotNet.Testcontainers.Containers;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Pure finite diagnostics; this type never starts or owns provider resources.</summary>
internal static class KeycloakStartupFailure
{
    internal static string Classify(Exception exception) => exception switch
    {
        ContainerNotRunningException => "container_exit",
        OperationCanceledException => "deadline",
        TimeoutException => "readiness_timeout",
        HttpRequestException => "http_transport",
        DockerApiException { StatusCode: HttpStatusCode.BadRequest } => "docker_bad_request",
        DockerApiException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "docker_access_denied",
        DockerApiException { StatusCode: HttpStatusCode.NotFound } => "docker_not_found",
        DockerApiException { StatusCode: HttpStatusCode.Conflict } => "docker_conflict",
        DockerApiException { StatusCode: HttpStatusCode.TooManyRequests } => "docker_rate_limited",
        DockerApiException { StatusCode: HttpStatusCode.InternalServerError } docker => ClassifyServerError(docker.ResponseBody),
        DockerApiException => "docker_api_error",
        ResourceReaperException => "resource_reaper",
        System.Net.Sockets.SocketException => "socket_error",
        UnauthorizedAccessException => "filesystem_access_denied",
        IOException => "filesystem_io",
        ArgumentException => "argument",
        InvalidOperationException => "invalid_state",
        NotSupportedException => "unsupported_operation",
        KeyNotFoundException => "missing_mapping",
        _ => "provider_error",
    };

    private static string ClassifyServerError(string? responseBody)
    {
        if (responseBody is null || responseBody.Length > 16384)
            return "docker_server_error";

        try
        {
            using var body = JsonDocument.Parse(responseBody, new JsonDocumentOptions { MaxDepth = 2 });
            if (body.RootElement.ValueKind != JsonValueKind.Object)
                return "docker_server_error";

            string? message = null;
            foreach (var property in body.RootElement.EnumerateObject())
            {
                if (message is not null || !property.NameEquals("message")
                    || property.Value.ValueKind != JsonValueKind.String)
                    return "docker_server_error";
                message = property.Value.GetString();
            }

            if (message is null)
                return "docker_server_error";

            // Moby 28.0.4 (6430e49a) / 28.5.2 (89c5e8fd): daemon/start_linux.go,
            // libnetwork/drivers/bridge/port_mapping_linux.go and portallocator/portallocator.go.
            // These select fixed daemon failure families, never a cause or ownership claim.
            const string networkPrefix = "failed to set up container networking:";
            const string taskPrefix = "failed to create task for container:";
            if (message.StartsWith(networkPrefix, StringComparison.Ordinal))
            {
                if (message.LastIndexOf(networkPrefix, StringComparison.Ordinal) != 0)
                    return "docker_server_error";
                if (message.Contains("failed: port is already allocated", StringComparison.Ordinal))
                    return "docker_port_allocated";
                if (message.Contains("failed to listen on TCP socket:", StringComparison.Ordinal))
                    return "docker_port_listen";
                if (message.Contains("failed to start userland proxy for port mapping ", StringComparison.Ordinal))
                    return "docker_port_proxy";
                if (message.Contains("failed to bind host port ", StringComparison.Ordinal))
                    return "docker_port_bind";
                return "docker_network_error";
            }

            if (message.StartsWith(taskPrefix, StringComparison.Ordinal)
                && message.LastIndexOf(taskPrefix, StringComparison.Ordinal) == 0)
                return "docker_runtime_task";
            return "docker_server_error";
        }
        catch (JsonException)
        {
            return "docker_server_error";
        }
        catch (InvalidOperationException)
        {
            return "docker_server_error";
        }
    }
}
