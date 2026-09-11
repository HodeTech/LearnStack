using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// Names the aggregate instance the current request's audit row is about, for a handler
/// that writes more than one instance of the type its operation declares.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a handler has to say it.</b> A catalogue entry declares an aggregate
/// <em>type</em> (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044
/// Amendment 5 § 2</see>) and the composer finds the instance among what the request
/// captured. That is enough while a request writes one instance. Publishing a
/// customization revision writes two — it retires the incumbent and activates the
/// successor on one transaction — and nothing in the captures says which of the two the
/// operation is about. The composer refuses rather than guess, because a row naming one
/// instance's id beside the other's state is permanent; before this port existed the
/// refusal rolled back every replacement publication
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment 6
/// § 1</see>). The handler knows which instance it is publishing, so it says.
/// </para>
/// <para>
/// <b>What a designation binds.</b> The intents the <em>current</em> request declared —
/// the innermost open audit frame's — whose declared type the aggregate is. A nested
/// request's designation never reaches its parent's intents, and a designation for a type
/// the request is not audited as (or is classified <c>Off</c> for) binds nothing.
/// Designating a second, different instance of the same type in one request is a
/// programming error and throws.
/// </para>
/// <para>
/// <b>What it does not do.</b> It changes which instance <c>entity_id</c>,
/// <c>before_state</c> and <c>after_state</c> describe. Every other instance of the type
/// the request wrote — the retired incumbent — still travels in <c>changes</c>, under its
/// own instance-qualified pointer, so the retirement stays on the record.
/// </para>
/// </remarks>
public interface IAuditSubject
{
    /// <summary>Designates <paramref name="aggregate"/> as the subject of this request's row.</summary>
    /// <remarks>
    /// Call it once the instance is known and before the request can end — ideally as soon
    /// as it is loaded, so that a refusal after that point names the instance it refused.
    /// </remarks>
    void Designate<TId>(IAggregateRoot<TId> aggregate)
        where TId : struct, IStronglyTypedId<Guid>;
}
