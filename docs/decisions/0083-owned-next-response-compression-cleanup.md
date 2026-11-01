# ADR-0083: Owned Next Response Compression Cleanup

## Status

Proposed

**Date:** 2026-11-01
**Deciders:** @cemil (approval pending)

This proposal adds a version-specific dependency-maintenance obligation. It does
not relax ADR-0070/0071/0078/0079 ownership, authorization or publication rules.
No installed package, lockfile or runtime policy changes before acceptance.
The [three-step package](../roadmap/phase-03-delivery-log.md#adr-0083-proposal-r1-corrections-and-revised-package-2026-11-01)
owns the proposed carriers, review order and evidence limits.

## Decision Drivers

- The pinned Next 15.5.28 response pipe can wait for `finish` after an actual
  response `close` that emits no `finish`. Default compression can also retain
  its paused readable side after the response closes.
- The original handler and compressor must settle before request ownership is
  released. A transport terminal event cannot replace unrelated actual work.
- Preserve compression, response bytes, backpressure, native publication fences,
  all existing deadlines and complete final-source hosted A/B qualification.
- Make local framework maintenance reproducible and fail closed on package,
  patch, compiled-copy or source-map drift; no shared-store mutation or runtime
  string replacement supplies production behavior.

## Considered Options

1. **A version-pinned pnpm patch with framework-owned cleanup** (chosen).
   Correct terminal observation and compression ownership in the installed
   package, including every embedded pipe copy and direct-response paths.
2. **Disable Next compression** (valid topology alternative, not selected).
   Removes the encoder but changes bandwidth/deployment behavior and does not
   repair the separate finish-only pipe path.
3. **Upgrade to a released upstream correction** (preferred removal trigger,
   not established today). An open pull request is not a verified release and
   its terminal-only change does not prove actual compressor cleanup.
4. **Release the native request on response close or enlarge shutdown waits**
   (rejected). These hide retained work or change a different policy without
   supplying actual cleanup.

## Decision

LearnStack maintains one exact-version pnpm patch for Next 15.5.28. The framework
observes actual response `finish` or `close`, owns premature-close cancellation
and actual closure of its compressor, and joins that cleanup through both its
pipe and outer router request handler. Actual end work and the native request's
other retained work remain independently joined. Package installation and
runtime-source controls reject missing, partial, changed or unqualified patches.
The supported build, native development/production, copied-fixture and packaged
entry points verify exact installed bytes before importing Next or listening.

## Context

### Evidence and limits

The seventh complete P03-3 hosted A fails two cases on its recorded source. Its
browser counter proves the expected history mutant, but cleanup still fails.
The last aggregate observation has one pending handler and no pending terminal,
write, registered framework or snapshot-pool counts. The sample may be stale;
it identifies neither the exact request nor the handler's await site.

A private direct HTTPS probe uses the actual pinned Next pipe, bundled
compression and current native response fence. Closing its own peer after the
wrapped end call reproduces a pending handler and paused, unclosed compressor.
Changing only the pipe's terminal observation settles that handler while the
compressor remains retained. A private two-owner prototype joins real compressor
close and settles the original pipe; healthy compressed bytes remain equal.
Held real end work still prevents settlement until its actual release.

These controls establish a reachable framework defect and a concrete repair
mechanism. They do not attribute the hosted frame, qualify the whole patched
package, prove all actual native/API/pool boundaries, or replace complete hosted
A/B. A separate instrumented stock-browser counter exits with its intended
rejection and actual cleanup; its 64 observed frames all release. It does not
reproduce the failed frame and is not a passing full browser Fact.

The pinned upstream pipe waits only for `finish`. Upstream PR #100529 is open at
review time; its terminal-observation correction is corroboration, not a shipped
cleanup guarantee. Recheck actual release bytes before replacing this patch.

### Exact maintenance surface

The patch is checked into `frontend/patches/next@15.5.28.patch`, referenced by the
exact `pnpm.patchedDependencies` key in the workspace root package manifest and
recorded by the existing pnpm 9.12.3 lockfile. Normal frozen installation applies
it. Non-applied patches remain errors. No environment override, postinstall
mutation, startup loader or permissive fallback selects a correction.

The installed-package census currently finds 27 pipe implementations: CommonJS,
ESM and embedded production/development, regular/experimental/Turbo runtime
copies. It also identifies the common bundled compression owner and two router
copies. Patch all of these; a currently unused copy is not silently exempt.
A checked-in machine-readable census owns exact paths and original/patched
hashes. Correct associated source maps and embedded source content; retain
upstream license/provenance. Deterministic parsed edits and reviewable source
changes, rather than manual minified-string edits, produce the checked-in patch.

This is a bounded maintenance surface, not approval to patch arbitrary Next
internals. A changed package version, new implementation, unsupported map or
unmatched transformation refuses qualification and reopens the maintenance work.

### Actual owner and terminal contract

The compression middleware synchronously installs one immutable, response-local
owner slot and cleanup promise at middleware entry, before any new stream can be
created. The slot records actual header/encoding selection, response finish/close
and actual compressor creation/error/close before allowing output. A handler
return cannot declare that no later compressor exists. Absent selection in a
normal live operation waits for actual selection or transport terminal; the
rejection/abort transitions below are separate. Identity/HEAD/no-body paths
create no stream. Duplicate installation refuses before allocating another
compressor. A premature real response close requests destruction of that actual stream; destruction requested,
`writableFinished`, timeout or a wrapper's completion is not close confirmation.
A real router rejection before encoding selection seals the slot against later
stream creation before joining it. With no created resource, no transport event
is required to retire that compression obligation. Its wrappers/header callback
can no longer create a compressor; the existing native error boundary may use
the captured, still-fenced uncompressed writer. This preserves its existing
pre-header error policy without waiting for a response that only that caller
can produce. A real abort with a still-live response requests actual response
cancellation before joining. Normal early return alone cannot seal a slot while
registered response-producing work can still create its stream.

Construction failures account for every resource actually created. A compressor
fault is observed immediately, requests actual response cancellation, and joins
actual stream close before publishing its failed cleanup result. The owner
observes rejection immediately even if no consumer has reached finally. This
transport failure may close the connection before any error body can be sent;
it does not change ordinary API/BFF refusal classification. The physical close
and failed operation are reported honestly, never as a successful response.

If the handler and cleanup both fail, retain both errors and preserve the
handler's original exception identity. On an otherwise successful handler,
cleanup failure yields a failed handler/cleanup outcome. Headers or body bytes
already delivered cannot be retracted: emit no second response or synthetic
rollback, and do not change an already-committed wire status. Existing native
publication/output fences still apply before every output operation.

The pipe's transport promise observes actual finish or close without emitting
synthetic events or invoking somebody else's callback. Its real end-work await
remains before pipe completion. Its `finally` joins the compressor's original
cleanup promise, including abort/error paths. The outer router's actual request
handler also joins the same promise in `finally`, covering static/direct `end`
paths that do not enter a pipe. Joining one promise twice creates no second
stream owner. The primary-error and irreversible-wire rules above apply to both
joins. A cleanup failure cannot become positive cleanup even when the peer has already
received a successful HTTP result.

The response-local capability is private to the patched framework and contains
no credential, user input or authority selector. Duplicate ownership, late or
inconsistent registration and a missing required owner on a middleware-applied
response fail closed. A valid direct pipe consumer without compression is not
misclassified as missing that middleware. Healthy uncompressed, HEAD and no-body responses with no compressor do not acquire a
fictional stream. No generic native-handler release shortcut is added.

### Reopen and removal

Reopen this choice if a version or runtime family changes, source/census/map
verification fails, healthy wire/backpressure changes, a retained owner escapes
its gate, or a verified upstream release provides equivalent cleanup. Removing
the patch requires the same actual early-close, healthy and independent-owner
controls and complete final-source qualification; deleting the patch to make an
installation proceed is not a repair. The maintainer owns this review through
P03-3 and subsequent dependency upgrades; Phase 11 retains production capacity
qualification, not this correctness proof.

## Consequences

### Positive

- Actual framework and compressor cleanup can finish without discarding the
  native handler or weakening independent work ownership.
- Compression and healthy response behavior remain part of the qualification
  contract; reproducible installation exposes the local maintenance burden.

### Negative

- LearnStack temporarily maintains internal Next code across 27 pipe copies,
  shared compression/router owners and maps. Minified runtime diffs are large;
  upgrades require explicit regenerated evidence and review.
- Cleanup can wait indefinitely for actual retained work. Existing timeouts
  report failure and retain ownership; they do not authorize a successful join.
- A reproduced local framework defect does not guarantee the hosted browser
  failure is resolved. A further failure requires diagnosis, not a weaker gate.

### Neutral

- No database, provider, tenant/native trust source, permission, audit or session
  policy changes. No response buffering, deadline increase, skipped test,
  fabricated finish/callback or forced-cleanup success is introduced.
- Maintainer-deferred manual accessibility evidence stays unexecuted.

## Implementation Notes

The P03-3 delivery package owns three committed steps, each with two fresh
independent review rounds and verified correction commits before progression:

1. Accept this decision and reconcile its named carriers. Record an additive
   navigation amendment in ADR-0070; its ownership decision stays unchanged.
2. Implement the exact patch/census/generator and frozen-install controls. Prove
   every changed copy matches the semantic correction and every actual runtime
   family used by supported builds loads corrected code. Join direct-end paths
   as well as stream pipes; retain every actual resource on failure. Reconcile
   private fixture dependency/source provenance so it executes the same patched
   installed package. Do not broaden its live-source exemptions. Prove real
   initialized-router rejection before headers/selection, abort before selection,
   synchronous construction failure and early stream fault; no error-path join
   may wait for the caller's own future error response. Cover primary-plus-cleanup
   errors and irreversible output before/after headers/finish. Explicit event-order
   controls cover already-terminal attachment, close during blocked write/end,
   actual held callbacks and drain, and exact listener retirement; no synthetic
   event or callback supplies success.
3. Run real healthy and peer-close controls, held independent native/framework/
   API/pool controls, actual development/production browser journeys, frontend
   type/lint/full guarded suite and public browser/Lighthouse proof. Freeze the
   actual final source/population, then retain and independently audit complete
   hosted A followed by full B on the identical source/toolchain/plan. No failed
   historical subset qualifies this extension.

Standard 07 owns the installed-framework maintenance and cleanup rules; Testing
06 owns actual proof, Catalogue 21 owns rule registration/status, and the Phase
03 Status block alone owns packet state. Proposal acceptance promotes no test
rule or runtime gate. Detailed proof and carrier edits live in the delivery log.

## References

- [Owned tenant BFF sessions](0070-owned-tenant-bff-sessions.md),
  [uncertain AccountSite refusals](0071-uncertain-account-site-refusals.md),
  [native scope](0078-native-session-scope-and-self-entry.md) and
  [bounded administration](0079-bounded-identity-administration.md).
- [Frontend architecture](../standards/07-frontend-architecture.md),
  [Testing](../standards/06-testing.md),
  [Architecture tests catalogue](../standards/21-architecture-tests-catalogue.md)
  and [Phase 03](../roadmap/phase-03-identity-admin.md).
- [Pinned Next source](https://github.com/vercel/next.js/blob/v15.5.28/packages/next/src/server/pipe-readable.ts)
  and [open upstream proposal](https://github.com/vercel/next.js/pull/100529).
- [pnpm 9.12.3 patch metadata](https://github.com/pnpm/pnpm/blob/v9.12.3/packages/types/src/package.ts)
  and [patch-commit implementation](https://github.com/pnpm/pnpm/blob/v9.12.3/patching/plugin-commands-patching/src/patchCommit.ts).
