# Customization — Audit Coverage Matrix

Per [Audit Coverage](../../standards/18-audit-coverage.md), which names this
file. Part of the [module spec](README.md).

Four of the operations below now exist, all written by Phase 02a Packet 8's
handlers: `ContentType` register and publish, and `LevelTaxonomy` register and
publish. Eight more rows are classification ahead of code and carry `(planned)` in the
`Operation` column ([ADR-0044 Amendment 3](../../decisions/0044-audit-write-path.md));
the last row carries no slug at all, and its own cell says why.

**All four are unaudited today**, and publication is the one that matters most:
it retires the incumbent and makes every subsequent write of that content type
validate against a new shape, and nothing records who changed what a tenant's
data is allowed to look like. `AuditLogBehavior` lights up in
[Packet 9](../../roadmap/phase-02a-kernel-tenancy.md), which declares the same
operations in code through `IAuditCatalogSource.Describe(IAuditCatalogBuilder)`
rather than parsing this file — the catalogue is the executable artifact, this
matrix is the human-readable one, and
`Every_TenantOwned_Command_HasAuditCoverage` asserts the two agree
([ADR-0044 § 6](../../decisions/0044-audit-write-path.md)).

**That join runs in two directions and they have different domains**
([ADR-0044 Amendment 3](../../decisions/0044-audit-write-path.md)). Catalogue → matrix
is total: every entry this module's `IAuditCatalogSource` registers has a row here
carrying the same slug, and one that does not fails. Matrix → catalogue binds only to a
slug whose request type **exists**, so the eight `(planned)` rows are classification and
not drift — and a `(planned)` row whose command has since shipped fails, which is what
keeps the marker from becoming a hole rather than a claim. Nothing in this module is
written off the request path, so no row here carries the `(off-path)` marker the
[Tenancy matrix](../tenancy/audit.md) needs for five of its own.

**What these handlers already do so that Packet 9 is a wiring change and not a
rewrite:** every one runs inside the ambient transaction
([ADR-0040](../../decisions/0040-ambient-unit-of-work.md)), so the MUST-class row
[ADR-0033](../../decisions/0033-audit-durability-model.md) requires commits with
the state change or not at all, and it executes while `app.tenant_id` is set,
which is what lets Row Level Security accept it. Every one attributes its write
to `tenantContext.UserId ?? UserId.SystemActor`, so the actor the audit row needs
is already resolved rather than reconstructed. Nothing has to move for the row to
be added.

**The band rows are captured with the aggregate.** `TenantLevelTaxonomyItem` is a
plain class rather than an `AuditableEntity<>` descendant, and § Baseline Coverage
requires both `before` and `after` on a customization schema change.
`AuditChangeTrackerInterceptor` captures every `ChangeTracker` entry in state
`Added`, `Modified` or `Deleted` minus a named exclusion list
([ADR-0044 § 7](../../decisions/0044-audit-write-path.md)), so an added or removed
band appears in the operation's `changes` array instead of being missed by a
base-class predicate.

This matrix is not the floor — [Audit Coverage § Baseline Coverage](../../standards/18-audit-coverage.md)
is, and a module matrix "cannot remove anything in this list". This file adds
rows beneath that baseline and classifies what the baseline leaves open; a tenant
`AuditConfig` may then narrow SHOULD/MAY at runtime. Neither touches a baseline
MUST.

**Nothing here is SHOULD, and that is the baseline's doing rather than this
file's caution.** The Customization row of § Baseline Coverage names these
aggregates "created / updated / deleted" — every operation an aggregate has — so
each row below is MUST because the floor already is. The rename rows said SHOULD for
being presentational; a rename is an update, and the class was the baseline's to
set. The one row that is not MUST is not an aggregate operation at all.

The `Operation` column carries the catalogue key — `{module}.{resource}.{verb}`,
the shape of a permission key with a verb this matrix names rather than the closed
action set ([ADR-0044 § 6](../../decisions/0044-audit-write-path.md)). That is why
publication is `customization.content_type.publish` here and
`customization.content_type_publication.write` in
[permissions.md](permissions.md): a permission bounds what a principal may do, and
an audit operation records what happened. One slug per cell and one row per audited
`(resource, operation)` — the column is read as a column of slugs and not parsed as
prose
([Audit Coverage § Classification Matrix Template](../../standards/18-audit-coverage.md)),
a cell carrying `(planned)` beside its slug where the command that will raise it does
not exist yet, and the legend's `–` is what the last row carries, because it has no
audited operation at all.

