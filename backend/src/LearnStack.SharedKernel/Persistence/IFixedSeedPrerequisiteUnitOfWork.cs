using System.Data.Common;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.SharedKernel.Persistence;

/// <summary>One closed metadata operation on the already fenced ordinary owner; no scope lease or callback.</summary>
public interface IFixedSeedPrerequisiteUnitOfWork
{
    Task<bool> VerifySeedPrerequisitesAsync(SeedPrerequisiteReadWitness witness,
        CancellationToken cancellationToken = default);

    Task<bool> VerifySeedCompletionAsync(SeedCompletionReadWitness witness,
        CancellationToken cancellationToken = default);
}

/// <summary>The composition's fixed Identity metadata reader, never submitted work.</summary>
public interface ISeedPrerequisiteReadOperation
{
    Task<bool> ExecuteAsync(SeedPrerequisiteReadWitness witness, CancellationToken cancellationToken = default);
}

/// <summary>Resolves the one registered operation on the same scope after its physical owner exists.</summary>
public interface ISeedPrerequisiteReadOperationResolver
{
    ISeedPrerequisiteReadOperation Resolve();
}

/// <summary>The composition's fixed completion operation; it receives no submitted reader or work.</summary>
public interface ISeedCompletionReadOperation
{
    Task<bool> ExecuteAsync(SeedCompletionReadWitness witness, CancellationToken cancellationToken = default);
}

/// <summary>Resolves only the registered completion operation after its scoped physical owner exists.</summary>
public interface ISeedCompletionReadOperationResolver
{
    ISeedCompletionReadOperation Resolve();
}

/// <summary>Common owned writers check this before issuing writes on a borrowed physical owner.</summary>
public interface IUnitOfWorkWriteGuard
{
    void RequireWriteAllowed();
}

/// <summary>The common context factory alone registers contexts; commands can refresh only those same objects.</summary>
public interface IUnitOfWorkContextEnrollment
{
    void RegisterModuleContext(object context);
    void RequireModuleCommandAllowed(object? context, DbTransaction? transaction, bool isWrite);
}
