# ADR-0084: Owned Frontend Runtime Process Families

## Status

Proposed

**Date:** 2026-11-01
**Deciders:** @cemil
**Bounded supersession:** ADR-0083's no-new-worker-topology boundary, only for
the native launcher ownership described here. Its compression, qualification,
security and request-lifetime decisions remain unchanged. No persistent runtime
supervisor is implemented before maintainer approval.

## Decision Drivers

- A compiler can write after its immediate parent exits; output ownership must
  outlive every actual mutable descendant.
- Development Next can create subprocess workers inside the native runtime.
  Its synchronous exit hook cannot prove their physical retirement.
- Preserve graceful restart without deleting an uncertain ownership record or
  treating a dead PID, closed pipe or delivered signal as cleanup.
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
5. **Add privileged OS containment** (not selected for this boundary). A future
   daemonizing tool or different platform needs its own qualified containment;
   no cgroup/service-manager privilege is introduced here.

## Decision

LearnStack runs each supported native frontend runtime under one builtins-only
Node owner process on qualified macOS/Linux surfaces. The owner holds the
existing frontend/app/SDK reservations and starts one private runtime child in
its own POSIX process group. It releases reservations only after the direct
child closes and the actual group is absent. The runtime child verifies the
current parent-bound ownership and installed/generated identity before importing
application, SDK or Next code. No listener, authorization or request owner moves
into the outer process.

## Context

### Verified failure and evidence limits

ADR-0083 Step 2's independent R1 reproduces an actual managed native producer
with the unchanged SDK build script and an inert private compiler. Cancellation
exits the producer and releases its reservations in about seven milliseconds;
the compiler remains alive, a replacement producer acquires ownership, and the
old compiler writes output roughly 1.2 seconds later. This is a real ownership
counterexample, not an executed full TypeScript/Next cancellation test.

Pinned Next 15.5.28's build CLI exits immediately on SIGTERM/SIGINT. Its worker
close requests shutdown without awaiting worker completion. Source inspection
also identifies development `getStaticPathsWorker` subprocesses. The native
launcher currently joins `app.close()`, calls `process.exit`, and releases its
lease in a synchronous exit hook. That sequence supplies no actual descendant
join. The development case is a source-established gap; its full initialized
worker control remains an implementation obligation.

