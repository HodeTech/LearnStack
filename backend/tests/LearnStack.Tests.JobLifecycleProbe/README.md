# Linux job termination probe

ADR-0060's test-only console runs in an owned official ASP.NET Core 10.0.10 Linux
container. `preflight.sh` rejects unsupported collectors and verifies inherited
soft/hard core limits before managed entry. The supervisor clears the child
environment, disables modern and legacy runtime dumps, observes actual native
exit and emits only the finite receipt used by `HangfireFatalProcessTests`.
The root filesystem stays read-only. The staged proof output is the sole host
input, mounted read-only at `/opt/probe`; writable scratch is an isolated tmpfs.

Every fatal case first runs a benign twin proving the `finally` and `ProcessExit`
sentinels work. The actual production gate supplies the only fatal sink. A wait
timeout cannot supply an expected native result; container teardown never counts
as observed FailFast. The explicitly dirty direct-callback control below requires
its own supervised timeout/kill/join and is refused by the normal exit predicate.
Only fixed sentinels are allowed in the writable scratch mount. Raw runtime stderr
is drained and discarded after recognizing its exact fixed diagnostic.

These cases prove direct gate/sink behavior and host-stop/final-exit priority.
`GateClosedBeforeBoundary` records only the observed host-stop/final-exit marker;
false on a direct fatal case makes no claim about the gate after termination.
They do not prove generation phase dominance, genuine Core resource failure,
PostgreSQL ownership, production dump policy or successful external cleanup.
Unsupported preflight or an unavailable image fails; no case is skipped and no
host-wide crash/collector setting is changed.

## Actual generation phase controls

`GenerationPhaseDriver` adds a separate finite protocol for synthetic faults in
the actual production `HangfireGeneration` over the unchanged pinned Core/provider
assemblies and real PostgreSQL. It never calls the terminal action directly or
writes the owner's phase facts. Its test-only log provider places faults at actual
Core construction, post-builder logging and `NotifySucceeded`; narrow reflection
decorates the existing required/capture/final-marker builders to exercise skipped
removal and actual outer completion. Every scenario runs a benign twin first.
These are synthetic phase controls, not genuine native resource-failure experiments.

`HangfireGenerationPhaseHarness` owns a separate internal Docker network and
PostgreSQL container. Setup runs the canonical roles script and migration inventory,
then detaches the database's preparation bridge before launching the proof. The
actual network must contain exactly the two owned containers. A length-bounded
scoped connection envelope travels through private attached stdin to the supervisor
and redirected stdin to the child; the existing cleared environment, read-only root,
core/dump preflight and bounded artifact rules remain in force. Original vendor DLL
hashes are checked against the canonical distribution manifest before launch.
This disposable PostgreSQL topology uses an ephemeral role password and explicitly
disables GSS encryption/library discovery; it does not qualify Kerberos or change
production connection settings. Supervisor stderr must still be empty.

The phase receipt requires the exact observed branch as well as native exit:
constructor, before/after capture, post-builder log, actual notification recovery,
post-marker wait setup, required disposal that skips removal, second announcement,
actual outer completion, host-stop priority, final-teardown priority and actual
ProcessExit registration order. Retained stock threads are held by a synthetic
logger barrier; that evidence is not business work or an external cleanup witness.

The production `gen_process_exit` case additionally admits one actual
generation-owned private invocation and holds a callback on its real private
token. Real `RequestHostStop` issues the production `CancelAsync` relay; its
actual observer and invocation cleanup stay pending after finite caller waiting.
The private source remains accessible, and the same retained generation/source
ownership survives until process exit. A post-Core observer must see the closed
final gate, unjoined Core/framework cleanup and the still-held private callback
before the supervisor observes native zero. This fixture invocation and callback
hold are synthetic; they do not activate business work or reproduce a native
resource failure. Its benign twin releases the same callback and joins the real
private cleanup and generation before exiting.

The separate `gen_process_exit_direct` child adds a deliberately unsafe callback
on the actual captured Core shutdown token. That synchronous callback waits for
the same held production private cleanup and prevents the post-Core observer
from running. After its exact entry marker, the supervisor requires a bounded
one-second unsuccessful exit wait, explicitly kills only its owned child process
tree, and joins its actual exit and final output before returning. The receipt
records `TimedOut`, `ExternalKill` and `OwnedProcessJoined`; the normal native-exit
predicate rejects any such cleanup. Native SIGKILL and a normally exited
supervisor are observed before container teardown. Ordinary unexpected child
timeouts also receive owned kill/join but remain failed qualification. No timeout,
kill, source-disposal request or process exit claims actual private cleanup.

Four clock-controlled stalls retain real Core construction or required/supplied
startup work through the observation deadline. Late publication/progress must remain
closed, and only actual joins and cleanup finish the owner. Core's own timers stay
real; the test clock advances only the existing production observation clock.

Five same-child controls copy the staged binaries and use PE metadata to identify
one exact production call instruction: the sole `Environment.FailFast(string)`
sink, constructor selection, capture disposal, second announcement, or actual outer
completion. The copy replaces only that five-byte call with stack pops and no-ops;
all other bytes and copied files are verified, including original vendor hashes.
The source output and parent process are untouched. The same benign twin, fault
placement, isolation checks and normal receipt predicate run against the copy.
Constructor/sink/outer suppression must produce independently observed native zero
with working finally/exit markers. Capture/second-announcement suppression can reach
a later fatal observer; actual post-return Core markers make the normal predicate
reject that fallback. Mutation selectors only choose test observation/return paths;
they never select a production fatal state or write private production phase facts.

These source controls require their actual Linux runs before qualification can be
claimed. Dirty receipt predicate tests do not count as runtime mutations. Ordinary
qualified worker, heartbeat, provider parity, process-wide issuance races and
truthful cleanup proofs remain independently required.
