using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Identity.Application.Contracts.Bindings;

public enum IdentityBindingOutcome
{
    Existing = 1,
    Created = 2,
    NotFound = 3,
    NonAdmitting = 4,
    Conflict = 5,
    AdmissionDenied = 6,
    AuthorityUnavailable = 7,
    StorageUnavailable = 8,
    Indeterminate = 9,
}

/// <summary>Closed internal outcome; a refused or uncertain attempt authenticates no actor.</summary>
public sealed class IdentityBindingResult
{
    private IdentityBindingResult(IdentityBindingOutcome outcome, UserId? actor)
    {
        Outcome = outcome;
        Actor = actor;
    }

    public IdentityBindingOutcome Outcome { get; }
    public UserId? Actor { get; }
    public bool IsBound => Outcome is IdentityBindingOutcome.Existing or IdentityBindingOutcome.Created;

    public static IdentityBindingResult Bound(UserId actor, bool created)
    {
        if (!actor.IsInitialized() || actor.Value == Guid.Empty || actor.Value.Version != 7 || actor == UserId.SystemActor)
            throw new ArgumentException("A binding requires an admitted actor.", nameof(actor));
        return new(created ? IdentityBindingOutcome.Created : IdentityBindingOutcome.Existing, actor);
    }

    public static IdentityBindingResult Refused(IdentityBindingOutcome outcome)
    {
        if (!Enum.IsDefined(outcome) || outcome is IdentityBindingOutcome.Existing or IdentityBindingOutcome.Created)
            throw new ArgumentException("A refusal requires a closed non-success outcome.", nameof(outcome));
        return new(outcome, null);
    }

    public override string ToString() => nameof(IdentityBindingResult);
}
