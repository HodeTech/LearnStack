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
timeout fails qualification; container teardown never counts as observed FailFast.
Only fixed sentinels are allowed in the writable scratch mount. Raw runtime stderr
is drained and discarded after recognizing its exact fixed diagnostic.

These cases prove direct gate/sink behavior and host-stop/final-exit priority.
`GateClosedBeforeBoundary` records only the observed host-stop/final-exit marker;
false on a direct fatal case makes no claim about the gate after termination.
They do not prove generation phase dominance, genuine Core resource failure,
PostgreSQL ownership, production dump policy or successful external cleanup.
Unsupported preflight or an unavailable image fails; no case is skipped and no
host-wide crash/collector setting is changed.
