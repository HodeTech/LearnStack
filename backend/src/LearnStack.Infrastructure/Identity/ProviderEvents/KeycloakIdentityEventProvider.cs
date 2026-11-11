using System.Runtime.ExceptionServices;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Resilience;
using LearnStack.SharedKernel.Secrets;
using Polly;

namespace LearnStack.Infrastructure.Identity.ProviderEvents;

/// <summary>One retained HTTP operation; no provider body, credential or transport handle escapes.</summary>
internal sealed partial class KeycloakIdentityEventProvider : IIdentityEventProvider, IAsyncDisposable
{
    private static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(5);
    private const int MaximumBodyBytes = 1024 * 1024;
    private readonly object _gate = new();
    private readonly IdentityEventProviderOptions _options;
    private readonly ISecretProvider _secrets;
    private readonly IProviderResilience<IIdentityEventProvider> _resilience;
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private Owner? _owner;
    private bool _closed;
    private Task? _disposal;

    internal KeycloakIdentityEventProvider(IdentityEventProviderOptions options, ISecretProvider secrets,
        IProviderResilience<IIdentityEventProvider> resilience)
        : this(options, secrets, resilience, new SocketsHttpHandler()) { }

    // Controlled transports are a test seam, not a production certificate bypass.
    internal KeycloakIdentityEventProvider(IdentityEventProviderOptions options, ISecretProvider secrets,
        IProviderResilience<IIdentityEventProvider> resilience, HttpMessageHandler handler)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _resilience = resilience ?? throw new ArgumentNullException(nameof(resilience));
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is SocketsHttpHandler sockets)
        {
            if (sockets.SslOptions.RemoteCertificateValidationCallback is not null)
                throw new InvalidOperationException("Identity event source certificate validation cannot be replaced.");
            sockets.AllowAutoRedirect = false; sockets.UseCookies = false; sockets.UseProxy = false;
            sockets.Credentials = null; sockets.DefaultProxyCredentials = null;
            sockets.ConnectTimeout = ReadBudget; sockets.MaxConnectionsPerServer = 1;
            sockets.MaxResponseHeadersLength = 32;
            // Do not let Dispose start an unowned asynchronous response drain.
            sockets.ResponseDrainTimeout = TimeSpan.Zero; sockets.MaxResponseDrainSize = 0;
        }
        _client = new(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaximumBodyBytes };
    }

    public Task<IdentityEventSourcePolicy> ReadPolicyAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async (owner, token) =>
        {
            var bytes = await ReadAuthenticatedAsync(owner, "events/config", token).ConfigureAwait(false);
            try
            {
                return bytes is null ? throw new IdentityEventProviderException(IdentityEventProviderFailure.Policy)
                    : KeycloakEventNormalization.ReadPolicy(bytes)
                        ?? throw new IdentityEventProviderException(IdentityEventProviderFailure.Policy);
            }
            finally { if (bytes is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }, cancellationToken);

    public Task<IdentityEventPage> ReadAsync(IdentityEventRead request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(async (owner, token) =>
        {
            var query = "events?dateFrom=" + request.From.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "&dateTo=" + request.To.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "&first=" + request.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "&max=" + request.Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture) + "&direction=asc"
                + string.Concat(IdentityEventKindCodes.RequiredCodes.Order(StringComparer.Ordinal).Select(code => "&type=" + Uri.EscapeDataString(code)));
            var bytes = await ReadAuthenticatedAsync(owner, query, token).ConfigureAwait(false);
            try { return bytes is null ? IdentityEventPage.Unreadable(IdentityEventPageIssue.Oversized) : KeycloakEventNormalization.ReadPage(bytes, request, _options); }
            finally { if (bytes is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }, cancellationToken);
    }

    private async Task<T> RunAsync<T>(Func<Owner, CancellationToken, Task<T>> operation, CancellationToken caller)
    {
        LearnStack.SharedKernel.Jobs.JobApplicationSourceOwner.RefuseUnrelatedRead();
        Owner owner;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_owner is not null) throw new IdentityEventProviderException(IdentityEventProviderFailure.Capacity);
            owner = new(); _owner = owner;
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(caller, _lifetime.Token);
        budget.CancelAfter(ReadBudget);
        ExceptionDispatchInfo? failure = null;
        T result = default!;
        try
        {
            result = await _resilience.Pipeline.ExecuteAsync(async token =>
            {
                Task<T> actual;
                lock (owner.Gate)
                {
                    if (owner.Unconfirmed || owner.Actual is { IsCompleted: false })
                        throw new IdentityEventProviderException(IdentityEventProviderFailure.Capacity);
                    if (owner.Actual is { IsFaulted: true } previous) _ = previous.Exception;
                    actual = TranslatedAsync(operation, owner, token);
                    owner.Actual = actual;
                }
                return await actual.ConfigureAwait(false);
            }, budget.Token).ConfigureAwait(false);
            budget.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException error) when (caller.IsCancellationRequested || _lifetime.IsCancellationRequested)
        { failure = ExceptionDispatchInfo.Capture(error); }
        catch (IdentityEventProviderException error) { failure = ExceptionDispatchInfo.Capture(error); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { failure = ExceptionDispatchInfo.Capture(new IdentityEventProviderException(IdentityEventProviderFailure.Unavailable)); }

        // Cancellation/resilience completion is not cleanup acknowledgement.
        // Join concrete HTTP work before freeing admission, including test
        // transports that ignore their cancellation token.
        if (owner.Actual is { } active)
            try { await active.ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
        lock (_gate)
        {
            if (owner.Unconfirmed)
            {
                _closed = true;
                var cleanup = new IdentityEventProviderException(IdentityEventProviderFailure.Unavailable);
                owner.Completion.TrySetException(cleanup);
                failure ??= ExceptionDispatchInfo.Capture(cleanup);
            }
            else
            {
                _owner = null;
                owner.Completion.TrySetResult();
            }
        }
        failure?.Throw();
        return result;
    }

    private static async Task<T> TranslatedAsync<T>(Func<Owner, CancellationToken, Task<T>> operation, Owner owner, CancellationToken token)
    {
        try { return await operation(owner, token).ConfigureAwait(false); }
        catch (IdentityEventProviderException) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { throw new IdentityEventProviderException(IdentityEventProviderFailure.Unavailable); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _closed = true;
            return new(_disposal ??= DisposeCoreAsync(_owner));
        }
    }

    private async Task DisposeCoreAsync(Owner? owner)
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (owner is not null) await owner.Completion.Task.ConfigureAwait(false);
        _client.Dispose(); _lifetime.Dispose();
    }

    private sealed class Owner
    {
        internal object Gate { get; } = new();
        internal Task? Actual { get; set; }
        internal HttpRequestMessage? Request { get; set; }
        internal HttpResponseMessage? Response { get; set; }
        internal Stream? Stream { get; set; }
        internal bool Unconfirmed { get; set; }
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
