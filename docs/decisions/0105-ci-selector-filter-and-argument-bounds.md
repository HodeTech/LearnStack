# ADR-0105: CI Selector Filter And Argument Bounds

## Status

Accepted

**Date:** 2026-11-21
**Deciders:** @cemil
**Bounded supersession:** the 32 KiB filter-argument and 64 KiB argument-vector
bounds that [ADR-0063](0063-complete-bounded-ci-test-shards.md) records as its
initial bounds. A dated Amendment in ADR-0063 discloses this change; its
historical body, status and date remain intact.

Maintainer acceptance changes only the two argument bounds. Counts, the
assignment algorithm, the B salt, weights, whole-job ceilings and the complete
two-layout qualification stay fixed. Executable change and the hosted
qualification of both layouts are separate gates; the
[acceptance record](../roadmap/phase-03-delivery-log.md#adr-0105-acceptance-and-implementation-2026-11-21)
owns them.

## Decision Drivers

- The first hosted layout-B run of the P03-9 source refuses `backend-ordinary-11`
  with `filter_bound` before any test runs: its selector filter is 33,367 bytes
  against the 32,768-byte ceiling, and `ordinary-7` is at 31,989. The result is
  deterministic from the manifest and the plan, so no rerun can pass.
- Layout A holds the same population in 27,144 to 28,123 bytes: the average
  ordinary shard already fills about 84 % of the ceiling. There are 2,052
  distinct ordinary selectors in twelve shards, the Accepted upper count, so
  growth is absorbed by longer filters, not by another shard.
- A salt cannot repair this. Over 200 candidate B salts on the current
  population the largest filter has a median of 33,070 bytes; 86 of 200 fit and
  none stays under 90 %. Every added selector reshuffles a hash-ordered
  assignment, so a salt chosen for today's population is a fresh draw tomorrow.
- ADR-0063 calls these bounds LearnStack ceilings, not claimed platform maxima,
  whose hosted behaviour must qualify. A filter is one argument; Linux allows
  a single argument of 128 KiB.
- Whatever changes must keep exact case-identity reconciliation: refuse an
  oversize filter before spawning and never split or run unfiltered.

## Considered Options

1. **Raise the filter to 64 KiB and the argument vector to 128 KiB** (chosen).
   Doubles both ceilings. The median layout-B filter falls to about half of the
   new bound, and nothing else in the plan moves.
2. **Make the assignment byte-aware** (valid, not selected). Bin choice would
   also balance filter bytes, which brings layout B down to layout A's roughly
   84 %. It is a new algorithm version with new code and tests, and it leaves
   about 16 % of growth before the same refusal.
3. **Choose another B salt** (rejected). Measured above: at best a one-time
   fit that the next added selector undoes.
4. **Add a thirteenth ordinary shard** (rejected). It exceeds the Accepted count
   range, costs another VM and reopens the count envelope for a byte ceiling.
5. **Split a filter across invocations or run unfiltered** (rejected).
   ADR-0063 forbids both; the exact filter-to-case-ID equality needs one
   filter per owner.

## Decision

LearnStack bounds a generated selector filter argument to 64 KiB of UTF-8
(65,536 bytes) and all argument strings of one invocation, separators
included, to 128 KiB (131,072 bytes). The committed plan carries both in
`bounds` and the plan validator pins exactly these values. An oversize filter
or vector is still refused before spawning, as `filter_bound` or `argv_bound`;
it is never split and the run is never unfiltered. Everything else in
ADR-0063 stays in force.

The bounds remain LearnStack ceilings, not claimed platform maxima. Their
hosted behaviour qualifies with a complete layout A and a complete layout B on
the same final source after the change. The refused layout-B run supplies the
first filters above 32 KiB, and independent rediscovery of each filter before
execution continues to prove that no argument was truncated or altered.

## Consequences

### Positive

- Layout B stops refusing on this population, and both layouts gain roughly
  twice the room for selector growth with no new shard, VM or algorithm.
- The bounds stay finite, validated and refused before spawning.

### Negative

- A filter of up to 64 KiB passes through `dotnet test` and the vendor test
  host on hosted runners. If a tool in that chain refuses or alters it, the
  failure is detected by rediscovery and shows as a failed attempt, not as a
  silent partial run; qualification is what proves the larger size.
- The room is finite. Roughly doubled population reopens this question, and the
  byte-aware assignment remains the next candidate.

### Neutral

- Counts, salt, algorithm, weights and every job ceiling are unchanged.

## Implementation Notes

- The plan, its validator and the shard tests change together; ADR-0063 gains a
  dated Amendment 4 pointing here.
- The Phase 03 delivery log owns the measurements, the implementation record
  and the hosted qualification receipts.

## References

- [ADR-0063](0063-complete-bounded-ci-test-shards.md): the bounds this record
  replaces and the rules it keeps.
- [ADR-0075](0075-ordinary-ci-job-window.md) and
  [ADR-0082](0082-current-source-ci-execution-windows.md): the precedent for
  changing an ADR-0063 ceiling by a bounded decision and a dated Amendment.
- [Standards 06](../standards/06-testing.md): the complete bounded CI
  assignments rule.
