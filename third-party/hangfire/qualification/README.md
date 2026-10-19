# Maintenance decision qualification

These are reproducible decision experiments for Accepted
[ADR-0058](../../../docs/decisions/0058-bounded-stock-hangfire-maintenance.md).
They activate no LearnStack runtime, use no development database and make no
business-owner, granted-role or full-worker regression claim.

From the repository root on macOS/Linux, with Docker, Python 3 and .NET 10:

```sh
python3 third-party/hangfire/qualification/probe-maintenance.py
python3 third-party/hangfire/qualification/probe-pg-timeouts.py
```

The maintenance command builds the adjacent C# source in a temporary project
against the exact package pins, installs stock schema in an owned PostgreSQL
18.4 container and uses random loopback ports. It measures actual stock cleanup
of synthetic counters, selective public component/filter removal and the
aggregator's ACKing response-blackhole behavior. SQL/install rights belong only
to this disposable probe database, not the application. The output is a bounded
JSON receipt. No shared repository binaries are built or changed.

The timeout command sends simple-query protocol messages without reading the
responses while a separate observer inspects active/aborted backend locks. It
qualifies per-statement timing, released abort locks and idle-aborted session
cleanup. It does not exercise Npgsql reset or an implemented maintenance owner.

Both clean up their own container and sockets on normal completion or handled
interruption. Forced process/machine termination cannot guarantee cleanup. The
maintenance command's retained task is observed after closing the owned proxy;
its process/container also provide a finite experiment teardown boundary. These
experiments do not claim forced cancellation of managed production work.

Measurements and findings belong to the
[Step 2 reassessment](../../../docs/roadmap/phase-02b-events-auth.md#step-2-maintenance-reassessment).
Mandatory production-path controls remain in the ADR and canonical test catalogue.