| Resource | Operation | Class | Why |
|---|---|---|---|
| `ContentType` | `customization.content_type.register` | **MUST** | A tenant declaring a new shape for its own data; the row is what a later "who added this?" reads |
| `ContentType` | `customization.content_type.publish` | **MUST** | Retires the incumbent and changes what every subsequent content write is validated against — the highest-blast-radius act in the module |
| `ContentType` | `customization.content_type.revise` `(planned)` | **MUST** | Only a draft's body is mutable, but "additive" is claimed by the editor and not proved by the aggregate; the row is what makes a wrong claim traceable |
| `ContentType` | `customization.content_type.rename` `(planned)` | **MUST** | The baseline lists these aggregates' **created / updated / deleted**, and a rename is an update. It is presentational, which changes the payload and not the class: the display name is what every Studio list, every editor and every renderer shows for the shape |
| `ContentType` | `customization.content_type.soft_delete` `(planned)` | **MUST** | Frees the key for a successor, because the one-live-revision index is partial on `deleted_at IS NULL`; a retired definition releasing its name is the same class of act as a domain releasing its host |
| `LevelTaxonomy` | `customization.level_taxonomy.register` | **MUST** | Same reason as the content type's registration: a tenant declaring a vocabulary of its own, and the row is what a later "who added this?" reads |
| `LevelTaxonomy` | `customization.level_taxonomy.publish` | **MUST** | Same reason as the content type's publication, and with a wider reach: every level reference in the tenant resolves through the vocabulary this retires and replaces |
| `LevelTaxonomy` | `customization.level_taxonomy.add_band` `(planned)` | **MUST** | Extends the vocabulary every level reference resolves against, and the aggregate permits it while the revision is a draft |
| `LevelTaxonomy` | `customization.level_taxonomy.remove_band` `(planned)` | **MUST** | Removing a band strands every row that referenced it, and the aggregate permits it while the revision is a draft |
| `LevelTaxonomy` | `customization.level_taxonomy.rename` `(planned)` | **MUST** | Same reason as the content type's rename: the baseline lists these aggregates' **created / updated / deleted**, and a rename is an update |
| `LevelTaxonomy` | `customization.level_taxonomy.rename_band` `(planned)` | **MUST** | A band's label is what a learner sees where a level is named, so renaming one is an update of the taxonomy and not of its presentation |
| `LevelTaxonomy` | `customization.level_taxonomy.soft_delete` `(planned)` | **MUST** | Same reason as the content type's |
| `customization_generations` | – | – | **The bump is deliberately unaudited.** It is not an aggregate ([ADR-0043 § 7](../../decisions/0043-customization-payload-validation.md)), it carries no decision, and it is written exactly once per audited operation above — a row for it would be a second entry for the same act, in the same transaction, saying less. Nor does the broad capture predicate pick it up: `CustomizationGenerationStore` bumps the row with one `ON CONFLICT … DO UPDATE` statement on the ambient connection, so it is never a `ChangeTracker` entry for `AuditChangeTrackerInterceptor` to see |

**A refused schema is not an operation of its own.** A document the four gates
reject never becomes state: no definition row is written, no generation is bumped,
and the caller gets a `400` naming the JSON pointer that failed. What the pipeline
records is the operation the request already declared, carrying a non-success
`outcome` — the column is a four-value closed set for exactly that reason
([ADR-0044 § 5](../../decisions/0044-audit-write-path.md)) — and not a second row
for the attempt. Auditing attempts as operations in their own right is a different
decision with a different owner:
[Audit Coverage](../../standards/18-audit-coverage.md) puts authentication failures
on `security-event` and says nothing about validation failures, and a tenant fixing
its own draft in a loop would be the noisiest writer in the system.

The classification is inert until
[Packet 9](../../roadmap/phase-02a-kernel-tenancy.md) lights up
`AuditLogBehavior`.
