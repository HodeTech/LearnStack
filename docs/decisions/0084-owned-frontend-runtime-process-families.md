# ADR-0084: Owned Frontend Runtime Process Families

## Status

Accepted

**Date:** 2026-11-01
**Deciders:** @cemil
**Bounded supersession:** ADR-0083's no-new-worker-topology boundary, only for
one launch-scoped native runtime owner. ADR-0083 remains Accepted in place; its
compression, qualification, security and request-lifetime decisions remain in
force. It is neither reduced to a redirect stub nor marked wholly Superseded.
The maintainer conditionally authorizes acceptance and implementation after
review corrections. The [acceptance record](../roadmap/phase-03-delivery-log.md#adr-0084-maintainer-corrections-and-acceptance-2026-11-01)
records their verified disposition and the step sequence. Acceptance establishes
the contract; it does not qualify implementation, runtime controls or hosted A/B.

## Decision Drivers

- A compiler can write after its immediate parent exits; output ownership must
  outlive every actual mutable descendant.
- Development Next can create subprocess workers inside the native runtime.
  Its synchronous exit hook cannot prove their physical retirement.
- Preserve graceful restart without deleting an uncertain ownership record or
  treating a dead PID, closed pipe or delivered signal as cleanup.
- Preserve stop requests during startup, before private handshake and while
  real preparation is pending, without allowing a later listener to open.
- Keep the existing native listener, request/session owners, credentials,
  operation limits, shutdown failure policy and complete hosted A/B gates.

## Considered Options

1. **One builtins owner around each native runtime** (chosen). The owner holds
   the existing output reservations and joins an isolated runtime process group.
2. **Release on direct runtime exit** (rejected). That exit does not establish
   descendant retirement, even when Next requests worker shutdown.
3. **Keep every development lease permanently after exit** (not selected).
   Conservative but breaks supported normal restart; a successful shutdown
   needs an actual retirement path.
4. **Patch/intercept every framework worker creation and shutdown** (not selected).
   Expands private framework maintenance and still needs abnormal-exit proof.
5. **Use the compiler family's group-broadcast preload for runtime** (rejected).
   It can stop workers before the native owner joins their request work.
6. **Add privileged OS containment** (not selected for this boundary). A future
   daemonizing tool or different platform needs its own qualified containment;
   no cgroup/service-manager privilege is introduced here.

## Decision

LearnStack runs each supported native frontend runtime under one builtins-only
Node owner on qualified macOS/Linux surfaces. The owner alone holds the existing
frontend/app/SDK reservations and starts one private runtime child as a new
POSIX process-group leader. It releases reservations only after direct child
close and actual group absence. The child independently verifies its current
parent-bound ownership and installed/generated identity before runtime imports.
Irreversible startup stop latches feed the native in-process shutdown owner;
no runtime group broadcast substitutes for ordered native joins. Listener,
authorization and request ownership remain in the runtime child.

## Context

### Verified failure and evidence limits

ADR-0083 Step 2's independent R1 reproduces an actual managed native producer
with the unchanged SDK build script and an inert private compiler. Cancellation
exits the producer and releases its reservations in about seven milliseconds;
the compiler remains alive, a replacement producer acquires ownership, and the
old compiler writes output roughly 1.2 seconds later. This is a real ownership
counterexample, not an executed full TypeScript/Next cancellation test.

The corrected command-family helper now joins compiler/installer families and
preserves cancellation failure. This decision integrates that correction; it does
not claim that the original R1 producer defect still awaits its first repair.
The [R1 record](../roadmap/phase-03-delivery-log.md#adr-0083-step-2-review-round-1-and-runtime-family-decision-gap-2026-11-01)
owns the historical evidence and separate environment/package-entry findings.
Their correction and local controls do not qualify the runtime boundary here.

Pinned Next 15.5.28's build CLI exits immediately on SIGTERM/SIGINT. Its worker
close requests shutdown without awaiting worker completion. Source inspection
also identifies development `getStaticPathsWorker` subprocesses. The native
launcher currently joins `app.close()`, calls `process.exit`, and releases its
lease in a synchronous exit hook. It registers its signal handlers only after
`app.prepare()` resolves. Neither descendant retirement nor startup cancellation
is proved by that sequence. Full initialized-worker controls remain required.

### Public entry, preparation and lease ownership

- `frontend/apps/web/scripts/public-server.mjs` remains the supported runtime
  CLI: no argument selects production start; `--dev` selects development. It
  becomes a launch-scoped builtins-only owner and starts no replacement child.
- `frontend/apps/web/scripts/framework-build.mjs` remains the separate clean
  production/native producer. Its exported `buildFramework` also supplies
  development preparation to the owner; development is not a public producer
  CLI mode. There is no `public-server.mjs build` mode.
- In development the owner calls `buildFramework({ mode: 'development', ... })`
  before creating a runtime child and retains its returned lease. The producer
  prepares clean SDK/native output and the development manifest under that same
  owner PID. In production the owner acquires the production lease, verifies the
  completed build and prepares the native cache before creating the child.
- The owner alone invokes `prepareFrameworkNativeCache(identity, lease.record)`
  and owns its metadata mutation. The child neither reacquires a conflicting
  frontend/app/SDK lease nor prepares, cleans or claims a cache. Its verification
  reads the exact current parent records, manifest, cache capability metadata
  and graph. Runtime/compiler cache use remains under the parent's reservation;
  verification grants no independent lease or cache-metadata writer authority.
- `public-runtime-child.mjs` is a new private builtins entry. Direct invocation,
  wrong parent/mode/challenge, missing IPC or changed/stale records refuse before
  application, SDK or Next imports and listener creation. The child checks the
  exact current parent PID and live private channel, and independently verifies
  installation and production/development identity. Parent-serialized identity
  alone is not authority.
- The owner rejects nonempty `process.execArgv` and `NODE_OPTIONS` before owned
  admission, preparation or spawn. Supported callers use no extra Node flags.
  Node executes `--import` before a script starts: this gate cannot undo an
  arbitrary caller's earlier preload. Such invocation is unsupported, never a
  claim that the entry prevented already executed code. Supported copied proofs
  migrate their current CLI preloads to the closed mechanism below.
- The owner and its transitive admission/helper imports stay builtins-only.
  Mechanical import-closure controls cover static and dynamic imports, with
  planted nonbuiltin/transitive imports. The private runtime import is the one
  explicit post-validation edge in the child, never an owner import exemption.

### Control metadata and startup stop ownership

These three values have separate purposes and are never reused for one another:

| Value | Minting, storage and permitted use |
|---|---|
| Lease-record nonce | Existing random `lease.record.nonce`, minted by the owner and written in its exact frontend/app/SDK records. Disk-readable binding metadata, not a secret or IPC authentication by itself. The child compares the current records; it cannot release them. |
| Private IPC family challenge | Fresh per runtime spawn. The owner sends it only in the finite child environment and validates it on that exact IPC channel. The dedicated child entry captures it in a module-local constant before environment scrubbing, then removes it before descendants or runtime imports. It never enters records, manifests, argv, proof observations or diagnostics. |
| Proof-incarnation label | Fresh label for the actual runtime child, bound to its authenticated handshake and immutable proof handle. It attributes observations and detects replacement; it grants no cleanup, request, session or credential authority. |

There is no fourth "owner nonce" concept. The expected parent PID, mode and
lease-record binding accompany the private challenge as finite control metadata.
Native credentials retain their existing finite runtime projection; neither
credential values nor their hashes enter these control fields or source graphs.
The dedicated child entry captures its own private metadata directly. It never
runs after the generic producer preload has consumed and deleted another
protocol's environment. No mutable global factory or ambient injection exemption
carries the challenge or supplies the shutdown capability.

The owner installs an irreversible SIGTERM/SIGINT stop latch at entry, before
asynchronous admission or preparation. The latch records the first request even
before any child/handshake exists, and remains active through lease release.
Development preparation receives the same latched `AbortSignal` at each mutation
and owned-command boundary. An already started command still joins its actual
family; cancellation cannot certify preparation or start the next command.
After any awaited preparation, verification or spawn boundary, a latched stop
prevents a new runtime/listener. A stop during startup is a failed launch even
when no work was started or cleanup later completes.

The child installs its own irreversible latch at the start of private validation,
before asynchronous validation/import/preparation. Valid IPC stop, SIGTERM,
SIGINT and parent disconnect all enter that latch. A stop before handshake or
before shutdown registration is retained. After validation the child announces its
authenticated handshake and waits for one authenticated start-or-stop response
before runtime imports. The owner replies stop if its latch is set; otherwise it
grants start once. It retries a pending stop only on that live authenticated
channel. Neither a later handshake nor a queued
start may clear either latch or establish listener readiness. Lost parent/channel
remains a failure even if native shutdown later completes cleanly.

`public-server-runtime.mjs` currently exports no shutdown entry. Implementation
adds `startPublicRuntime(control)`. The child supplies a closed in-process
control with the frozen verified `framework` identity/mode and
`registerShutdown(fn)`, `requestStop(reason)`, `stopRequested()` and
`listenerReady()`. Registration is single-use, consumes any already latched stop
immediately and cannot reset it. Runtime module import performs no effectful
startup. The exported start function
registers a partial-startup-capable shutdown function before its first asynchronous
setup operation and before `app.prepare()`. It tracks each actual started resource
and the preparation promise. Every later allocation/listen boundary checks the
latch; no late continuation opens admission or a listener after stop. This replaces
late signal registration and the runtime identity global with a checked
module-local handoff, not a mutable global factory.

The child controller owns one five-second shutdown watchdog, measured from its
first latched shutdown request. The existing native shutdown failure policy moves
to that controller so a request during runtime import, before registration or
while real `app.prepare()` is held cannot wait indefinitely for a handler before
starting the watchdog. Registration and repeated requests consume the same
remaining interval; they never restart it or add a second interval. An unstarted
child may fail immediately. Expiry reports failure and exits the child nonzero,
without a runtime-group signal or cleanup credit. A blocked JavaScript event loop
has no new hard wall-clock guarantee. The owner continues its actual-family join.

### Runtime shutdown, process observation and results

`framework-process-family.mjs` retains `runFrameworkProcessFamily` for compiler
and installer commands. `runOwnedNode` retains its failed-cancellation result,
including a child that exits zero after a requested stop. Existing SDK cancel
controls continue to assert failure. Its generic
`framework-process-family-preload.mjs` remains a command-family protocol and is
never installed in a runtime child.

A distinct `runFrameworkRuntimeFamily` entry owns the detached runtime spawn,
closed IPC protocol and physical join. It starts the exact private entry with
explicit argv/environment, never inherited `execArgv`, generic broadcast preload
or arbitrary imports. A valid stop invokes the child's in-process registration
above. Admission closes first; native request/session stop is requested before
joining listener, request/session and Next work in the established order. A
pending real prepare remains owned and is joined before any dependent teardown;
partially started resources are accounted for without a fake listener close.
Native faults enter the same controller through the closed `requestStop(reason)`
seam. Its single five-second watchdog retains the first-request anchor described
above and grants no group-retirement credit. Existing ten-second operation/body
limits keep their own cancellation semantics. No group timer cancels actual work.

Neither runtime owner nor child broadcasts a shutdown signal to the runtime
group, including on watchdog, IPC failure or parent death. Framework-requested
worker cancellation keeps its own existing owner and failure semantics. The
owner can outlive the child's five-second failure and must not turn the later
group absence into a successful shutdown.

The [qualified Node spawn](https://nodejs.org/download/release/v22.23.1/docs/api/child_process.html#optionsdetached)
uses `detached: true`: on these POSIX surfaces it creates a new session/process
group led by the runtime child, so the created PGID is the child PID. A fixture
may separately start the CLI owner in another group; owner PGID and runtime PGID
are distinct. Node 22 supplies no `process.getpgrp()`/`getsid()` verification API.
The child's signal-zero probe of `-process.pid` only corroborates current group
existence; spawn plus authenticated entry supplies the provenance, not the probe.

The owner observes direct child `close` and actual group absence. Only `ESRCH`
from signal zero establishes absence; permission/observation errors retain
uncertainty. Runtime observation backs off from 10 ms to at most 250 ms while
work remains; the generic command helper keeps its existing polling. Neither has
a total timeout or supplies cancellation, cleanup or success credit. Once
absence is observed the handle is permanently retired. Nonzero signals are never
sent to a cached runtime PGID, even after a positive probe or leader/channel loss.
Native Windows and other unqualified surfaces refuse before owned mutation or
listener creation.

The runtime runner returns one frozen final receipt only after direct close and
group absence. It preserves `code`, `signal`, `handshakeValidated`,
`listenerReady`, `requestedStop`, `startupCancelled`, `parentLost`, `fault`,
`directChildClosed` and `groupAbsent`. Stop reasons and faults use closed scalar
enums, never exception/request payloads. Uncertain/present groups keep the join
pending; a watchdog or provisional observation is not this final receipt.
The outer owner applies this distinct runtime outcome policy:

| Observed outcome | Outer CLI result after actual retirement |
|---|---|
| Actual listener became ready, requested graceful stop, child code `0`, no signal/fault/parent loss/startup cancellation, valid handshake and group absent | Success; release the owner's unchanged reservations. |
| Stop before actual listener readiness, including held prepare or pre-handshake stop | Failure, even if child code is `0`; release only after actual joined work/family retirement. |
| Child exits `0` before readiness or without a requested graceful stop | Failure; a handshake or synthetic ready frame cannot convert it to success. |
| Child nonzero exit, any signal exit, protocol/observation fault or lost parent | Failure remains failure after group absence. |
| Direct close or group absence is missing/uncertain | No completed result or reservation release. |

`listenerReady()` is called only by the actual native listener's successful
listening callback after checking its bound address and stop state. The private
IPC frame carries that observation; handshake, IPC connection, log text or an
arbitrary ready frame cannot stand in for the binding. Fixtures independently
check the real HTTP/TLS listener. No new startup deadline is selected.

### Owner loss and retained records

Parent death requests the child's latched native shutdown through IPC disconnect.
The dead owner's durable records remain; the child never releases them. Missing
parent PID, old record age, closed stdio and a delivered signal never authorize
record deletion, stale-PID stealing or signaling a remembered group. The owner
is one launch's ownership scope, not a persistent restart/recovery supervisor.
Phase 11 retains production supervision and capacity proof.

Step 1 assigns Standard 07's **Frontend runtime owner-loss recovery** procedure
to @cemil until Phase 11 establishes its operational owner. That procedure keeps
exact records and roots, identifies every launch-owned producer/runtime family,
and requires evidence of their actual retirement plus unchanged record identity
before any explicit recovery. A documented full host stop/restart may prove all
pre-restart work ended; a PID probe alone cannot. The procedure stops competing
launches/mutations, snapshots record paths/bytes without following aliases and
rechecks the exact unchanged records after all-family or host-lifetime evidence.
Only then may the maintainer manually retire those captured record files and run
the normal clean managed producer before restart. Other records, cache ownership
and output roots are not deleted by that manual step. Missing evidence retains
records/roots and escalates to @cemil. This adds no production automatic
reconciler or record-deletion command. Step 2 proves the procedure's retained and
positive evidence paths in isolated fixtures before runtime support is claimed.

POSIX groups cover the qualified inherited Node/Next/compiler/lifecycle topology,
not arbitrary programs that change their session/group or daemonize. Supported
pnpm installation disables its optional detached store server for that invocation.
Qualification inventories actual descendants; a newly detached mutable writer
reopens this decision before support is claimed. This is trusted-source ownership,
not hostile-process containment or a total resource bound.

### Copied callers, instrumentation and proof attribution

`framework-proof-support.mjs` explicitly copies `public-runtime-child.mjs` beside
the bootstrap. The shared helper uses the existing qualified workspace-script
resolution. Both, the runtime shutdown seam and the full builtins import closure
belong to production/development input graphs and restart-critical inputs.
Changed, missing or extra executable boundary inputs refuse or require a real new
owned build. No borrowed stale child, broad loader exemption or proof profile
outside the admitted source graph is permitted.

> **Erratum — 2026-11-01.** The caller enumeration below is not exhaustive:
> existing BFF consumers also include `verify-bff-protocol.mjs` and
> `bff-proof-support.mjs`. Their profile-specific routing remains required.
> The Decision is unchanged. Current authority:
> [Standard 07](../standards/07-frontend-architecture.md#copied-runtime-proof-protocols).
> Recorded in Amendment 1.

The exact caller inventory under `frontend/apps/web/scripts/` is
`stop-test-child.mjs`, `fixture-support.mjs`, `ingress-readiness.mjs`,
`verify-ingress.mjs`, `verify-ingress-controls.mjs`, `verify-ingress-cleanup.mjs`,
`verify-public-admission.mjs`, `verify-public-rendering.mjs`,
`verify-bff-session.mjs`, `verify-bff-browser.mjs`, `admission-browser.mjs`,
`bff-proof-host.mjs`, `bff-proof-lifeline.mjs`, `bff-proof-parent-watch.mjs`,
`admission-hmr.mjs` and `admission-hmr-watch.mjs`. Direct provenance controls and
`scripts/demo.mjs`/`scripts/demo-process.mjs` also reconcile PID, group, source-pin,
stdio and cleanup assumptions. Make's `public-web`, `build-frontend`,
`test-frontend` and `demo DEMO_MODE=ci`, plus CI's `frontend` and `public-demo`
jobs and native backend proof callers, retain their actual entry names and gates.

Before counting readiness a fixture captures a frozen positive family handle
from its exact launch and private child channel. The fixture establishes the
owner PID/group from its own qualified detached spawn; an ordinary foreground
owner need not lead a group, and the owner cannot invent a `getpgrp()` result.
The handle also binds unchanged lease-record nonces, runtime PID/created group
and incarnation. Missing launch/group capture is unknown, never inferred. Any
producer families still active during startup also require captured handles.
`stop-test-child.mjs` requests graceful stop from the live owner itself and joins
its result plus the captured actual families; disappearance of the fixture's
outer group alone is insufficient. Owner and runtime observations remain distinct.
No two-second SIGKILL fallback or cached-PGID rescue is a runtime cleanup path.
A deliberately forced owner exit remains failed and uncertain until independent
family observations prove absence. Missing/stale handles preserve roots/records.

Only the closed fixture reconciler may retire that fixture launch's exact
unchanged records after every captured family actually disappears. Forced or
failed shutdown remains a failed test even after evidenced fixture cleanup. This
is no production recovery API. The BFF lifeline and parent-watch controls migrate
intentionally: their current group-kill behavior must not reach the runtime
family or substitute for its in-process shutdown. Lost host/owner IPC enters the
same latch, and blocked/failed observation retains roots. Fault controls use
isolated metadata/output roots and never alter unrelated workspace records.

> **Erratum — 2026-11-01.** The three-profile enumeration below omits the
> already selected `verify-public-admission.mjs` proof. Its existing commands
> require a separate copied-source `admission` profile; the production owner gets
> no profile selector. The Decision is unchanged. Current authority:
> [Standard 07](../standards/07-frontend-architecture.md#copied-runtime-proof-protocols).
> Recorded in Amendment 1.

`prepareFrameworkProof` adds a closed `instrumentation` selection of `none`,
`ingress` or `bff`. Before input capture and the genuine producer, it copies the
profile's exact modules and applies exact-match source transforms to the copied
owner and child. The copied owner imports a builtins-only
`framework-proof-relay.mjs`; the copied child contains literal profile imports at
the post-validation, pre-runtime seam. These are new implementation artifacts,
not existing exports. A frozen launch handle binds the selected profile, exact
module paths/digests, child channel and incarnation. No caller-supplied module,
path, raw frame or production profile env/argv flag is accepted.

Existing bind/lifeline instrumentation no longer runs through CLI `--import`.
Its admitted child modules initialize after the private latch/validation and
before native/Next imports. The owner never imports the child instrumentation,
forwards inherited `execArgv`, synthesizes Next observations or offers an ambient
factory. Profile transformations, relay, child modules and declarations all enter
the copied input graph before build; any change requires normal revalidation or
a real rebuild. Production owner control contains no proof relay or profile.

> **Erratum — 2026-11-01.** The command enumeration below is incomplete for
> the existing public-admission caller. Its `mode`, `observe`, `settled`, reply
> and shutdown-count schemas remain required; `settled` joins the real native
> completion barrier. The Decision is unchanged. Current authority:
> [Standard 07](../standards/07-frontend-architecture.md#copied-runtime-proof-protocols).
> Recorded in Amendment 1.

The copied relay uses a separately tagged finite protocol over the existing
proof-host and private child channels. Host commands remain
`observe(id, fence)`, `advance(milliseconds)` and
`render-control(arm|release|status)`. Replies retain finite bind/prepare/version
fetch, counter, snapshot and render-state schemas. Actual ingress
`{ surface, sanitized }` upgrade frames retain `native|sink` plus a Boolean;
BFF `proof-listener` retains its bounded port. `proof-shutdown` keeps the existing
six-frame maximum, scalar milestones/watchdog/join transitions and five bounded
ownership counts. Profile/path selection belongs to copied source, challenge to
the private runner, and child incarnation/sender validation to the relay. No
credential, request value, arbitrary payload or raw error becomes a diagnostic.

Each relay hop checks exact child sender/channel, incarnation, schema and bounds;
unknown/duplicate/impossible transitions fail its owning proof. Receipt of any
frame supplies observation, not actual-work acknowledgement. Bind plus actual
HTTP/TLS checks remain decisive. HMR, freshness, publication, isolation and
session proofs compare the same real runtime incarnation before and after their
actions. An unchanged outer PID alone is insufficient. Wrong sender, lost
channel, never-binds and deliberate runtime replacement fail the intended guards.
End-to-end controls deliver every schema to its existing host, including both
sanitized sink observations and BFF listener/shutdown milestones. Forged,
malformed, omitted or lost frames cannot replace physical joins or satisfy guards.

## Consequences

### Positive

- A supported replacement cannot mutate output while an older owned runtime
  worker still uses or produces it.
- Normal graceful restart has a real retirement witness; uncertain shutdown
  remains conservative, including startup and parent-loss races.
- The runtime retains its HTTPS, native request and credential owners.

### Negative

- One additional persistent Node process per native instance adds startup and
  memory cost. Local and hosted qualification must measure it.
- An unresolved descendant can delay CLI completion indefinitely. Sudden owner
  death leaves durable records requiring the named evidence-based procedure.
- PID/readiness/stdio assumptions and copied fixture drivers need coordinated
  changes. Initial containment is qualified for macOS/Linux, not native Windows.

### Neutral

- No permission, DB role, endpoint, provider authority, TLS trust or session
  policy is added. No request pool, snapshot budget or CI VM count increases.
- ADR-0083's exact toolchain/patch/maps and Security 11's seven-day fix SLA stay.
- Complete same-final-source A/B and maintainer-deferred, unexecuted manual
  accessibility evidence keep their existing meaning.

## Implementation Notes

The [plan addition](../roadmap/phase-03-delivery-log.md#adr-0084-proposed-runtime-family-package-2026-11-01)
is subordinate to ADR-0083's unfinished Steps 2–3. Acceptance does not qualify a
runtime or complete P03-3. Every implementation step retains commit, fresh R1,
verified correction, new R2 and correction before progression.

1. After correction reviews, reconcile Standard 07's ownership/topology and
   recovery clauses, Testing 06, Catalogue 21's existing Registered rule,
   decision index and phase Status. Add owner process, runtime child, runtime
   incarnation and process family to the glossary before accepting their use.
   Append dated navigation/disclosure amendments to ADR-0083 and ADR-0070;
   allocate their next amendment numbers only when appending. ADR-0083 stays
   Accepted; no whole-record supersession or redirect stub is authorized.
2. Implement the owner/private-child boundary, startup latches, dedicated native
   shutdown seam and distinct runtime receipt. Integrate the already-corrected
   compiler/installer helper without changing cancellation success policy. Deliver
   exact environment/entry checks, copied instrumentation/callers, input graphs,
   recovery controls and proof/PID changes. Complete fresh R1 and R2 implementation
   reviews and verified corrections. No wholesale private loader exemption.
3. Resume ADR-0083 Step 3: real initialized router/worker/HMR, native held-owner,
   public/provider/browser, final discovery and complete hosted A then B. Keep
   the existing windows, reserve, deadlines and negative witnesses.

## Architecture tests

Extend the existing `Next_Response_Cleanup_Is_Installed_And_Owned` rule in
[Catalogue 21](../standards/21-architecture-tests-catalogue.md#next_response_cleanup_is_installed_and_owned),
including the owner import-closure guard; do not invent a second canonical rule.
It remains Registered until complete qualification. Decisive controls include:

- Real descendant held after direct-child normal, failed and SIGKILL exit;
  replacement refusal until actual group absence, then normal production/dev
  restart. Actual initialized development static-paths workers are required.
- SIGTERM/SIGINT before child handshake, between preparation stages, before
  shutdown registration and during held real `app.prepare()`. Assert irreversible
  stop, no later listener, actual started-work/group joins and failed launch.
  Parent death/disconnect exercises those same startup points. Hold registration
  and prepare past the original first-request watchdog boundary; no reset or
  deferred timer may give another five seconds or successful cleanup. Queued
  start/ready messages cannot erase a pre-handshake stop.
- Actual ready listener plus requested graceful child-zero stop succeeds only
  after group absence. Pre-ready zero, unsolicited zero, signal exit, listener
  fault, forged ready without binding and lost parent remain failures.
- Ordered native shutdown with real held worker/request work; admission closes
  before joins. Child watchdog fails while owner/lease remain until group absence;
  eventual retirement preserves failure. Runtime generic-preload/group-broadcast
  mutants fail. Existing compiler/pnpm/SDK cancellation remains failed.
- Wrong parent/mode/challenge, missing channel, stale/changed records and direct
  child invocation refuse. Parent-owned preparation/cache mutation and read-only
  child verification have positive and conflicting-lease/child-writer controls.
- Owner death with independent family handles and retained roots, unknown/stale
  handles, observation errors, changed-record recovery refusal and all-family
  positive fixture recovery. No signal follows cached-group/leader loss.
- Nonempty owner `execArgv`/`NODE_OPTIONS`, unqualified metadata/compiler options,
  nonbuiltin/transitive owner imports and child/helper/profile input mutations
  refuse at their declared boundaries. Already executed Node preloads are outside
  the entry gate's prevention claim.
- Every copied relay schema, malformed/wrong-sender/lost-frame controls, readiness
  without listening and runtime-replacement negatives reach existing proof guards.
  Preserve unchanged listener/browser/native lifetime and disclosure behavior.

A timer, fake close/drain, killed flag, IPC acknowledgement or diagnostic disposal
proves none of these physical retirement obligations.

## References

- [ADR-0083](0083-owned-next-response-compression-cleanup.md),
  [native BFF ownership](0070-owned-tenant-bff-sessions.md) and
  [native session scope](0078-native-session-scope-and-self-entry.md).
- [Frontend architecture](../standards/07-frontend-architecture.md#version-qualified-next-response-cleanup),
  [Testing](../standards/06-testing.md#version-qualified-next-response-cleanup),
  [Security](../standards/11-security.md#dependency-hygiene),
  [Catalogue](../standards/21-architecture-tests-catalogue.md#next_response_cleanup_is_installed_and_owned),
  [Glossary](../glossary.md) and [Phase 03](../roadmap/phase-03-identity-admin.md).
- [Node 22.23.1 process groups](https://nodejs.org/download/release/v22.23.1/docs/api/child_process.html#optionsdetached)
  and [signal/existence API](https://nodejs.org/download/release/v22.23.1/docs/api/process.html#processkillpid-signal).

## Amendments

### Amendment 1 — Existing public-admission proof protocol (2026-11-01)

The acceptance-time profile and command enumerations omitted an existing named
consumer, `verify-public-admission.mjs`. Its source already sends public-admission
mode/observe/settled commands, correlated observation replies and a shutdown-count
frame. Preserving those existing controls was already selected; their omission
cannot authorize dropping them or routing them through an arbitrary relay.

The exact caller list also omitted existing BFF consumers
`verify-bff-protocol.mjs` and `bff-proof-support.mjs`. The inline errata retain the
original accepted text. Standard 07 owns the current closed protocol and caller
correction, including a separate fixture-only `admission` profile.
It uses the same pre-build copied-source binding, literal child instrumentation,
authenticated channel/incarnation and per-profile validation as the other selected
proofs. No production selector, new product consumer, permission, process topology
or shutdown result policy is added. `settled` still awaits the actual native
completion observer; a response/frame receipt cannot replace physical joins.

This clarification changes none of the Decision, Status, Date, Deciders,
alternatives, rationale or consequences. It records a false-at-entry enumeration
under [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md)'s default
inline-erratum mechanism, not an in-place expansion of
accepted body text. Runtime implementation and full qualification remain open.

### Amendment 2 — Correction carrier disclosure (2026-11-01)

Amendment 1's local disclosure did not name every carrier changed by correction
commit `a6089485`. Its comparison with `087f4c54` shows six changed files; the
local amendment named Standard 07 but omitted four other outside carriers.
Documentation 13 and ADR-0041 require this inventory in the changed Accepted ADR,
not only in a delivery record or Git diff.

The complete correction inventory is:

- This ADR: three dated inline errata before the caller, profile and command
  enumerations, and Amendment 1. Original accepted lines remain unchanged.
- [Standard 07](../standards/07-frontend-architecture.md#copied-runtime-proof-protocols):
  the finite existing-proof profiles, admission shapes/completion barrier,
  ingress output fences and BFF consumer/message-routing obligations.
- [Testing 06](../standards/06-testing.md#version-qualified-next-response-cleanup):
  matching admission protocol and actual-completion negative controls.
- [Catalogue 21](../standards/21-architecture-tests-catalogue.md#next_response_cleanup_is_installed_and_owned):
  those obligations within the same Registered rule, without status promotion.
- [Decision index](README.md): the Active-table continuity repair for this ADR.
- [Phase 03 delivery log](../roadmap/phase-03-delivery-log.md#adr-0084-step-1-review-round-1-and-existing-protocol-correction-2026-11-01):
  the appended R1/correction record and regenerated index.

This appended disclosure and the new dated R2 record in that same delivery log
complete the current correction. No existing accepted paragraph or Amendment 1
text is replaced. The Decision, Status, Date, Deciders, rationale, alternatives,
consequences and current runtime/proof policies remain unchanged. This adds no
new protocol, product authority or runtime implementation claim.

### Amendment 3 — Selected proof orchestration windows (2026-11-03)

Accepted [ADR-0087](0087-native-frontend-proof-windows.md) supersedes only
§ Implementation Notes, step 3's preserved proof-deadline clause for its named
frontend test orchestration rows. Runtime result/cancellation policy, actual
family ownership, first-stop shutdown observer, publication fences, unlisted
bounds and complete qualification remain. This body remains historical.
