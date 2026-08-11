namespace LearnStack.SharedKernel.Identifiers;

public interface IStronglyTypedId<out TKey>
    where TKey : notnull
{
    TKey Value { get; }

    /// <summary>
    /// False for an id that was never given a value — the state a
    /// <c>default(TId)</c> struct is in before <c>From(...)</c> runs.
    /// </summary>
    /// <remarks>
    /// This is the only safe way to ask the question. <c>id.Equals(default(TId))</c>
    /// looks equivalent and is not: Vogen's generated <c>Equals</c> returns
    /// <c>false</c> when either side is uninitialized, so a "is this transient?"
    /// test written that way answers <c>false</c> for a transient id and the guard
    /// it protects never runs. Reading <see cref="Value"/> on an uninitialized id
    /// throws, so the check has to happen before the read, not around it.
    /// Every Vogen <c>[ValueObject]</c> emits a matching <c>IsInitialized()</c>,
    /// so implementers satisfy this member without writing it.
    /// </remarks>
    bool IsInitialized();
}
