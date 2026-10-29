using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace LearnStack.Api.Idempotency;

/// <summary>ADR-0079's closed successful-receipt path, separate from generic idempotency semantics.</summary>
internal sealed partial class ProtectedIdempotencyFilter(IIdempotencyStore store, ITenantContext tenantContext,
    IIdempotencyBusinessOwnerCleanup cleanup, ISender sender,
    ILogger<ProtectedIdempotencyFilter> logger, ProtectedAdministrationOperation operation) : IAsyncResourceFilter
{
    internal const int MaxReceiptBytes = 1024;
    private readonly ProtectedAdministrationDescriptor _descriptor = ProtectedAdministrationDescriptor.For(operation);

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (!IdempotencyFilter.TryReadKey(context.HttpContext, out var key))
        {
            context.Result = new ProblemDetailsActionResult(new Error(LocalizedMessage.Of("lockey_validation_failed"),
                new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
                {
                    [IdempotentAttribute.ErrorsKey] = [LocalizedMessage.Of("lockey_idempotency_key_invalid")],
                }));
            return;
        }
        if (!tenantContext.IsResolved || tenantContext.TenantId == TenantId.PlatformSentinel || tenantContext.UserId is null
            || tenantContext.UserId == UserId.SystemActor)
        {
            context.Result = Problem("lockey_forbidden");
            return;
        }
        long? expected = null;
        long version = 0;
        if (_descriptor.Creates ? context.HttpContext.Request.Headers.IfMatch.Count != 0
            : !ProtectedEntityTag.TryRead(context.HttpContext.Request, out version))
        {
            context.Result = new ProblemDetailsActionResult(ProtectedEntityTag.Invalid());
            return;
        }
        if (!_descriptor.Creates) expected = version;
        if (!AdministrationHttpInput.SupportsJsonMedia(context.HttpContext.Request))
        {
            context.Result = AdministrationHttpInput.Invalid();
            return;
        }
        var cancellationToken = context.HttpContext.RequestAborted;
        await AdministrationHttpInput.ReadBodyBytesAsync(context.HttpContext);
        var fingerprint = await ComputeFingerprintAsync(context.HttpContext, expected, cancellationToken);
        var admission = ProtectedReplayHttpAdmission.Consume(context.HttpContext, tenantContext, _descriptor, key, fingerprint, expected);
        if (admission.Kind == ProtectedHttpAdmissionKind.Refused)
        {
            context.Result = Problem("lockey_forbidden");
            return;
        }
        if (admission.Kind == ProtectedHttpAdmissionKind.Replay)
        {
            // The retained snapshot is an as-of-read result. Expiry/recycling
            // after HTTP admission never turns disclosure authority into a claim.
            await ReplayAsync(context, admission.Response, cancellationToken);
            return;
        }
        IdempotencyClaimResult claim;
        try { claim = await store.TryClaimAsync(tenantContext.TenantId, key, fingerprint, cancellationToken); }
        catch (IdempotencyStoreException)
        {
            context.Result = Problem("lockey_dependency_unavailable");
            return;
        }
        switch (claim.Outcome)
        {
            case IdempotencyClaim.Completed:
                await ReplayAsync(context, claim.Stored, cancellationToken);
                return;
            case IdempotencyClaim.InFlight:
                context.Result = Problem("lockey_request_in_progress");
                return;
            case IdempotencyClaim.Mismatched:
                context.Result = Problem("lockey_idempotency_key_reuse");
                return;
            case IdempotencyClaim.Unreplayable:
                context.Result = Problem("lockey_idempotency_outcome_unavailable");
                return;
            case IdempotencyClaim.Acquired when claim.Token != Guid.Empty:
                await RunAsync(context, next, key, claim.Token, expected);
                return;
            default:
                context.Result = Problem("lockey_dependency_unavailable");
                return;
        }
    }

    internal async Task<string> ComputeFingerprintAsync(HttpContext context, long? expected, CancellationToken cancellationToken)
        => await ComputeFingerprintAsync(context, tenantContext, _descriptor, expected, cancellationToken);

    internal static async Task<string> ComputeFingerprintAsync(HttpContext context, ITenantContext tenantContext,
        ProtectedAdministrationDescriptor descriptor, long? expected, CancellationToken cancellationToken)
    {
        // The historical digest is deliberately unchanged, including its exact
        // actor/scope/method/path/query/body byte framing. Protected inputs wrap
        // it in a separate versioned domain and canonical precondition.
        var original = await IdempotencyFilter.ComputeFingerprintAsync(context, tenantContext, cancellationToken);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append("learnstack.idempotency.protected.v1");
        Append(descriptor.Operation.ToString());
        Append(expected is { } version ? EntityTag.For(version) : string.Empty);
        Append(original);
        return Convert.ToHexStringLower(digest.GetHashAndReset());

        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            digest.AppendData(length);
            digest.AppendData(bytes);
        }
    }

    private async Task ReplayAsync(ResourceExecutingContext context, IdempotentResponse? stored, CancellationToken cancellationToken)
    {
        if (stored is null || !ValidStoredResponse(stored, _descriptor, tenantContext.TenantId))
        {
            context.Result = Problem("lockey_dependency_unavailable");
            return;
        }
        // Send returns only after the ordinary MUST read transaction's commit.
        // No completed row is completed again, abandoned or otherwise changed.
        Result<None> disclosure;
        try
        {
            disclosure = await new ProtectedReceiptReplayDispatcher(sender)
                .ReadAsync(_descriptor, stored.ReplayTarget!, cancellationToken);
        }
        catch
        {
            await EndBusinessOwnerAsync();
            throw;
        }
        if (!await EndBusinessOwnerAsync())
        {
            context.Result = Problem("lockey_dependency_unavailable");
            return;
        }
        if (disclosure.IsFailure)
        {
            context.Result = new ProblemDetailsActionResult(disclosure.Error);
            return;
        }
        await IdempotencyFilter.ReplayAsync(context.HttpContext, stored, cancellationToken);
    }

    private async Task RunAsync(ResourceExecutingContext context, ResourceExecutionDelegate next, string key, Guid token, long? expected)
    {
        var response = context.HttpContext.Response;
        var original = response.Body;
        using var buffer = new ReceiptBuffer(response);
        response.Body = buffer;
        ResourceExecutedContext executed;
        try { executed = await next(); }
        catch
        {
            response.Body = original;
            if (await EndBusinessOwnerAsync()) await TryReleaseAfterFailureAsync(key, token);
            throw;
        }
        finally { response.Body = original; }

        // A detached transaction field is not cleanup. This awaited seam proves
        // the actual business transaction AND connection have ended before any
        // later outcome-store owner can begin, even on faults or disconnect.
        var ended = await EndBusinessOwnerAsync();
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            if (ended) await TryReleaseAfterFailureAsync(key, token);
            return;
        }
        if (!ended)
        {
            await WriteUnavailableAsync(context);
            return;
        }
        var successful = response.StatusCode is >= 200 and <= 299;
        IdempotentResponse? receipt = null;
        if (successful && buffer.TotalBytes <= MaxReceiptBytes
            && executed.Result is ProtectedAdministrationResult result && result.Matches(_descriptor, tenantContext, expected))
        {
            var proposed = new IdempotentResponse(response.StatusCode, response.ContentType,
                CaptureHeaders(response), buffer.Bytes, result.Target);
            if (ValidStoredResponse(proposed, _descriptor, tenantContext.TenantId)
                && proposed.Body.AsSpan().SequenceEqual(ProtectedAdministrationResult.BodyFor(result.Version))) receipt = proposed;
        }
        bool recorded;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            recorded = successful
                ? await store.CompleteAsync(tenantContext.TenantId, key, token, receipt, deadline.Token)
                : await store.AbandonAsync(tenantContext.TenantId, key, token, deadline.Token);
        }
        catch (Exception failure) when (failure is IdempotencyStoreException
            || failure is OperationCanceledException && deadline.IsCancellationRequested)
        {
            OutcomeUnavailable(logger);
            await WriteUnavailableAsync(context);
            return;
        }
        if (successful && (!recorded || receipt is null))
        {
            // Invalid/oversized success is tombstoned, not released, and its
            // malformed bytes never escape. A failed completion is not rollback.
            OutcomeUnavailable(logger);
            await WriteUnavailableAsync(context);
            return;
        }
        if (buffer.Overflowed)
        {
            await WriteUnavailableAsync(context);
            return;
        }
        await original.WriteAsync(buffer.Bytes, context.HttpContext.RequestAborted);
    }

    private async ValueTask<bool> EndBusinessOwnerAsync()
    {
        try { return await cleanup.EndBusinessOwnerAsync(); }
        catch
        {
            CleanupUnavailable(logger);
            return false;
        }
    }

    private async Task TryReleaseAfterFailureAsync(string key, Guid token)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await store.AbandonAsync(tenantContext.TenantId, key, token, deadline.Token); }
        catch (Exception failure) when (failure is IdempotencyStoreException
            || failure is OperationCanceledException && deadline.IsCancellationRequested)
        {
            OutcomeUnavailable(logger);
        }
    }

    internal static bool ValidStoredResponse(IdempotentResponse stored, ProtectedAdministrationDescriptor descriptor, TenantId tenant)
    {
        if (stored.Body is null || stored.Headers is null
            || stored.ReplayTarget is not { } target || target.Kind != descriptor.TargetKind || target.Id == Guid.Empty || target.Id.Version != 7
            || target.Kind == IdempotencyReplayTargetKind.Tenant && target.Id != tenant.Value
            || stored.StatusCode != (descriptor.Creates ? StatusCodes.Status201Created : StatusCodes.Status200OK)
            || stored.ContentType != "application/json; charset=utf-8" || stored.Body.Length > MaxReceiptBytes
            || !TryHeader(stored, "ETag", out var tag) || !ProtectedEntityTag.TryParse(tag, out var version)
            || !stored.Body.AsSpan().SequenceEqual(ProtectedAdministrationResult.BodyFor(version))) return false;
        if (descriptor.Creates ? !TryHeader(stored, "Location", out var location)
                || location != ProtectedAdministrationResult.LocationFor(target.Id)
            : stored.Headers.Keys.Any(name => name.Equals("Location", StringComparison.OrdinalIgnoreCase))) return false;
        var response = new DefaultHttpContext().Response;
        long bytes = stored.Body.Length;
        foreach (var (name, values) in stored.Headers)
        {
            if (string.IsNullOrEmpty(name) || name.Any(character => !char.IsAsciiLetterOrDigit(character)
                    && !"!#$%&'*+-.^_`|~".Contains(character))
                || values is null || values.Count == 0
                || values.Any(value => value is null || !JsonValue.IsStorableText(value) || value.Any(char.IsControl))) return false;
            response.Headers[name] = new Microsoft.Extensions.Primitives.StringValues(values.ToArray());
            bytes += Encoding.UTF8.GetByteCount(name);
            foreach (var value in values) bytes += Encoding.UTF8.GetByteCount(value);
        }
        // Stored exchange/framing headers cannot be smuggled through a corrupt
        // completed row. The same capture policy strips them before persistence.
        return bytes <= MaxReceiptBytes && CaptureHeaders(response).Count == stored.Headers.Count
            && TryHeader(stored, "Content-Type", out var contentType) && contentType == stored.ContentType;
    }

    private static bool TryHeader(IdempotentResponse response, string name, out string value)
    {
        value = string.Empty;
        var matches = response.Headers.Where(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Value is null || matches[0].Value.Count != 1) return false;
        value = matches[0].Value[0];
        return value is not null;
    }

    private static ProblemDetailsActionResult Problem(string code) => new(new Error(LocalizedMessage.Of(code)));

    internal static Dictionary<string, IReadOnlyList<string>> CaptureHeaders(HttpResponse response)
    {
        var headers = IdempotencyFilter.CaptureHeaders(response);
        // RFC hop-by-hop fields and Connection-nominated fields describe this
        // exchange. Keep the historical generic policy byte-compatible.
        headers.Remove("TE");
        headers.Remove("Proxy-Authenticate");
        headers.Remove("Proxy-Authorization");
        foreach (var value in response.Headers.Connection)
            foreach (var name in (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                headers.Remove(name);
        return headers;
    }

    private static async Task WriteUnavailableAsync(ResourceExecutingContext context)
    {
        var response = context.HttpContext.Response;
        var correlation = response.Headers[CorrelationHeaderMiddleware.HeaderName];
        response.Headers.Clear();
        if (correlation.Count > 0) response.Headers[CorrelationHeaderMiddleware.HeaderName] = correlation;
        await Problem("lockey_dependency_unavailable").ExecuteResultAsync(context);
    }

    [LoggerMessage(EventId = 3300, Level = LogLevel.Error, Message = "Protected idempotency outcome storage is unavailable.")]
    private static partial void OutcomeUnavailable(ILogger logger);
    [LoggerMessage(EventId = 3301, Level = LogLevel.Error, Message = "Protected idempotency business-owner cleanup is unconfirmed.")]
    private static partial void CleanupUnavailable(ILogger logger);

    /// <summary>Bounds successful receipt capture; non-success Problem Details keep their complete body.</summary>
    private sealed class ReceiptBuffer(HttpResponse response) : Stream
    {
        private readonly MemoryStream _bytes = new();
        internal long TotalBytes { get; private set; }
        internal bool Overflowed => TotalBytes != _bytes.Length;
        internal byte[] Bytes => _bytes.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => TotalBytes;
        public override long Position { get => TotalBytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            TotalBytes = Math.Min(long.MaxValue - buffer.Length, TotalBytes) + buffer.Length;
            var retained = response.StatusCode is >= 200 and <= 299
                ? Math.Min(buffer.Length, Math.Max(0, MaxReceiptBytes + 1 - (int)_bytes.Length)) : buffer.Length;
            if (retained > 0) _bytes.Write(buffer[..retained]);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _bytes.Dispose();
            base.Dispose(disposing);
        }
    }
}
