// Mutating proofs own independent Cecil graphs. Keep their per-class collections
// bounded even on large workstations; the backend CI timeout is unchanged.
[assembly: Xunit.CollectionBehaviorAttribute(Xunit.CollectionBehavior.CollectionPerClass, MaxParallelThreads = 2)]
