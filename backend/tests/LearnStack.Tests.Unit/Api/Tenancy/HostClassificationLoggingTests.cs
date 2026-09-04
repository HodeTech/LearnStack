using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Tenancy;

/// <summary>
/// What a refused host is written to the log at.
/// </summary>
/// <remarks>
/// <para>
/// The level is load-bearing rather than a preference. The <c>Host</c> header is
/// attacker-authored on every anonymous request, and
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
/// keeps attacker-authored strings out of anything retained — it refuses to put
/// them in <c>audit_log</c>, and an <c>Information</c> line an operator forwards to
/// a shared sink is the same exposure by another route. A well-meaning bump "for
/// observability" passed the entire suite before this case existed.
/// </para>
/// <para>
/// Driven against the middleware directly rather than through the host: Serilog is
/// wired with <c>UseSerilog</c> and no <c>writeToProviders</c>, so an
/// <c>ILoggerProvider</c> registered in a <c>WebApplicationFactory</c>'s test
/// services receives nothing — measured, the capture came back empty. The counter
/// half of the same rejection path is asserted through the real pipeline in
/// <c>HostClassificationHttpTests</c>.
/// </para>
/// </remarks>
public sealed class HostClassificationLoggingTests
{
    [Fact]
    public async Task A_Refused_Host_Is_Logged_No_Higher_Than_Debug()
    {
        var logger = new CapturingLogger();
        var middleware = Build(logger);
        var context = ContextFor("stranger.example.com");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);

        var entry = logger.Entries.Should().ContainSingle(
            captured => captured.Message.Contains("stranger.example.com", StringComparison.Ordinal))
            .Subject;

        entry.Level.Should().Be(LogLevel.Debug,
            "the rejected host is attacker-authored and must not reach a retained sink");
    }

    [Fact]
    public async Task A_Host_That_Names_Nothing_Never_Reaches_The_Log_At_All()
    {
        // The unnamed branch logs the literal "unnamed" and not the header, so a
        // 300-character or percent-escaped Host cannot be written anywhere by this
        // middleware even at Debug.
        var logger = new CapturingLogger();
        var middleware = Build(logger);
        var context = ContextFor("ex%41mple.com");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        logger.Entries.Should().OnlyContain(
            captured => !captured.Message.Contains("ex%41mple.com", StringComparison.Ordinal));
    }

    private static HostClassificationMiddleware Build(ILogger<HostClassificationMiddleware> logger)
    {
        var meterFactory = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();

        return new HostClassificationMiddleware(
            _ => Task.CompletedTask,
            new EffectiveHostAccessor(Options.Create(new TrustedHopOptions())),
            new NeverResolvesResolver(),
            new PlatformHostOptions(),
            logger,
            meterFactory);
    }

    private static DefaultHttpContext ContextFor(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/anything";
        context.Request.Headers.Host = host;
        return context;
    }

    private sealed class NeverResolvesResolver : IHostToTenantResolver
    {
        public Task<HostResolution?> ResolveAsync(
            string host, CancellationToken cancellationToken = default) =>
            Task.FromResult<HostResolution?>(null);
    }

    private sealed class CapturingLogger : ILogger<HostClassificationMiddleware>
    {
        private readonly List<Captured> _entries = [];

        public IReadOnlyList<Captured> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        // Deliberately always enabled: the assertion is about the level the
        // middleware CHOOSES, and a logger that filtered would hide exactly the
        // change this case exists to catch.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Add(new Captured(logLevel, formatter(state, exception)));
        }

        internal sealed record Captured(LogLevel Level, string Message);
    }
}
