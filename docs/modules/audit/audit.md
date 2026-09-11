# Audit — Audit Coverage Matrix

Per [Audit Coverage](../../standards/18-audit-coverage.md), which names this file. Part
of the [module spec](README.md).

**The module that owns the log is audited like any other.** Reading someone's audit
trail is itself a sensitive read; exporting it produces a copy that outlives the
request; and redacting or purging a row is the one act in the system that removes
evidence. All three are recorded, and the last two are recorded in the table they are
about — which is the arrangement that makes them answerable rather than merely logged.

**No row below is written in this packet.** Packet 9 ships the table, the store, the
capture, the classifier and the pipeline that fills them; every operation this module
declares belongs to a surface that lands later — the query API and the export job in
[Phase 03](../../roadmap/phase-03-identity-admin.md), the override editor in
[Phase 06](../../roadmap/phase-06-renderer-admin-studio.md), redaction with Phase 03's
erasure handler, and retention in
[Phase 11](../../roadmap/phase-11-production-hardening.md). Every row therefore carries
`(planned)`, and the marker is a claim `Every_Matrix_Row_Whose_Command_Exists_Is_Registered`
re-checks on every run: a `(planned)` row whose command has since shipped fails.

This matrix is not the floor —
[Audit Coverage § Baseline Coverage](../../standards/18-audit-coverage.md) is, and a
module matrix "cannot remove anything in this list". This file adds rows beneath that
baseline and classifies what the baseline leaves open; a tenant `AuditConfig` may then
narrow SHOULD/MAY at runtime. Neither touches a baseline MUST.

The `Operation` column carries the catalogue key — `{module}.{resource}.{verb}`, the
shape of a permission key with a verb this matrix names rather than the closed action
set ([ADR-0044 § 6](../../decisions/0044-audit-write-path.md)). Its first two segments
match the permission key for the same resource, so `event` here is `audit.event.*` and
matches `audit.event.read` in [permissions.md](permissions.md).

Two resource segments have no permission key to agree with, and keep their own names for
the reason `tenancy.tenant_assertion.*` keeps its prefix: the key that gates them is
Platform-scope, does not share the `audit.` module segment, and has no name yet —
[permissions.md](permissions.md) records who names it.
`audit.redaction.apply` is the spelling
[Audit Subsystem § 10](../../architecture/31-audit-subsystem.md) already writes into the
draft it shows, and `audit.purge.apply` is its twin — one resource per act, one verb
each, rather than two more verbs on `event`, which is the resource a tenant reads.

