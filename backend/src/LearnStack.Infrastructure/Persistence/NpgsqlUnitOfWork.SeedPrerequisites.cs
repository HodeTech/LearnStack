using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LearnStack.Infrastructure.Persistence;

public sealed partial class NpgsqlUnitOfWork
{
    private enum SeedReadState { Narrow, Enabling, Verifying, Clearing, Faulted }

    private readonly ISeedPrerequisiteReadOperationResolver? _seedPrerequisiteReads;
    private readonly ISeedCompletionReadOperationResolver? _seedCompletionReads;
    private readonly Dictionary<DbContext, long> _moduleContexts = new(ReferenceEqualityComparer.Instance);
    private SeedReadState _seedReadState;
    private IFixedSeedRead? _seedReadWitness;
    private TenantContextAnnouncement? _seedReadAnnouncement;

    private bool HasFixedSeedRead => _seedReadState is SeedReadState.Enabling
        or SeedReadState.Verifying or SeedReadState.Clearing;

    private bool IsSeedTenantWideRead => _seedReadState == SeedReadState.Verifying && ValidateFixedSeedRead();

    public Task<bool> VerifySeedPrerequisitesAsync(SeedPrerequisiteReadWitness witness,
        CancellationToken cancellationToken = default) =>
        VerifyFixedSeedReadAsync(new PrerequisiteSeedRead(witness, _seedPrerequisiteReads), cancellationToken);

