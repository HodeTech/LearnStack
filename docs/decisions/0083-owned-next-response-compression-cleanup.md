# ADR-0083: Owned Next Response Compression Cleanup

## Status

Accepted

**Date:** 2026-11-01
**Deciders:** @cemil

The maintainer conditionally authorizes acceptance and implementation after
review corrections. The [acceptance record](../roadmap/phase-03-delivery-log.md#adr-0083-maintainer-corrections-acceptance-and-step-1-2026-11-01)
records their disposition and the unchanged
[three-step delivery sequence](../roadmap/phase-03-delivery-log.md#adr-0083-proposed-owned-next-cleanup-package-2026-11-01),
including subsequent refinements. Acceptance establishes the contract; it does
not establish a checked-in patch, installed gate or qualified runtime.

## Decision Drivers

- Pinned Next can wait for `finish` after actual `close` without `finish` and
  retain a paused compressor. Native request ownership must join both.
- Preserve existing compression selection and healthy wire behavior, including
  encoders actually created for early-flushed bodyless responses.
- Verified installed code alone does not qualify stale generated Next output.
- Preserve independent work, native publication fences, all operation deadlines
  and complete same-final-source hosted A/B qualification.
- Make framework maintenance, exact toolchain enforcement and security updates
  explicit without suspending Security 11's seven-day Critical/high fix SLA.

## Considered Options

1. **Exact-version pnpm patch with framework-owned cleanup** (chosen).
   Correct all compiled copies, join actual resource closure and bind generated
   output to a clean verified producer.
2. **Disable compression** (not selected). Changes wire/bandwidth policy and
   leaves the finish-only pipe problem.
3. **Disable bundled compression and add a custom server wrapper** (not selected).
   Splits ownership and still requires the pipe correction, another compressor
   adapter and its lifecycle/provenance proof.
4. **Adopt a released upstream equivalent** (removal trigger, not established).
   An open pull request and terminal-only correction prove no complete repair.
5. **Release native ownership on close or extend shutdown waits** (rejected).
   Neither supplies actual cleanup of independent work.

## Decision

- LearnStack admits an explicitly qualified exact Node/pnpm/Next tuple, initially
  Node 22.23.1 / pnpm 9.12.3 / patched Next 15.5.28. Actual executables and
  installed correction bytes are checked at their supported boundaries.
- A version-bound pnpm patch corrects every compiled pipe copy and the common
  compression/router owners. Actual terminal observation resolves on finish or
  close; actual compressor closure, operation faults and independent work remain
  separately owned. Existing compression selection and wire behavior remain.
- Supported generation starts clean and binds runtime output to the verified
  package, sources and producer. Native launch rejects stale or changed generated
  code before importing application/framework runtime or opening a listener.
- Replacement tuples require equivalent reviewed controls and complete
  qualification. The exact pin neither suspends the security fix SLA nor grants
  an emergency exemption from the existing qualification gates.

## Context

### Evidence and limits

The [seventh hosted A](../roadmap/phase-03-delivery-log.md#p03-3-seventh-hosted-a-and-business-task-observation-correction-2026-11-01)
fails a browser cleanup case and a separate NoPlan observation case. The latter
is not evidence for this decision. The browser's last aggregate sample has one
pending handler and zero terminal/write/registered-framework/pool counts. It may
be stale and identifies no exact request or await site.

Nine private HTTPS controls reproduce stock pipe/compressor retention and a
repair mechanism, including the need for a router join on direct-end paths.
Seventy-four subsequent HTTP/HTTPS controls confirm actual bodyless allocation:
early-flushed, unknown-length 204/304 responses can allocate gzip/deflate and
select its header while transmitting zero body bytes. Held real end work and
peer close reproduce retained stock encoders/pipes. Status, empty producers or
zero wire bytes cannot certify absence of a resource.

The [acceptance record](../roadmap/phase-03-delivery-log.md#adr-0083-maintainer-corrections-acceptance-and-step-1-2026-11-01)
owns retained local evidence paths/digests. These are mechanism controls, not a
shipped patch, full initialized router, all-copy execution, actual native/API/pool
qualification or attribution of the hosted frame. The separate stock-browser
counter's intended rejection and 64 released frames are not a full browser pass.

The [pinned upstream pipe](https://github.com/vercel/next.js/blob/v15.5.28/packages/next/src/server/pipe-readable.ts)
waits for finish. [Upstream pull request #100529](https://github.com/vercel/next.js/pull/100529)
is open at review time and resolves its terminal observation on close. It proves
no compressor disposal. Verify the actual released/installed bytes and equivalent
controls before replacing this maintenance patch.

### Implementation artifacts and verification unit

The private inventory finds 27 compiled pipe copies: two standalone CJS/ESM
modules and 25 copies embedded in runtime bundles. With common compression and
two routers, the initial transformation covers **30 JS files and 29 existing
maps**. The compression bundle has no map; the census records this exception.
Inventory is not execution of every compiled branch.

Step 2 creates the following tracked artifacts; none exists by acceptance alone:

| Artifact | Purpose |
|---|---|
| `frontend/patches/next@15.5.28.patch` | Exact initial package patch |
| `frontend/framework/next-response-cleanup/census.json` | Tuple, generator identity, paths and original/patched SHA-256 |
| `frontend/framework/next-response-cleanup/owner.mjs` | Readable transformation input for actual ownership |
| `frontend/scripts/generate-next-response-cleanup.mjs` | Parsed installed-JS transformation and map composition |
| `frontend/scripts/framework-installation.mjs` | Builtins-only toolchain/installed-source admission |
| `frontend/scripts/install-frontend.mjs` | Supported verified executable restore |
| `frontend/apps/web/scripts/framework-build.mjs` | Clean owned generation and build manifest |

The generator edits qualified installed JavaScript by parsed location and
composes its edit map with each original map. It updates mappings and embedded
source content, preserves unchanged segments and licenses, and rejects unknown
or unmatched copies. Editing only `sourcesContent` or stale map comments fails.
Original and patched digests cover every changed JS/map and common owner.

`frontend/package.json` gains the exact `pnpm.patchedDependencies` reference and
pnpm-generated lock identity in Step 2; current overrides remain. Non-applied
patches remain errors. Verification reads actual Node, Next metadata, patch/lock
relationship and the qualified 30-JS/29-map set, not every framework/cache file.
The complete distribution census is an install/qualification control. Supported
startup also verifies the generated runtime graph described in Standard 07;
that set has its own enumeration and measurements.

### Toolchain, generated output and maintenance

Step 2 changes Node/pnpm engine declarations to exact values and enables strict
engine/manager settings. The actual selected pnpm executable is independently
checked before install/build mutation; declarations, Corepack, PATH selection
and user-agent text alone are insufficient. Native runtime startup checks Node
and verified install/build provenance, and need not run a package manager.
README, Standard 03, `.nvmrc` and CI declarations align with the qualified tuple.

[Standard 07](../standards/07-frontend-architecture.md#version-qualified-next-response-cleanup)
owns clean generation, generated-copy inventory, production input/output
manifest, development cache ownership and entry-point rules. Production rejects
old `.next` beside a corrected install; development starts from owned clean
output and verifies its compiler path before incremental output is admitted.
Current build, native and copied fixtures are covered. Source-less packaging or
standalone deployment is not delivered; any later introduction must qualify its
immutable artifact and entry point before support is claimed.

These controls address drift within a trusted source/install/build environment.
They are not signatures against an actor able to alter code, verifier, manifest
and output together. Supported install/build/run owners serialize their mutable
outputs; arbitrary external co-writers are outside the integrity claim.

Security maintenance keeps [Security 11](../standards/11-security.md#dependency-hygiene)'s
seven-day Critical/high fix deadline. Updates receive priority regeneration,
actual-owner controls and complete qualification before admission. A pin never
licenses a known-vulnerable runtime beyond the deadline; likely inability to
meet it reopens the choice immediately. This decision neither extends that SLA
nor permits an unqualified release. Any emergency departure requires a separate
bounded accepted decision; initial P03-3 A/B is unchanged. Routine equivalent
tuple updates use the reviewed census/declarations and qualification path rather
than rewriting this historical initial tuple.

### Terminal, operation and supervision boundaries

[Standard 07](../standards/07-frontend-architecture.md#version-qualified-next-response-cleanup)
owns the complete selection/error/close contract. No-resource requires actual
no-compression selection or a sealed allocation path with no object created.
Every actual encoder, including a bodyless encoder never ended, is joined at
actual close. Either actual response finish or close accounts for remaining
encoders; an earlier finish never exempts them. Native refusal can finish through
a captured writer after allocation, even removing the final encoding header;
that does not erase the actual resource. The terminal observation resolves for
actual finish **or** close;
close without finish records incomplete delivery, not a new synthetic fault.
Existing expected-abort behavior remains. Source/end-work and cleanup faults
retain their failures and primary error identity after real resource retirement.

Each pipe/router error boundary transitions its owner before its own join,
including empty-slot sealing and cancellation of an existing stream. Neither
branch releases genuinely pending end work, native callbacks, registered
framework/API work or snapshot reservations. The existing
[native output fences](../standards/07-frontend-architecture.md#native-entry-and-administration)
remain; already-sent HTTP bytes/status cannot be retracted or replaced.

Unfinished actual work can retain its current reservation for the process
lifetime. The launcher's existing five-second **shutdown** watchdog reports
failure and exits nonzero if shutdown joins do not finish; it is not a normal
request deadline or cleanup witness. Existing ten-second operation/body limits
request cancellation without granting retirement. No new retention timer,
request timeout or worker topology is introduced. Phase 11 owns production
supervision/capacity proof. Returning capacity while its actual required work
is still pending is an ownership escape; retained unfinished work is not release.

## Consequences

### Positive

- Framework and compressor work can retire without discarding native ownership.
- Generated runtime and installed correction share a verified producer identity.
- Existing healthy encoding/wire and independent-work behavior remain decisive.

### Negative

- Internal minified bundles and accurate maps remain local maintenance until a
  verified upstream equivalent qualifies. Tuple upgrades need regenerated proof.
- Hash verification has startup/build cost; installed and generated scopes are
  measured separately. No per-request whole-tree/cache scan is introduced.
- A genuine compressor fault can close transport before an error body. Successful
  wire delivery cannot erase a later cleanup failure or prove actual retirement.
- A never-settling operation retains capacity; this is not a total memory or
  availability guarantee. No longer deadline can substitute for real cleanup.

### Neutral

- No database, credential, tenant authorization, audit, provider or session scope
  changes. Existing CI windows, reserve, test deadlines and complete A/B remain.
- This decision neither executes nor changes P03-3's existing maintainer-deferred
  manual accessibility evidence.

## Implementation Notes

The [three-step package](../roadmap/phase-03-delivery-log.md#adr-0083-proposed-owned-next-cleanup-package-2026-11-01)
and [maintainer revision](../roadmap/phase-03-delivery-log.md#adr-0083-maintainer-corrections-acceptance-and-step-1-2026-11-01)
own carriers and exact gates. Each step is committed, reviewed in fresh R1,
corrected, reviewed by new R2 reviewers and corrected before progression.

1. Accept the corrected decision, reconcile Standards 03/06/07/11 and Catalogue
   21, index and phase Status; allocate ADR-0070's next navigation amendment only
   when actually appending it. Executable enforcement remains Step 2.
2. Deliver the patch, maps/census, actual manager/runtime checks, owned clean
   builds, generated-code manifest and all supported/copy entry points. Prove
   stale generated code beside fixed packages refuses before import/listen.
3. Prove actual owner/router/native/public/browser behavior, including early
   bodyless headers, error/callback/drain orders, held independent work and full
   frontend checks. Freeze actual final source/population and retain/audit full
   A before full B: 27 successful jobs, 20 natural owners, 25 original ZIPs,
   exact zero-skip unions and unchanged 203-second reserve in both layouts.

### Amendment 3 — Frontend completion-window successor (2026-11-03)

Accepted [ADR-0088](0088-frontend-ci-completion-windows.md) supersedes
only its retained test-deadline clause for the selected frontend settings.
The selected settings are web 900 seconds, the closed 31-control Node signal
threshold 20 seconds, its combined Vitest failure threshold 90 seconds and
frontend CI 25 minutes. All unlisted settings, runtime ownership, populations,
physical cleanup and complete qualification remain. Synchronous timeout signals
and Vitest settings are not guaranteed return-time limits; canonical
[Testing 06](../standards/06-testing.md#frontend-guarded-execution) records their
semantics. This body remains historical; acceptance proves no hosted fit.

## References

- [BFF ownership](0070-owned-tenant-bff-sessions.md),
  [uncertain AccountSite](0071-uncertain-account-site-refusals.md),
  [native scope](0078-native-session-scope-and-self-entry.md) and
  [administration](0079-bounded-identity-administration.md).
- [Frontend coding](../standards/03-frontend-coding.md),
  [Testing](../standards/06-testing.md),
  [Frontend architecture](../standards/07-frontend-architecture.md),
  [Security](../standards/11-security.md),
  [Catalogue](../standards/21-architecture-tests-catalogue.md) and
  [Phase 03](../roadmap/phase-03-identity-admin.md).
- [pnpm 9.12.3 patch metadata](https://github.com/pnpm/pnpm/blob/v9.12.3/packages/types/src/package.ts)
  and [patch command](https://github.com/pnpm/pnpm/blob/v9.12.3/patching/plugin-commands-patching/src/patchCommit.ts).

## Amendments

### Amendment 1 — Bounded native runtime family ownership (2026-11-01)

Accepted [ADR-0084](0084-owned-frontend-runtime-process-families.md) supersedes only
this record's no-new-worker-topology boundary for one launch-scoped native owner
and its private runtime child. This record remains Accepted in place; its
compression, exact qualification, security and request-lifetime decisions and
historical body are unchanged. It is not a whole-record supersession or redirect.

The parent retains frontend/app/SDK reservations through actual runtime-family
retirement. The child retains native admission/request/session shutdown ordering,
startup stop latching and the existing five-second failure policy, now anchored
to the first latched request including startup.
Compiler/installer cancellation remains failed; the distinct runtime result
requires actual listener readiness, requested graceful stop and physical group
absence. Phase 11 still owns production supervision and capacity proof.

The acceptance package reconciles Standards 07/06, Catalogue 21's existing
Registered rule, glossary, decision index and Phase 03 Status/delivery record,
and appends corresponding ADR-0070 navigation. Acceptance supplies no runtime
implementation, executed control or complete same-final-source hosted A/B.

### Amendment 2 — Selected proof orchestration windows (2026-11-03)

Accepted [ADR-0087](0087-native-frontend-proof-windows.md) supersedes only
§ Consequences / Neutral's preserved-test-deadlines clause for its selected
frontend proof orchestration windows. Response/compression ownership, operation/
body deadlines, physical cleanup requirements, exact tuple and security-release
policy remain. This body and its original rationale remain historical.

### Amendment 4 — Architecture test coverage navigation (2026-11-04)

#### Architecture Tests

The historical Implementation Notes already require installed-byte, generated-
code provenance, pipe/compression/router ownership and actual native/browser
controls. The missing dedicated test-heading navigation does not mean those
controls are absent. Their canonical owner is Catalogue 21's
[`Next_Response_Cleanup_Is_Installed_And_Owned`](../standards/21-architecture-tests-catalogue.md#next_response_cleanup_is_installed_and_owned).
The [frontend test windows](../standards/06-testing.md#frontend-guarded-execution)
record current execution settings; this amendment changes no window, rule status,
cleanup requirement or qualification credit. The Accepted Decision and its
original rationale remain historical.
