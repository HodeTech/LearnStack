using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Permissions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>The four retained owners of the independent, guarded authorization lane.</summary>
/// <remarks>No datasource alias or caller-supplied statement/callback is published.</remarks>
public sealed class IdentityAuthorizationReadSource : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly NpgsqlDataSource _source;
    private readonly int _capacity;
    private readonly HashSet<AuthorizationReadOwner> _owners = [];
    private Task? _qualification;
    private DatabaseQualificationProof? _businessProof;
    private Task? _disposal;
    private IdentityAuthorityId? _authority;
    private string? _issuer;
    private bool _qualified;
    private bool _closed;
    private long _generation;

    public IdentityAuthorizationReadSource(string connectionString) : this(connectionString, 4, null) { }

    internal IdentityAuthorizationReadSource(string connectionString, int capacity, ILoggerFactory? loggerFactory = null)
    {
        if (capacity is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(capacity));
        AuthorizationSourceConfiguration.ValidateApplicationConnection(connectionString, "ConnectionStrings:IdentityAuthorization");
        var parsed = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = capacity,
            Multiplexing = false,
            Enlist = false,
        };
        _capacity = capacity;
        // Npgsql's physical initializer has no operation token. This private source instead
        // applies the shared guard at every checkout, before any candidate transaction exists.
        var builder = new NpgsqlDataSourceBuilder(parsed.ConnectionString);
        if (loggerFactory is not null) builder.UseLoggerFactory(loggerFactory);
        try { _source = builder.Build(); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            throw new InvalidOperationException("ConnectionStrings:IdentityAuthorization must configure a valid guarded application-role connection string.");
        }
    }

    public bool IsQualified { get { lock (_gate) return _qualified && !_closed; } }

    /// <summary>The actual server database observed at qualification, independently of a PgBouncer alias.</summary>
    public string? QualifiedDatabaseName { get; private set; }

    public Task QualifyAsync(IdentityAuthorityId authorityId, string issuer, CancellationToken cancellationToken = default) =>
        QualifyCoreEntryAsync(authorityId, issuer, null, cancellationToken);

    public Task QualifyAsync(IdentityAuthorityId authorityId, string issuer, DatabaseQualificationProof businessProof,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(businessProof);
        businessProof.RequireActive();
        return QualifyCoreEntryAsync(authorityId, issuer, businessProof, cancellationToken);
    }

    private Task QualifyCoreEntryAsync(IdentityAuthorityId authorityId, string issuer,
        DatabaseQualificationProof? businessProof, CancellationToken cancellationToken)
    {
        if (!StronglyTypedId.IsAssigned(authorityId) || string.IsNullOrWhiteSpace(issuer))
            throw new ArgumentException("Identity authorization qualification is invalid.");
        JobApplicationSourceOwner.RefuseUnrelatedRead();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_qualification is not null)
            {
                if (_authority != authorityId || !string.Equals(_issuer, issuer, StringComparison.Ordinal)
                    || !ReferenceEquals(_businessProof, businessProof))
                    throw new InvalidOperationException("Identity authorization qualification cannot be retargeted.");
                return _qualification;
            }
            _authority = authorityId;
            _issuer = issuer;
            _businessProof = businessProof;
            return _qualification = QualifyCoreAsync(authorityId, issuer, businessProof, cancellationToken);
        }
    }

    public async Task<AuthorizationReadOwner?> OpenAsync(AuthorizationCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        JobApplicationSourceOwner.RefuseExternalEntry();
        if (!candidate.IsActive) throw new InvalidOperationException("Authorization candidate owner is closed.");
        var deadline = NewDeadline(cancellationToken);
        AuthorizationReadOwner? owner;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (!_qualified) throw new InvalidOperationException("Identity authorization source is not qualified.");
                deadline.Token.ThrowIfCancellationRequested();
                if (_owners.Count == _capacity) { deadline.Dispose(); return null; }
                var jobResources = JobApplicationSourceOwner.Current?.BeginAuthorizationRead();
                owner = RetainOwner(candidate, deadline, jobResources);
            }
        }
        catch { deadline.Dispose(); throw; }
        try
        {
            await owner.OpenAsync().ConfigureAwait(false);
            return owner;
        }
        catch
        {
            try { await owner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception)
            {
                // Actual failed cleanup remains retained; preserve the original open failure.
            }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _closed = true;
            return new ValueTask(_disposal ??= DisposeCoreAsync([.. _owners]));
        }
    }

    internal void Release(AuthorizationReadOwner owner)
    {
        lock (_gate) _owners.Remove(owner);
    }

    private AuthorizationReadOwner RetainOwner(AuthorizationCandidate? candidate, CancellationTokenSource deadline,
        JobApplicationAuxiliaryResources? jobResources)
    {
        var owner = new AuthorizationReadOwner(this, _source, candidate, deadline, checked(++_generation), jobResources);
        _owners.Add(owner);
        return owner;
    }

    private async Task QualifyCoreAsync(IdentityAuthorityId authorityId, string issuer,
        DatabaseQualificationProof? businessProof, CancellationToken cancellationToken)
    {
        // Qualification is itself a retained owner, with no user-directory or candidate access.
        var owner = RetainOwner(null, NewDeadline(cancellationToken), null);
        await using (owner.ConfigureAwait(false))
        {
            await owner.OpenAsync().ConfigureAwait(false);
            QualifiedDatabaseName = await owner.QualifyAsync(authorityId, issuer, businessProof).ConfigureAwait(false);
            await owner.CompleteAsync().ConfigureAwait(false);
        }
        businessProof?.RequireActive();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _qualified = true;
        }
    }

    private async Task DisposeCoreAsync(AuthorizationReadOwner[] owners)
    {
        // Neither deadline expiry nor a second Dispose supplies positive cleanup evidence.
        // A failed actual cleanup faults this join and keeps the pool and retained owner alive.
        await Task.WhenAll(owners.Select(owner => owner.ActualCompletion)).ConfigureAwait(false);
        await _source.DisposeAsync().ConfigureAwait(false);
    }

    private static CancellationTokenSource NewDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        return deadline;
    }
}