The [R1 record](../roadmap/phase-03-delivery-log.md#adr-0083-step-2-review-round-1-and-runtime-family-decision-gap-2026-11-01)
owns commands, retained receipts and the separate environment/package-entry
findings. Compiler/installer process joining and those two admission fixes fit
ADR-0083 and proceed independently. They cannot close this runtime boundary.

### Process and entry boundary

- `public-server.mjs` remains the supported CLI. It becomes a persistent
  builtins-only owner rather than importing the server runtime into itself.
  Existing build/dev/start modes and actual cwd checks remain closed.
- A private `public-runtime-child.mjs` entry receives a fresh owner nonce over
  the child-only environment and checks `process.ppid`, the live parent and
  matching current frontend/app/SDK records. Direct, wrong-parent, wrong-mode,
  wrong-nonce or stale-record invocation refuses before runtime import/listen.
- The child independently verifies current installation and its production or
  development graph; a serialized identity from its parent is not authority.
  Existing native credentials use their finite runtime projection. Credential
  values never enter argv, records, manifests, diagnostics or handshake fields.
  The local owner nonce is private control metadata in its existing record,
  distinct from provider/session credentials and any proof-incarnation label.
- Stdin/stdout/stderr retain their existing meaning. Production owner-control
  IPC has a closed finite protocol. Runtime readiness still comes from the
  actual native listener; parent spawn or IPC connection is not ready.
- The CLI PID observed by demo/tests is the owner PID. Callers signal and join
  that process, without inferring runtime completion from inherited pipe close.
  An owner starts exactly one runtime child and never replaces/restarts it.
  Runtime incarnation observations attribute proof; they grant neither cleanup
  credit nor request/session authority.

### Actual retirement and failure

The shared `framework-process-family.mjs` helper owns the newly created group;
it never signals the caller's shell group. It observes direct child close and
checks the group's existence with signal zero. Only `ESRCH` establishes absence;
permission/observation errors retain uncertainty. Polling observes physical
state and creates no timeout or cleanup credit. Native Windows and other
unqualified process surfaces refuse before mutation or listener creation.

SIGTERM/SIGINT to the owner sends an idempotent shutdown request over its live
private child channel. The actual child invokes its existing native shutdown
path, first closing admission and requesting the existing request/session stop,
then joining listener, request/session and Next work under the existing order
and five-second failure watchdog. The parent does not broadcast a signal to
workers needed by those joins. Framework-requested worker cancellation keeps
its existing owner and failure semantics.

Nonzero signals are never addressed to a cached numeric runtime PGID, including
after child/channel loss or during record recovery. A probe is not generation
authority for a later signal. After child loss the parent only observes/retains;
it cannot force a remaining worker into a success acknowledgement. Compiler/
installer cancellation is a separate command-family protocol, not permission
to bypass the runtime's graceful request-lifetime boundary.

A delivered cancellation request, watchdog expiry, zero direct exit code or
closed stdio cannot release reservations while a descendant remains. Requested
graceful exit zero succeeds only after the whole group disappears. Unexpected
exit, nonzero or signal failure stays failure after actual retirement. Receipts
distinguish the child's watchdog/result from later outer CLI completion; the
owner can remain alive beyond five seconds without extending that watchdog.

Unfinished descendants or uncertain group observation can retain the owner and
reservations indefinitely. No new forced-kill timer or normal-request deadline
is selected. Parent death is different: private IPC loss requests child shutdown,
but the dead parent's durable records remain and no automatic stale-PID stealing
is added. Safe recovery requires evidence that the actual owned work ended;
deleting a record because it is old or its parent died is not that evidence.

POSIX groups cover the qualified inherited Node/Next/compiler/lifecycle topology,
not arbitrary programs that change their session/group or daemonize. Supported
pnpm installation disables its optional detached store server for that invocation.
Qualification inventories the actual tool descendants; a newly detached mutable
writer reopens this decision before support is claimed. This is trusted-source
ownership, not hostile-process containment or a total resource bound.

### Copied callers and proof attribution

`framework-proof-support.mjs` explicitly copies the private runtime child beside
the native bootstrap. The shared builtins `framework-process-family.mjs` uses
the same qualified workspace-script resolution as the existing guard. Both are
members of production/development input graphs and restart-critical boundary
inputs; changed, missing or additional executable boundary input refuses or
requires a genuine new owned build. No borrowed stale child or loader exemption.

The caller inventory includes `stop-test-child.mjs`, `fixture-support.mjs`,
`ingress-readiness.mjs`, `verify-ingress.mjs`, `bff-proof-host.mjs`,
`admission-hmr.mjs`, `verify-public-rendering.mjs`, public admission, direct
provenance fixtures, and the Make/demo/CI drivers. Their owner/group, forced
cleanup, stdio and runtime-PID assumptions must be reconciled together.

Before counting readiness, a fixture captures an immutable authenticated handle
binding its owner launch, exact record nonces, runtime incarnation and created
group. Ordinary parent completion supplies the full join. If the owner dies,
absence of its old group is insufficient: the fixture independently observes
every captured family. Missing/stale handle or uncertain retirement fails
cleanup and preserves roots and records. No cached-PGID kill is authorized.
Only a closed fixture reconciler, bound to that launch's exact unchanged
records, may retire its records after all captured families actually disappear.
Forced/failed shutdown remains failure. This is evidenced fixture cleanup,
not automatic production stale-record recovery or deletion on PID age/death.
Fault controls use isolated metadata/output roots and never poison or remove
unrelated workspace records.

Copied ingress/BFF instrumentation uses a separately tagged, finite test-only
relay between the existing proof-host IPC and actual child instrumentation.
Commands are the existing bounded `observe(id, fence)`, `advance(milliseconds)`
and `render-control(arm|release|status)` shapes; replies retain their finite
bind/prepare/version-fetch, counter, snapshot and render-state schemas. Only
instrumented copies add this relay. Production owner control accepts no generic
proof payload, request data or credential. Preloads attach to the actual runtime
child and enter its post-instrumentation source graph before the real producer;
the owner must not synthesize framework observations or run the child's preload.

Relays validate actual child sender, incarnation, shape and bounds. Listener
bind and actual HTTP/TLS checks remain decisive. HMR, freshness, publication,
isolation and session proofs compare the same real runtime incarnation before
and after their actions, retaining behavior/source assertions. An unchanged
outer PID alone is insufficient. Wrong-sender, lost-channel, never-binds and
deliberate-runtime-replacement controls must fail their intended proof guards.

## Consequences

### Positive

- A supported replacement cannot mutate output while an older owned runtime
  worker still uses or produces it.
- Normal graceful restart has a real retirement witness; uncertain shutdown
  remains conservative.
- The existing runtime retains its HTTPS, native request and credential owners.

### Negative

- One additional persistent Node process per native instance adds startup and
  memory cost. Local and hosted qualification must measure it.
- An unresolved descendant can delay CLI completion indefinitely. Sudden owner
  death leaves conservative durable records requiring evidenced recovery.
- PID/readiness/stdio assumptions and copied fixture drivers need review. The
  initial process containment is qualified for macOS/Linux, not native Windows.

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

1. After approval, reconcile Standard 07's process/topology clauses, Testing 06,
   Catalogue 21's existing Registered rule, decision index and phase Status.
   Append dated navigation/disclosure amendments to ADR-0083 and ADR-0070;
   allocate their next available amendment numbers only when appending them.
2. Implement the parent/private-child boundary and group joins, shared compiler/
   installer ownership, exact environment/entry checks, all copied callers and
   proof/PID/source-pin changes. Correct ADR-0083 R1's findings and complete two
   fresh implementation review rounds. No wholesale private loader exemption.
3. Resume ADR-0083 Step 3: real initialized router/worker/HMR, native held-owner,
   public/provider/browser, final discovery and complete hosted A then B. Keep
   the existing windows, reserve, deadlines and negative witnesses.

Decisive controls include a held real descendant after direct-child normal,
failed and SIGKILL exit; replacement refusal until actual group absence; actual
development static-paths worker shutdown; production/dev normal restart; parent
death with independent family evidence and root retention; wrong child handshake;
no stale-group signal after leader loss; ordered shutdown with real held worker/
request work; child watchdog failure, owner still live and final nonzero result;
compiler/pnpm cancellation; actual loader-metadata and external compiler-option
refusals; explicit child/helper input mutations; proof-relay/runtime-replacement
negatives; and unchanged listener/browser/native lifetime and disclosure behavior.
A timer, fake close/drain, killed flag or diagnostic disposal proves none of these.

## References

- [ADR-0083](0083-owned-next-response-compression-cleanup.md),
  [native BFF ownership](0070-owned-tenant-bff-sessions.md) and
  [native session scope](0078-native-session-scope-and-self-entry.md).
- [Frontend architecture](../standards/07-frontend-architecture.md#version-qualified-next-response-cleanup),
  [Testing](../standards/06-testing.md#version-qualified-next-response-cleanup),
  [Security](../standards/11-security.md#dependency-hygiene),
  [Catalogue](../standards/21-architecture-tests-catalogue.md#next_response_cleanup_is_installed_and_owned)
  and [Phase 03](../roadmap/phase-03-identity-admin.md).
- [Node 22.23.1 process groups](https://nodejs.org/download/release/v22.23.1/docs/api/child_process.html#optionsdetached)
  and [signal/existence API](https://nodejs.org/download/release/v22.23.1/docs/api/process.html#processkillpid-signal).
