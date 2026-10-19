using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LearnStack.Tests.Integration;

/// <summary>Real HTTP/2 gRPC receiver for the production exporters, without a replacement logging sink.</summary>
internal sealed class TenantAuthenticationDiagnosticCollector : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<(string Path, string Payload)> _requests = new();
    private TenantAuthenticationDiagnosticCollector(WebApplication app) => _app = app;
    internal string Endpoint { get; private set; } = "";
    internal string Logs => Payloads("/opentelemetry.proto.collector.logs.v1.LogsService/Export");
    internal string Traces => Payloads("/opentelemetry.proto.collector.trace.v1.TraceService/Export");
    internal string Metrics => Payloads("/opentelemetry.proto.collector.metrics.v1.MetricsService/Export");
    internal string AllPayloads => string.Join('\n', _requests.Select(request => request.Payload));

    internal static async Task<TenantAuthenticationDiagnosticCollector> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        var app = builder.Build();
        var receiver = new TenantAuthenticationDiagnosticCollector(app);
        app.Run(receiver.Receive);
        await app.StartAsync();
        receiver.Endpoint = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        return receiver;
    }

    private string Payloads(string path) => string.Join('\n', _requests
        .Where(request => request.Path == path).Select(request => request.Payload));

    private async Task Receive(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        var data = body.ToArray();
        var offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 5) throw new InvalidDataException("Incomplete gRPC frame.");
            var compressed = data[offset] != 0;
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 1, 4));
            offset += 5;
            if (length < 0 || length > data.Length - offset) throw new InvalidDataException("Invalid gRPC frame length.");
            var payload = data.AsSpan(offset, length).ToArray();
            if (compressed)
            {
                if (context.Request.Headers["grpc-encoding"] != "gzip")
                    throw new InvalidDataException("Unexpected gRPC compression.");
                using var source = new MemoryStream(payload);
                using var gzip = new GZipStream(source, CompressionMode.Decompress);
                using var decoded = new MemoryStream();
                await gzip.CopyToAsync(decoded, context.RequestAborted);
                payload = decoded.ToArray();
            }
            _requests.Enqueue((context.Request.Path.Value!, Encoding.UTF8.GetString(payload)));
            offset += length;
        }
        context.Response.ContentType = "application/grpc";
        context.Response.DeclareTrailer("grpc-status");
        await context.Response.Body.WriteAsync(new byte[5], context.RequestAborted);
        context.Response.AppendTrailer("grpc-status", "0");
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
