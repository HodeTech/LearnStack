using System.Net;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.SharedKernel.Audit;

/// <summary>The admitted HTTP adapter's descriptive transport snapshot for one exact act.</summary>
public sealed class OperationTransportProvenance
{
    private readonly OperationInvocation _invocation;
    private readonly MintingSource _source;
    private readonly IPAddress? _address;

    private OperationTransportProvenance(MintingSource source, OperationInvocation invocation,
        string route, IPAddress? address, string? userAgent)
    {
        if (!invocation.IsActive || invocation.Candidate is not { IsActive: true, SourceKind: AuthorizationSourceKind.Http }
            || string.IsNullOrWhiteSpace(route) || route.Length > 128 || route.Any(char.IsControl))
            throw new ArgumentException("HTTP authorization provenance is invalid.");
        _source = source;
        _invocation = invocation;
        Route = route;
        _address = Copy(address);
        UserAgent = userAgent is null ? null : new string(userAgent.Where(character => !char.IsControl(character)).Take(256).ToArray());
    }

    public string Route { get; }
    public IPAddress? Address => Copy(_address);
    public string? UserAgent { get; }
    public bool Matches(OperationInvocation invocation) => _source.IsOpen && ReferenceEquals(_invocation, invocation) && invocation.IsActive;
    public override string ToString() => nameof(OperationTransportProvenance);
    private static IPAddress? Copy(IPAddress? address) => address is null ? null : new IPAddress(address.GetAddressBytes());

#pragma warning disable CA1034 // The compiled HTTP source census owns this minting capability.
    public abstract class MintingSource : IDisposable
#pragma warning restore CA1034
    {
        private int _closed;
        internal bool IsOpen => Volatile.Read(ref _closed) == 0;
        protected OperationTransportProvenance ForTransport(OperationInvocation invocation, string route,
            IPAddress? address, string? userAgent)
        {
            ObjectDisposedException.ThrowIf(!IsOpen, this);
            return new(this, invocation, route, address, userAgent);
        }
        public void Dispose() { Interlocked.Exchange(ref _closed, 1); GC.SuppressFinalize(this); }
    }
}

public interface IAuthorizationTransportSource
{
    OperationTransportProvenance? Capture(OperationInvocation invocation);
}