    public Task<bool> VerifySeedCompletionAsync(SeedCompletionReadWitness witness,
        CancellationToken cancellationToken = default) =>
        VerifyFixedSeedReadAsync(new CompletionSeedRead(witness, _seedCompletionReads), cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Every failed scope transition actually rolls back/disposes the retained owner before rethrowing its original failure.")]
    private async Task<bool> VerifyFixedSeedReadAsync(IFixedSeedRead witness, CancellationToken cancellationToken)
    {
        JobApplicationSourceOwner.RefuseExternalEntry();
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var announced = _tenantContextAnnouncement;
            if (!witness.IsConfigured || _seedReadState != SeedReadState.Narrow
                || _seedReadWitness is not null || _transaction is not { IsolationLevel: IsolationLevel.ReadCommitted }
                || _mode != TransactionMode.ReadWrite || _depth != 1 || _rollbackOnly || !_tenantContextIssued
                || announced is not { Generation: > 0 }
                || announced.Generation != _generation || announced.TenantId != witness.TenantId
                || announced.OrganizationId != witness.OrganizationId || _tenantWideRead || _readScopeFaulted
                || _readWitnesses.Count != 0 || IsGlobalIdentityTransaction || _selfDiscoveryWitness is not null
                || _openingSelfDiscovery || _jobSourceOwner is not null || JobApplicationSourceOwner.Current is not null)
                RefuseFixedSeedRead();

            cancellationToken.ThrowIfCancellationRequested();
            witness.RequireCurrent();
            RequireCleanSeedContexts();
            // Resolution happens narrow: construction may resolve a same-owner module
            // context. The resolver supplies one compiled operation, never caller work.
            if (!witness.Resolve() || !witness.TryBindTo(this, _transaction!, _generation))
                RefuseFixedSeedRead();
            RequireCleanSeedContexts();
            _seedReadWitness = witness;
            _seedReadAnnouncement = announced;
            _seedReadState = SeedReadState.Enabling;
            RequireCurrentFixedSeedRead(witness, cancellationToken);

            await ExecuteAsync("SELECT set_config('app.scope', 'tenant', true)", cancellationToken);
            RequireCurrentFixedSeedRead(witness, cancellationToken);
            _seedReadState = SeedReadState.Verifying;

            var admitted = await witness.ExecuteAsync(cancellationToken);
            RequireCurrentFixedSeedRead(witness, cancellationToken);
            RequireCleanSeedContexts();
            _seedReadState = SeedReadState.Clearing;

            await ExecuteAsync("SELECT set_config('app.scope', '', true)", cancellationToken);
            RequireCurrentFixedSeedRead(witness, cancellationToken);
            RequireCleanSeedContexts();
            _seedReadState = SeedReadState.Narrow;
            // Do not release evidence until the matching EF view is narrow and
            // the exact plan/source is rechecked after acknowledged restoration.
            witness.RequireCurrent();
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_tenantContextAnnouncement, announced)
                || !witness.IsBoundTo(this, _transaction!, _generation) || _rollbackOnly)
                RefuseFixedSeedRead();
            return admitted;
        }
        catch
        {
            PoisonFixedSeedRead();
            try { await RollbackCoreAsync(); }
            catch { LogSetupCleanupFailure(_logger, null); }
            throw;
        }
    }

    public void RequireWriteAllowed()
    {
        JobApplicationSourceOwner.RefuseExternalEntry();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_seedReadState != SeedReadState.Narrow) RefuseFixedSeedRead();
    }

    public void RegisterModuleContext(object context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context is not DbContext db || _transaction is null
            || !ReferenceEquals(db.Database.GetDbConnection(), _connection)
            || !ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), _transaction))
        {
            if (HasFixedSeedRead || _seedReadState == SeedReadState.Faulted) RefuseFixedSeedRead();
            throw new InvalidOperationException("A module context must enroll on its actual physical owner.");
        }
        if (_moduleContexts.TryAdd(db, _generation))
        {
            // State changes are caught immediately; ordinary property changes are
            // caught by explicit DetectChanges at entry, commands and restoration.
            db.ChangeTracker.Tracked += (_, args) =>
            {
                if (args.Entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                    RequireWriteAllowed();
            };
            db.ChangeTracker.StateChanged += (_, args) =>
            {
                if (args.NewState is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                    RequireWriteAllowed();
            };
        }
        else _moduleContexts[db] = _generation;
    }

    public void RequireModuleCommandAllowed(object? context, DbTransaction? transaction, bool isWrite)
    {
        if (!HasFixedSeedRead && _seedReadState != SeedReadState.Faulted) return;
        var db = context as DbContext;
        if (_seedReadState != SeedReadState.Verifying || isWrite || !ValidateFixedSeedRead()
            || db is null || !_moduleContexts.ContainsKey(db)
            || !ReferenceEquals(transaction, _transaction)
            || !ReferenceEquals(db.Database.GetDbConnection(), _connection)
            || !ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), _transaction))
            RefuseFixedSeedRead();
        // A binding lookup can have constructed this very context on an earlier
        // global transaction. Refresh only the factory-enrolled object's generation
        // after the owning adapter re-enlisted it on the actual tenant transaction.
        _moduleContexts[db] = _generation;
        RequireCleanSeedContexts();
    }

    private void RequireCurrentFixedSeedRead(IFixedSeedRead witness, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_seedReadWitness, witness) || !ValidateFixedSeedRead())
            RefuseFixedSeedRead();
        // This explicit check may use public unit getters. Scope-view validation
        // remains pure and cannot recursively enter the lock-plan validation.
        witness.RequireCurrent();
        if (!ValidateFixedSeedRead()) RefuseFixedSeedRead();
    }

    private bool ValidateFixedSeedRead()
    {
        if (_seedReadState == SeedReadState.Narrow) return true;
        if (_seedReadState == SeedReadState.Faulted) return false;
        if (!_disposed && !_rollbackOnly && _depth == 1 && _transaction is not null
            && _mode == TransactionMode.ReadWrite && _tenantContextIssued
            && _seedReadAnnouncement is { } announcement
            && ReferenceEquals(_tenantContextAnnouncement, announcement) && announcement.Generation == _generation
            && _seedReadWitness is { } witness && witness.TenantId == announcement.TenantId
            && witness.OrganizationId == announcement.OrganizationId
            && witness.IsBoundTo(this, _transaction, _generation)) return true;
        PoisonFixedSeedRead();
        return false;
    }

    private void RequireCleanSeedContexts()
    {
        foreach (var (context, generation) in _moduleContexts.ToArray())
        {
            try
            {
                context.ChangeTracker.DetectChanges();
                if (context.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added
                        or EntityState.Modified or EntityState.Deleted))
                    RefuseFixedSeedRead();
                if (generation == _generation && (!ReferenceEquals(context.Database.GetDbConnection(), _connection)
                    || !ReferenceEquals(context.Database.CurrentTransaction?.GetDbTransaction(), _transaction)))
                    RefuseFixedSeedRead();
            }
            catch (ObjectDisposedException)
            {
                // Disposing this generation's context can hide an undetected
                // POCO edit. Only a context from an earlier owner may retire.
                if (generation == _generation) RefuseFixedSeedRead();
                _moduleContexts.Remove(context);
            }
        }
    }

    private void PoisonFixedSeedRead()
    {
        _seedReadState = SeedReadState.Faulted;
        _rollbackOnly = true;
        _tenantContextIssued = false;
        _tenantContextAnnouncement = null;
    }

    [DoesNotReturn]
    private void RefuseFixedSeedRead()
    {
        PoisonFixedSeedRead();
        throw new InvalidOperationException("The fixed seed read is invalid; the unit is rollback-only.");
    }

    private void ClearFixedSeedRead()
    {
        _seedReadWitness = null;
        _seedReadAnnouncement = null;
        // Fault is sticky until the unit dies, just like its rollback-only mark.
        if (_seedReadState != SeedReadState.Faulted) _seedReadState = SeedReadState.Narrow;
    }

    // This interface is private to the physical owner. The only implementations
    // are these two sealed adapters; no caller can submit a reader or callback,
    // and neither public witness shares an authority-bearing base/interface.
    private interface IFixedSeedRead
    {
        TenantId TenantId { get; }
        OrganizationId? OrganizationId { get; }
        bool IsConfigured { get; }
        bool Resolve();
        void RequireCurrent();
        bool TryBindTo(IUnitOfWork unit, DbTransaction transaction, long generation);
        bool IsBoundTo(IUnitOfWork unit, DbTransaction transaction, long generation);
        Task<bool> ExecuteAsync(CancellationToken cancellationToken);
    }

    private sealed class PrerequisiteSeedRead(SeedPrerequisiteReadWitness? witness,
        ISeedPrerequisiteReadOperationResolver? resolver) : IFixedSeedRead
    {
        private ISeedPrerequisiteReadOperation? _operation;
        public TenantId TenantId => witness!.TenantId;
        public OrganizationId? OrganizationId => witness!.OrganizationId;
        public bool IsConfigured => witness is not null && resolver is not null;
        public bool Resolve()
        {
            _operation = resolver!.Resolve();
            return _operation is not null;
        }
        public void RequireCurrent() => witness!.RequireCurrent();
        public bool TryBindTo(IUnitOfWork unit, DbTransaction transaction, long generation) =>
            witness!.TryBindTo(unit, transaction, generation);
        public bool IsBoundTo(IUnitOfWork unit, DbTransaction transaction, long generation) =>
            witness!.IsBoundTo(unit, transaction, generation);
        public Task<bool> ExecuteAsync(CancellationToken cancellationToken) =>
            _operation!.ExecuteAsync(witness!, cancellationToken);
    }

    private sealed class CompletionSeedRead(SeedCompletionReadWitness? witness,
        ISeedCompletionReadOperationResolver? resolver) : IFixedSeedRead
    {
        private ISeedCompletionReadOperation? _operation;
        public TenantId TenantId => witness!.TenantId;
        public OrganizationId? OrganizationId => witness!.OrganizationId;
        public bool IsConfigured => witness is not null && resolver is not null;
        public bool Resolve()
        {
            _operation = resolver!.Resolve();
            return _operation is not null;
        }
        public void RequireCurrent() => witness!.RequireCurrent();
        public bool TryBindTo(IUnitOfWork unit, DbTransaction transaction, long generation) =>
            witness!.TryBindTo(unit, transaction, generation);
        public bool IsBoundTo(IUnitOfWork unit, DbTransaction transaction, long generation) =>
            witness!.IsBoundTo(unit, transaction, generation);
        public Task<bool> ExecuteAsync(CancellationToken cancellationToken) =>
            _operation!.ExecuteAsync(witness!, cancellationToken);
    }
}
