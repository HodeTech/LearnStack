namespace LearnStack.SharedKernel.Caching;

/// <summary>
/// Per-entry cache policy, per
/// <see href="../../../../docs/standards/20-infrastructure-stack.md">Standards 20
/// § Cache layer cheat sheet</see>.
/// </summary>
/// <param name="L1Ttl">
/// How long the in-process copy stays valid. <c>null</c> takes the
/// implementation's default.
/// </param>
/// <param name="L2Ttl">
/// How long the shared copy stays valid. <c>null</c> takes the implementation's
/// default, and an implementation with no second layer ignores it — the value is
/// carried so a caller written today does not have to be revisited when the
/// Valkey-backed adapter lands on its
/// <see href="../../../../docs/decisions/0035-demand-gated-infrastructure.md">ADR-0035</see>
/// trigger.
/// </param>
/// <remarks>
/// <b>No <c>Tags</c>.</b> An earlier sketch carried a <c>string[]? Tags</c>
/// third parameter that no document ever specified and nothing ever read.
/// Tag-based invalidation has the same defect as the prefix-based invalidation
/// <see href="../../../../docs/decisions/0014-adopt-dapr.md">ADR-0014 Amendment 2</see>
/// removed: it requires an index from tag to keys that no candidate backend
/// maintains across instances, so the method would evict what one process
/// happens to know about and silently miss the rest. A key family that must
/// invalidate a set it cannot enumerate uses the generation-key pattern instead.
/// </remarks>
public sealed record CacheOptions(TimeSpan? L1Ttl = null, TimeSpan? L2Ttl = null);