| Resource | Operation | Class | Notes |
|---|---|---|---|
| `event` | `audit.event.read` `(planned)` | **SHOULD** (`read-sensitive`) | Someone read a tenant's audit trail. SHOULD rather than MUST because a tenant admin reading their own log is ordinary administration, and a MUST here would make every page of the audit screen write a row into the table it is paging — which is the one place a self-referential write compounds. The tenant may narrow it; the platform-scope read below cannot be narrowed |
| `event_export` | `audit.event_export.write` `(planned)` | **MUST** | An export produces a file containing the log, outside every policy in this schema and outliving the request that asked for it. The row records who asked and for what range; the file's own retention is [Phase 03](../../roadmap/phase-03-identity-admin.md)'s to settle |
| `event_export` | `audit.event_export.read` `(planned)` | **MUST** | Collecting the file is a separate act from asking for it, often by a different principal and always at a different time. Two grants in [permissions.md](permissions.md), two rows here |
| `config` | `audit.config.write` `(planned)` | **MUST** | A tenant narrowed its own SHOULD/MAY coverage. Recording it is what keeps the narrowing itself visible — an override that could be set without a trace would be the first move of anyone reducing what the log will show. The floor holds regardless: `ClassifyAsync` re-applies the catalogue's MUST after the override ([ADR-0033](../../decisions/0033-audit-durability-model.md)) |
| `config` | `audit.config.delete` `(planned)` | **MUST** | The override removed, restoring baseline coverage. Recorded for the same reason and with the same weight as setting it: a reader of the log must be able to reconstruct what coverage was in force at any moment |
| `redaction` | `audit.redaction.apply` `(planned)` `(off-path)` | **MUST** (`security-event`) | The GDPR erasure path of [Audit Subsystem § 10](../../architecture/31-audit-subsystem.md), restricted by the column grant to `actor_email`, `ip_address`, `user_agent`, `before_state`, `after_state`, `changes`. `actor_user_id` is deliberately never redacted — after erasure it is an orphan surrogate with no path to a natural person, which is what keeps the row's existence auditable. `(off-path)`: it runs as `learnstack_platform` inside `EnterPlatformAdminScope(reason)`, so there is no request type to key a catalogue entry on |
| `purge` | `audit.purge.apply` `(planned)` `(off-path)` | **MUST** (`security-event`) | Retention removed rows. One row per purge run rather than per deleted row, carrying the range and the count — a per-row record of a per-row deletion is a log that grows faster than the purge shrinks it. It stays a row-level `DELETE` after [Phase 11](../../roadmap/phase-11-production-hardening.md) partitioning — one monthly partition holds many tenants and retention classes — and dropping a whole partition past the platform's maximum retention is [ADR-0028](../../decisions/0028-audit-log-partition-management.md)'s separate partition-management job. `(off-path)` for the same reason as redaction |

## What is deliberately not here

**No row for writing an audit row.** The obvious recursion, and the reason it is absent
is not that it recurses: it is that the row would carry no information the row it
describes does not already carry. `audit_log` is append-only, so a row's existence is
itself the record that it was written.

**No row for a failed write.** When a MUST-class row cannot be written durably the
operation is **rejected** — the caller sees `audit_unavailable`, and the failure is a
log line and a health-check signal, not a row in the table that just proved it cannot
accept one ([ADR-0033](../../decisions/0033-audit-durability-model.md)). A matrix entry
here would describe a write that by construction cannot happen.

**No row for reading `audit_config`.** The classifier reads it on every audited request
through a cached projection. A row per read would be a row per request, and the
information — that this tenant has overrides — is already on every row the classifier
produced.

## The two directions of the catalogue join

Per [ADR-0044 Amendment 3](../../decisions/0044-audit-write-path.md), and stated here
because this module's matrix is all `(planned)` and a reader will ask what the rule can
possibly be checking.

**Catalogue → matrix is total.** Every entry this module's `IAuditCatalogSource`
registers has a row here carrying the same slug, and one that does not fails. **This
module registers nothing in Packet 9**, and saying so is the point: every operation
below belongs to a surface that lands later, and declaring a slug the moment its writer
exists is what keeps the marker honest. A module with an empty registration is not a
module the rule skips — it is one whose obligations are all still on the matrix side.

**Matrix → catalogue binds only to a slug whose request type exists.** The five rows
above that are not `(off-path)` name commands nobody has written, so there is nothing to
join them to yet; each becomes a two-directional obligation the day its command lands.
That is what keeps `(planned)` from being an exemption: the marker says "no request type
yet", the rule re-derives whether that is true, and a row still marked `(planned)` after
its command ships fails. The two `(off-path)` rows sit outside the join in both
directions and are joined by neither — their writers are not requests: the GDPR erasure
handler Phase 03 lands and the retention purge Phase 11 lands, and each registers its slug
when it does.

## Retention

Audit rows for this module take the same retention class as every other MUST-class row —
[Audit Subsystem § 9](../../architecture/31-audit-subsystem.md) is the authority, and no
class is defined here. The two `(off-path)` rows are the ones a retention policy must
never expire ahead of the rows they describe: a purge record that outlives nothing is a
record of a deletion whose subject is gone, which is the only durable evidence that the
deletion happened.
