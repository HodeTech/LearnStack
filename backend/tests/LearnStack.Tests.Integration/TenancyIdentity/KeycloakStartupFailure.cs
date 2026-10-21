using System.Net;
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
        DockerApiException { StatusCode: HttpStatusCode.InternalServerError } => "docker_server_error",
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
}
