using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Identity;

public enum GlobalIdentityOwnerKind { Lookup = 1, Bootstrap = 2, SeedAdoption = 3 }

/// <summary>Closed provenance for one internal owner. Only enumerated production minters may derive its source.</summary>
public sealed class GlobalIdentityOwnerDescriptor
{
    private GlobalIdentityOwnerDescriptor(GlobalIdentitySubject subject, GlobalIdentityOwnerKind kind,
        Type contextType, Type entityType, UserId? seedActor)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(contextType);
        ArgumentNullException.ThrowIfNull(entityType);
        if (!Enum.IsDefined(kind) || contextType.IsAbstract || entityType.IsAbstract
            || (kind == GlobalIdentityOwnerKind.SeedAdoption
                && (seedActor is not { } actor || !actor.IsInitialized() || actor.Value.Version != 7
                    || actor == UserId.SystemActor))
            || (kind != GlobalIdentityOwnerKind.SeedAdoption && seedActor is not null))
            throw new ArgumentException("Global identity owner provenance is invalid.");
        Subject = subject;
        Kind = kind;
        ContextType = contextType;
        EntityType = entityType;
        SeedActor = seedActor;
    }

    public GlobalIdentitySubject Subject { get; }
    public GlobalIdentityOwnerKind Kind { get; }
    public Type ContextType { get; }
    public Type EntityType { get; }
    public UserId? SeedActor { get; }
    public string Source => Kind switch
    {
        GlobalIdentityOwnerKind.Bootstrap => "identity-bootstrap",
        GlobalIdentityOwnerKind.SeedAdoption => "identity-seed-adoption",
        _ => throw new InvalidOperationException("A lookup has no creation audit source."),
    };

    public override string ToString() => nameof(GlobalIdentityOwnerDescriptor);

#pragma warning disable CA1034 // Private construction and a closed minting-source census preserve provenance.
    public abstract class MintingSource
#pragma warning restore CA1034
    {
        protected static GlobalIdentityOwnerDescriptor ForLookup(GlobalIdentitySubject subject,
            Type contextType, Type entityType) => new(subject, GlobalIdentityOwnerKind.Lookup, contextType, entityType, null);

        protected static GlobalIdentityOwnerDescriptor ForBootstrap(GlobalIdentitySubject subject,
            Type contextType, Type entityType) => new(subject, GlobalIdentityOwnerKind.Bootstrap, contextType, entityType, null);

        protected static GlobalIdentityOwnerDescriptor ForSeedAdoption(GlobalIdentitySubject subject,
            UserId actor, Type contextType, Type entityType) =>
            new(subject, GlobalIdentityOwnerKind.SeedAdoption, contextType, entityType, actor);
    }
}
