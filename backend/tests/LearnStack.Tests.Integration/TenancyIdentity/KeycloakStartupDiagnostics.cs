using System.Globalization;
using System.Security.Authentication;
using DotNet.Testcontainers.Containers;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>In-memory startup observations only; never readiness, ownership or root-cause evidence.</summary>
internal sealed class KeycloakStartupDiagnostics(TimeProvider? timeProvider = null)
{
    internal const int MaximumCount = 1_000_000;
    internal const int MaximumMilliseconds = 600_000;
    internal const string DataKey = "learnstack.keycloak.startup.observation";
    internal static readonly string[] Milestones =
    [
        "certificate", "network", "database", "database_create_request", "database_start",
        "database_readiness", "database_started", "import_input", "provider_start", "provider_created",
        "provider_starting", "provider_started", "mapped_endpoint", "loopback_bindings", "discovery",
    ];

    private static int _sequence;
    private readonly object _gate = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly long _started = (timeProvider ?? TimeProvider.System).GetTimestamp();
    private readonly int _ordinal = NextOrdinal();
    private readonly int[] _milliseconds = Enumerable.Repeat(-1, Milestones.Length).ToArray();
    private int _attempts;
    private int _completed;
    private KeycloakReadinessOutcome _outcome;

    internal void Mark(string stage)
    {
        var index = Array.IndexOf(Milestones, stage);
        if (index < 0) return;
        lock (_gate) _milliseconds[index] = ElapsedMilliseconds();
    }

    internal HttpMessageHandler Observe(HttpMessageHandler inner) => new ReadinessHandler(this, inner);

    internal KeycloakStartupObservation Snapshot(TestcontainersStates? cachedState)
    {
        lock (_gate)
            return new KeycloakStartupObservation(_ordinal, ElapsedMilliseconds(),
                Array.AsReadOnly((int[])_milliseconds.Clone()), _attempts, _completed, _outcome,
                CachedStateLabel(cachedState));
    }

    private int ElapsedMilliseconds() =>
        (int)Math.Clamp(_clock.GetElapsedTime(_started).TotalMilliseconds, 0, MaximumMilliseconds);

    private static int NextOrdinal()
    {
        while (true)
        {
            var current = Volatile.Read(ref _sequence);
            var next = Math.Min(current + 1, MaximumCount);
            if (Interlocked.CompareExchange(ref _sequence, next, current) == current) return next;
        }
    }

    private void Starting()
    {
        lock (_gate)
        {
            _attempts = Math.Min(_attempts + 1, MaximumCount);
            _outcome = KeycloakReadinessOutcome.Pending;
        }
    }

    private void Completed(KeycloakReadinessOutcome outcome)
    {
        lock (_gate)
        {
            _completed = Math.Min(_completed + 1, MaximumCount);
            _outcome = outcome;
        }
    }

    private static KeycloakCachedProviderState CachedStateLabel(TestcontainersStates? state) => state switch
    {
        null => KeycloakCachedProviderState.NotCreated,
        TestcontainersStates.Undefined => KeycloakCachedProviderState.Undefined,
        TestcontainersStates.Created => KeycloakCachedProviderState.Created,
        TestcontainersStates.Restarting => KeycloakCachedProviderState.Restarting,
        TestcontainersStates.Running => KeycloakCachedProviderState.Running,
        TestcontainersStates.Paused => KeycloakCachedProviderState.Paused,
        TestcontainersStates.Exited => KeycloakCachedProviderState.Exited,
        TestcontainersStates.Dead => KeycloakCachedProviderState.Dead,
        _ => KeycloakCachedProviderState.Unknown,
    };

    private sealed class ReadinessHandler(KeycloakStartupDiagnostics owner, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.Starting();
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                owner.Completed((int)response.StatusCode switch
                {
                    200 => KeycloakReadinessOutcome.HttpOk,
                    >= 200 and < 300 => KeycloakReadinessOutcome.HttpOtherSuccess,
                    >= 300 and < 400 => KeycloakReadinessOutcome.HttpRedirect,
                    >= 400 and < 500 => KeycloakReadinessOutcome.HttpClientError,
                    >= 500 and < 600 => KeycloakReadinessOutcome.HttpServerError,
                    _ => KeycloakReadinessOutcome.HttpOther,
                });
                return response;
            }
            catch (Exception exception)
            {
                owner.Completed(exception switch
                {
                    OperationCanceledException => KeycloakReadinessOutcome.Cancelled,
                    HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => KeycloakReadinessOutcome.Tls,
                    HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => KeycloakReadinessOutcome.Connect,
                    HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => KeycloakReadinessOutcome.Dns,
                    HttpRequestException => KeycloakReadinessOutcome.Transport,
                    AuthenticationException => KeycloakReadinessOutcome.Tls,
                    _ => KeycloakReadinessOutcome.Other,
                });
                throw;
            }
        }
    }
}

internal enum KeycloakReadinessOutcome
{
    NotAttempted, Pending, HttpOk, HttpOtherSuccess, HttpRedirect, HttpClientError, HttpServerError,
    HttpOther, Cancelled, Tls, Connect, Dns, Transport, Other,
}

internal enum KeycloakCachedProviderState
{
    NotCreated, Undefined, Created, Restarting, Running, Paused, Exited, Dead, Unknown,
}

internal sealed record KeycloakStartupObservation(int FixtureOrdinal, int ElapsedMilliseconds,
    IReadOnlyList<int> MilestoneMilliseconds, int ReadinessAttempts, int ReadinessCompleted,
    KeycloakReadinessOutcome ReadinessOutcome, KeycloakCachedProviderState CachedProviderState)
{
    internal string ToSentence()
    {
        var outcome = ReadinessOutcome switch
        {
            KeycloakReadinessOutcome.NotAttempted => "not_attempted",
            KeycloakReadinessOutcome.Pending => "pending",
            KeycloakReadinessOutcome.HttpOk => "http_ok",
            KeycloakReadinessOutcome.HttpOtherSuccess => "http_other_success",
            KeycloakReadinessOutcome.HttpRedirect => "http_redirect",
            KeycloakReadinessOutcome.HttpClientError => "http_client_error",
            KeycloakReadinessOutcome.HttpServerError => "http_server_error",
            KeycloakReadinessOutcome.HttpOther => "http_other",
            KeycloakReadinessOutcome.Cancelled => "cancelled",
            KeycloakReadinessOutcome.Tls => "tls",
            KeycloakReadinessOutcome.Connect => "connect",
            KeycloakReadinessOutcome.Dns => "dns",
            KeycloakReadinessOutcome.Transport => "transport",
            _ => "other",
        };
        var cachedState = CachedProviderState switch
        {
            KeycloakCachedProviderState.NotCreated => "not_created",
            KeycloakCachedProviderState.Undefined => "undefined",
            KeycloakCachedProviderState.Created => "created",
            KeycloakCachedProviderState.Restarting => "restarting",
            KeycloakCachedProviderState.Running => "running",
            KeycloakCachedProviderState.Paused => "paused",
            KeycloakCachedProviderState.Exited => "exited",
            KeycloakCachedProviderState.Dead => "dead",
            _ => "unknown",
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"Keycloak startup observation v1; fixture={FixtureOrdinal}; elapsedMs={ElapsedMilliseconds}; milestones={string.Join(',', MilestoneMilliseconds.Select(value => value.ToString(CultureInfo.InvariantCulture)))}; attempts={ReadinessAttempts}; completed={ReadinessCompleted}; readiness={outcome}; cachedProvider={cachedState}.");
    }
}
