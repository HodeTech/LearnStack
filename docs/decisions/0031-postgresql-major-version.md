# ADR-0031: PostgreSQL — Start on Major Version 18

## Status

Accepted

**Date:** 2026-05-19
**Deciders:** @platform
**Supersedes (partial):** ADR-0002 — Initial Architecture (the "PostgreSQL"
major-version choice only; the rest of ADR-0002 stands)

## Decision Drivers

- **LearnStack is pre-implementation.** No migrations exist yet, no
  production database exists, no data exists. The migration-drag cost
  of choosing the wrong major version is zero today and grows
  exponentially after the first deployed schema.
- **Postgres 18 is the longest-runway LTS available.** EOL 2030-11
  versus 16 LTS at 2028-11. Starting on 18 buys an extra two years of
  upstream patches before any forced major upgrade.
> **Erratum — 2026-08-29.** The driver below names PostgreSQL 18's native UUIDv7
> generator `gen_uuid_v7()`. No such function exists — it is `uuidv7()`, shown by
> `SELECT gen_uuid_v7()` on `postgres:18.4-alpine` returning `ERROR: function
> gen_uuid_v7() does not exist`. The Decision is unchanged. Current authority: [ADR-0031
> Amendment 1](#amendments).

- **`gen_uuid_v7()` is native in 18.** LearnStack's
  [ADR-0023 (Strongly-typed ID source generator)](0023-strongly-typed-id-source-generator.md)
  adopts UUIDv7 as the canonical id format
  (time-ordered, index-friendly). Postgres 18 ships a built-in
  `gen_uuid_v7()` SQL function — DB-side DEFAULT values become trivial,
  the app side keeps the strongly-typed wrapping, no extension is
  required. Postgres 16 / 17 force a choice between an extension
  (`pg_uuidv7`) and app-side generation; Postgres 18 closes that gap
  natively.
- **Async I/O for sequential scans (Postgres 18).** On NVMe-backed
  production hardware the perf improvement is measurable and
  particularly relevant for the partitioned `audit_log` (per
  [ADR-0016](0016-audit-log-subsystem.md)) scans that operators run
  during incident review.
- **OAuth authentication (Postgres 18).** Opens a future option to
  shorten the Keycloak → Postgres auth path for diagnostic /
  break-glass scenarios. Not adopted today (the
  [ADR-0004](0004-authentication-strategy.md) realm-based posture
  stays), but worth recording as a Phase-11 lever.
- **EF Core + Npgsql provider parity.** `Npgsql.EntityFrameworkCore.PostgreSQL`
  10.0.0 (already pinned in `Directory.Packages.props`) supports
  Postgres 18 features. Switching majors does not require a provider
  bump; the same csproj graph works.
- **RLS-specific defaults are unchanged.** Tenant + organization
  isolation defense-in-depth ([ADR-0003 Amendment 1](0003-tenant-isolation-defense-in-depth.md))
  uses the `PERMISSIVE` / `RESTRICTIVE` RLS-policy primitives + the
  `app.tenant_id` / `app.organization_id` session-var pattern. None of
  this changed across Postgres 16 / 17 / 18; the policy shape we will
  write in Phase 02a works identically on 18.

## Considered Options

1. **Start on PostgreSQL 18.x** (chosen). Newest LTS, longest runway,
   native UUIDv7, async I/O perf, pre-implementation = zero migration
   cost.
2. **Stay on 16 LTS until production** (rejected). Defers the major
   upgrade to Phase 11 where every migration already exists; the
   `pg_upgrade` exercise + extension recompile + app-side UUIDv7 swap
   become a multi-day operational task instead of a zero-cost image
   bump.
3. **Skip to 17, defer 18 until proven** (rejected). 17 is already
   superseded by 18 LTS; adopting 17 buys nothing 18 doesn't, and
   forces the same upgrade question in 18 months.
4. **Adopt 18 only for dev, keep 16 for production** (rejected).
   Splits the deployment-mode portability promise (ADR-0020) — every
   feature has to be tested on both major versions, every migration
   has to be written for the lowest-common-denominator. The single-
   binary, single-major-Postgres posture is simpler and safer.

## Decision

LearnStack's primary RDBMS is **PostgreSQL 18.x** across all four
deployment modes. Dev compose pins `postgres:18.4-alpine`. EF Core
provider (`Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.0) targets 18.
Tenant + organization RLS policies are written for the 18 syntax (no
divergence from 16 / 17 here; the choice is forward-looking, not a
compatibility break).

This ADR **supersedes the PostgreSQL major-version choice in ADR-0002
only** — the rest of ADR-0002 (EF Core, modular monolith, …) stays.
Together with [ADR-0029 (SeaweedFS)](0029-object-storage-seaweedfs.md)
and [ADR-0030 (Valkey)](0030-redis-compatible-store-valkey.md), all
three backend rows of ADR-0002 now have explicit successor decisions.

## Context

### Why now

Every major upgrade gets harder as the schema grows. A pre-
implementation codebase has no schema. The cost of choosing 18 today is
one image tag change + a `docker compose down -v`; the cost of doing it
in Phase 11 is `pg_upgrade` across every production cluster + extension
recompilation + a possible app-side UUIDv7 generator swap if the
extension we picked early diverges from the 18 native function.

### What 18 brings that we directly benefit from

> **Erratum — 2026-08-29.** The table row below names PostgreSQL 18's native UUIDv7
> generator `gen_uuid_v7()`. No such function exists — it is `uuidv7()`, shown by
> `SELECT gen_uuid_v7()` on `postgres:18.4-alpine` returning `ERROR: function
> gen_uuid_v7() does not exist`. The Decision is unchanged. Current authority: [ADR-0031
> Amendment 1](#amendments).

| 18 feature | LearnStack benefit |
|------------|--------------------|
| `gen_uuid_v7()` built-in | ADR-0023 uses DB-side `DEFAULT gen_uuid_v7()` for high-volume append-only tables without committing to an extension |
| Async I/O for sequential scans | `audit_log` partition scans (ADR-0016) — operator query latency |
| OAuth authentication | Optional shortcut for Phase 11 break-glass paths (not adopted today) |
| Virtual generated columns | Computed columns for `LocalizedMessage`-like derived data (Phase 02a+) |
| `EXPLAIN (ANALYZE)` improvements | Day-to-day query tuning |

### What does NOT change

- **EF Core provider** — `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.0
  already supports 18.
- **RLS policy syntax** — identical in 16 / 17 / 18.
- **Connection-string + role provisioning** — same.
- **Session-var pattern** for `app.tenant_id` / `app.organization_id`
  in transaction-local config — unchanged.
- **`pg_partman` / `pg_stat_statements` / `pgcrypto` / `citext`** —
  all 18-ready in recent releases.
- **Dapr state store + outbox dispatcher patterns** — RDBMS-agnostic.

### What the upgrade costs in dev

A single line in `infra/compose/dev.yml` (`postgres:16.14-alpine` →
`postgres:18.4-alpine`) plus a one-time `docker compose -f
infra/compose/dev.yml down -v && up -d` to wipe the incompatible
catalog. No code change, no migration change (there are no migrations
yet).

### What the upgrade costs in production (deferred to Phase 11)

The first production deployment lands in Phase 11 anyway. Choosing 18
now means Phase 11 deploys *fresh* on 18; there is no `pg_upgrade`
exercise to schedule. If LearnStack ever runs an earlier production
preview, that preview already runs on 18 — no major upgrade needed.

## Consequences

### Positive

- Longest support runway (EOL 2030-11) — five-year horizon before any
  forced major upgrade.
- Native UUIDv7 — ADR-0023 adopts it (DB-side + app-side paths).
- Async I/O perf — direct benefit to `audit_log` and any future
  read-heavy partitioned table.
- Phase 11 production deployment ships on the modern LTS without a
  separate "upgrade Postgres" mini-project on its critical path.

### Negative

- 18 is younger than 16 LTS — community knowledge base is thinner; a
  rare edge-case query plan regression may take longer to resolve via
  StackOverflow / mailing list. Mitigated by EF Core provider maturity
  + the broader Postgres ecosystem's quick uptake of LTS releases.
- Some Postgres-as-a-service offerings (some smaller cloud regions, a
  handful of niche providers) may lag 18 availability by 1–2 quarters.
  Mitigated by ADR-0020 portability: the SaaS / Dedicated deployment
  modes can pin to a specific managed offering and the Self-Hosted
  modes ship with the container image.

### Neutral

- Backup tooling (`pg_basebackup`, `pg_dump`) shape is unchanged —
  Phase 11 backup runbooks stay the same template.
- `Standards 12 § Database Operations` continues to apply verbatim
  (daily logical backups for dev-grade restore, continuous WAL
  archiving in production).

## Implementation Notes

- **This commit** (Phase 01 packet 6 cleanup): dev compose image bump;
  ADR-0002 Amendment 2 references this decision; doc sweep across
  Standards 12 / Architecture / Standards 20.
> **Erratum — 2026-08-29.** The phase note below names PostgreSQL 18's native UUIDv7
> generator `gen_uuid_v7()`. No such function exists — it is `uuidv7()`, shown by
> `SELECT gen_uuid_v7()` on `postgres:18.4-alpine` returning `ERROR: function
> gen_uuid_v7() does not exist`. The Decision is unchanged. Current authority: [ADR-0031
> Amendment 1](#amendments).

- **Phase 02a** (Platform kernel): first EF migration targets Postgres
  18; ADR-0023 adopts UUIDv7 with DB-side `gen_uuid_v7()` as the
  default-value generator for high-volume append-only tables.
- **Phase 11** (production hardening): production sizing, backup
  cadence, replication topology — all written for 18.

## Amendments

### Amendment 1 — The built-in is `uuidv7()`, not `gen_uuid_v7()` (2026-08-27)

This ADR, and five documents repeating it, named PostgreSQL 18's native UUIDv7
generator **`gen_uuid_v7()`**. No such function exists. Measured against
`postgres:18.4-alpine`:

```sql
SELECT gen_uuid_v7();  -->  ERROR:  function gen_uuid_v7() does not exist
SELECT uuidv7();       -->  01a04366-8141-753d-a6a4-161239372fd0
```

The **Decision is unchanged** — PostgreSQL 18 is pinned, and one of its reasons
is that it generates UUIDv7 natively without an extension. Only the spelling was
wrong, and it was wrong in the one place a spelling matters: a `DEFAULT` clause
in a migration. [Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md)
writes the first such clause, which is why this surfaced now.

**The three ADR bodies keep the wrong name and carry an erratum; the three
non-ADR carriers are simply corrected.** That split is
[ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md)'s: in-place
replacement is licensed only where the text is a canonical artifact for reuse — a
template others are told to copy, a DDL or command meant to be applied — and a
function named in prose is read, not applied. The first draft of this amendment
swept all six, and cited
[ADR-0003 Amendment 3](0003-tenant-isolation-defense-in-depth.md) as precedent
for "wrong content inside an Accepted ADR corrected in place". **That citation
was false**, and git says so: the RLS template ADR-0003 removed sat at line 53 of
the pre-amendment file, inside `## Amendment 1 — Organization scope (2026-05-18)`.
ADR-0003's `## Decision` block has never been edited, and the ADR has no section
named "Decision outcome". An amendment corrected an amendment; no accepted
Decision body was touched.

That `IGuidFactory.cs` cannot hold a Markdown erratum is an argument for
correcting `IGuidFactory.cs`, which is code and which immutability never bound.
It is not a licence to rewrite an ADR alongside it: each carrier is judged on its
own.

The carriers, so the correction is recorded rather than silent:

| Carrier | Occurrences | Instrument |
|---|---|---|
| This ADR — § Decision Drivers, § Context, § Implementation Notes, § References | 5 | erratum |
| [ADR-0023](0023-strongly-typed-id-source-generator.md) — § Decision Drivers, § Decision, § Context, § Implementation Notes | 6 | erratum, disclosed in its own Amendment |
| [ADR-0002](0002-initial-architecture.md) — Amendment 2's PostgreSQL row | 1 | erratum, disclosed in its own Amendment |
| [Backend Coding Standards § Identifiers](../standards/02-backend-coding.md) | 1 | corrected |
| [decisions/README.md](README.md) — this ADR's summary row | 1 | corrected |
| `LearnStack.SharedKernel/Identifiers/IGuidFactory.cs` — XML remarks | 1 | corrected |

`gen_random_uuid()` is a real function and remains correct where it appears; it
produces a **v4** UUID, which is what [ADR-0023](0023-strongly-typed-id-source-generator.md)
adopted UUIDv7 to avoid for index locality. A `uuid` primary key on a
LearnStack table therefore defaults to `uuidv7()` or is minted app-side through
`IGuidFactory.NewUuidV7()` — never `gen_random_uuid()`.

## References

- [ADR-0002 Initial Architecture](0002-initial-architecture.md) — original PostgreSQL major-version row, now partially superseded.
- [ADR-0003 Tenant Isolation Defense in Depth](0003-tenant-isolation-defense-in-depth.md) — RLS pattern unchanged across 16/17/18.
- [ADR-0016 Audit Log Subsystem](0016-audit-log-subsystem.md) — partitioned `audit_log` benefits from async I/O.
- [ADR-0023 Strongly-typed ID source generator](0023-strongly-typed-id-source-generator.md) — adopts UUIDv7; PostgreSQL 18's native `gen_uuid_v7()` powers the DB-side default path. **Erratum 2026-08-29:** the function is `uuidv7()`; see Amendment 1.
- [Standards 05 — Database](../standards/05-database.md)
- [Standards 12 § Database Operations](../standards/12-infrastructure.md)
- PostgreSQL 18 release notes: <https://www.postgresql.org/docs/18/release-18.html>.
