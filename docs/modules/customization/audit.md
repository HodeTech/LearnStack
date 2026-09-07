# Customization — Audit Coverage Matrix

Per [Audit Coverage](../../standards/18-audit-coverage.md), which names this
file. Part of the [module spec](README.md).

Four of the operations below now exist, all written by Phase 02a Packet 8's
handlers: `ContentType` register and publish, and `LevelTaxonomy` register and
publish. The rest are classification ahead of code.

**All four are unaudited today**, and publication is the one that matters most:
it retires the incumbent and makes every subsequent write of that content type
validate against a new shape, and nothing records who changed what a tenant's
data is allowed to look like. `AuditLogBehavior` lights up in
[Packet 9](../../roadmap/phase-02a-kernel-tenancy.md), which transcribes its
in-process catalogue from this file.

**What these handlers already do so that Packet 9 is a wiring change and not a
rewrite:** every one runs inside the ambient transaction
([ADR-0040](../../decisions/0040-ambient-unit-of-work.md)), so the MUST-class row
[ADR-0033](../../decisions/0033-audit-durability-model.md) requires commits with
the state change or not at all, and it executes while `app.tenant_id` is set,
which is what lets Row Level Security accept it. Every one attributes its write
to `tenantContext.UserId ?? UserId.SystemActor`, so the actor the audit row needs
is already resolved rather than reconstructed. Nothing has to move for the row to
be added.

This matrix is not the floor — [Audit Coverage § Baseline Coverage](../../standards/18-audit-coverage.md)
is, and a module matrix "cannot remove anything in this list". This file adds
rows beneath that baseline and classifies what the baseline leaves open; a tenant
`AuditConfig` may then narrow SHOULD/MAY at runtime. Neither touches a baseline
MUST.

**Nothing here is SHOULD, and that is the baseline's doing rather than this
file's caution.** The Customization row of § Baseline Coverage names these
aggregates "created / updated / deleted" — every operation an aggregate has — so
each row below is MUST because the floor already is. Two rows said SHOULD for
being presentational; a rename is an update, and the class was the baseline's to
set. The one row that is not MUST is not an aggregate operation at all.

| Resource | Operation | Class | Why |
|---|---|---|---|
| `ContentType` | register | **MUST** | A tenant declaring a new shape for its own data; the row is what a later "who added this?" reads |
| `ContentType` | publish | **MUST** | Retires the incumbent and changes what every subsequent content write is validated against — the highest-blast-radius act in the module |
| `ContentType` | revise (additive) | **MUST** | Only a draft's body is mutable, but "additive" is claimed by the editor and not proved by the aggregate; the row is what makes a wrong claim traceable |
| `ContentType` | rename | **MUST** | The baseline lists these aggregates' **created / updated / deleted**, and a rename is an update. It is presentational, which changes the payload and not the class: the display name is what every Studio list, every editor and every renderer shows for the shape |
| `ContentType` | soft delete | **MUST** | Frees the key for a successor, because the one-live-revision index is partial on `deleted_at IS NULL`; a retired definition releasing its name is the same class of act as a released domain |
| `LevelTaxonomy` | register / publish | **MUST** | Same two reasons as the content type's; a level vocabulary is what every level reference in the tenant resolves through |
| `LevelTaxonomy` | add / remove band | **MUST** | Removing a band strands every row that referenced it, and the aggregate permits it while the revision is a draft |
| `LevelTaxonomy` | rename, band rename | **MUST** | Same reason. A band's label is what a learner sees where a level is named, and renaming one is an update of the taxonomy |
| `LevelTaxonomy` | soft delete | **MUST** | Same reason as the content type's |
| `customization_generations` | bump | – | **Deliberately unaudited.** It is not an aggregate ([ADR-0043 § 7](../../decisions/0043-customization-payload-validation.md)), it carries no decision, and it is written exactly once per audited operation above — a row for it would be a second entry for the same act, in the same transaction, saying less |

**A refused schema is not an audit row either.** A document the four gates reject
never becomes state: no row is written, no generation is bumped, and the caller
gets a `400` naming the JSON pointer that failed. Auditing attempts rather than
changes is a different decision with a different owner —
[Audit Coverage](../../standards/18-audit-coverage.md) puts authentication
failures on `security-event` and says nothing about validation failures, and a
tenant fixing its own draft in a loop would be the noisiest writer in the system.

The classification is inert until
[Packet 9](../../roadmap/phase-02a-kernel-tenancy.md) lights up
`AuditLogBehavior`.
