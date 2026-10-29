# Module Spec — Customization

**Status:** Design stable, partially implemented (Phase 02a Packet 8 shipped the
two aggregates, the schema and its isolation, the payload gate, and the write
path; P02d-2 Step 1 adds contextual exact-definition reads and metadata validation.
P02d-3 adds internal generation-cached display reads; P02d-4 delivers public consumers
in [Phase 02d](../../roadmap/phase-02d-walking-skeleton.md), and the Admin Studio
editors with the phases that consume them).
The fixed-seed continuation extension derives from
[ADR-0080](../../decisions/0080-fixed-seed-prerequisite-read-scope.md). Its new
guards and race proofs remain governed by the
[Phase 03 Status block](../../roadmap/phase-03-identity-admin.md).
The separate fixed-origin completion contract derives from Accepted
[ADR-0081](../../decisions/0081-fixed-seed-completion-before-origin-adoption.md);
acceptance does not qualify its owning readers or completion proofs.

The second module spec in the repository, per
[Documentation Standards § Per-Module Specifications](../../standards/13-documentation.md).

## Overview

Customization owns **the shapes a tenant declares its own data will take**, and
nothing about the data itself.

It is the module [ADR-0018](../../decisions/0018-tenant-driven-customization-model.md)
exists for: one binary and one schema serve a language school, a yoga studio and
a coding bootcamp because what differs between them is rows in these tables, not
code in any module. The boundary of that claim is
[Platform Vision § Genericity boundary](../../architecture/01-platform-vision.md)
— content shape, presentation and pure rule evaluation are tenant data; stateful
entitlement and external capability invocation are platform features gated by
plan.

**It owns:**

- **`TenantContentType`** — a tenant-authored JSON Schema declaring the shape of
  one kind of content, plus the composite renderer that draws it.
- **`TenantLevelTaxonomy`** — the tenant's level or difficulty vocabulary, and
  the bands inside it. CEFR for a language school, kyu/dan for a dojo, whatever
  a yoga studio calls its levels.
- **`customization_generations`** — one counter per tenant, embedded in every
  cache key this module's reads compose. Not an aggregate
  ([ADR-0043 § 7](../../decisions/0043-customization-payload-validation.md)).

**Phase 03 addition, not yet implemented:** `TenantCustomFieldDef` belongs to
Customization; Identity owns the membership values and consumes the definition
contract. [G9](../../roadmap/phase-03-identity-admin.md#the-decision-register)
settles immutable revisions, value pins, classification history and schema
admission before that aggregate's first migration. Its permission/audit matrices
and module-spec diagrams extend with the implementation, not with a second
Identity-owned definition table.

**It does not own:**

- **The content.** A `TenantContentType` says what an article looks like; the
  articles live in Content, from [Phase 04](../../roadmap/phase-04-cms-media-pages.md).
- **The renderers.** `default-card` and its eight siblings are keys of frontend
  composites; this module stores the keys, and
  [`Composite_Renderer_Keys_Match_The_Frontend_Registry`](../../standards/21-architecture-tests-catalogue.md#composite_renderer_keys_match_the_frontend_registry)
  holds the frontend's registered set inside the nine this module declares —
  containment, not equality.
- **The evaluator.** Scoring and completion rule bodies are `text` plus a
  `dialect` discriminator, decided here and given a table in
  [Phase 05](../../roadmap/phase-05-education-learning-content.md) once ADR-0025
  chooses the language.
- **Validation of instances against a schema.** `IJsonSchemaValidator` lives in
  the shared kernel because four modules need it, and this module is only its
  first caller. It admits LearnStack's own `x-renderer` / `x-taxonomy` /
  `x-language` keywords and reports where each one sits; **resolving** them
  against the registries is this module's, per
  [ADR-0043 § 4](../../decisions/0043-customization-payload-validation.md), because
  two of the registries are its own and the third is a tenant's rows.

## Entity-relationship diagram

```mermaid
erDiagram
    tenant_content_types {
        uuid id PK
        uuid tenant_id
        varchar key
        int schema_version
        int schema_revision
        text status
        jsonb display_name
        jsonb json_schema
        varchar renderer_key
    }
    tenant_level_taxonomies {
        uuid id PK
        uuid tenant_id
        varchar key
        int schema_version
        int schema_revision
        text status
        jsonb display_name
    }
    tenant_level_taxonomy_items {
        uuid tenant_id PK
        varchar taxonomy_key PK
        int schema_version PK
        varchar key PK
        jsonb display_name
        smallint sort
        jsonb metadata
    }
    customization_generations {
        uuid tenant_id PK
        bigint generation
    }

    tenant_level_taxonomies ||--o{ tenant_level_taxonomy_items : "bands, ON DELETE CASCADE"
```

Text fallback: four tables. The two definition tables are tenant-owned and
tenant-wide, keyed on a surrogate `id` with
`UNIQUE (tenant_id, key, schema_version)` as the natural key and a partial
`UNIQUE (tenant_id, key) WHERE status = 'Active' AND deleted_at IS NULL` holding
one live revision per concept. Taxonomy items are a child table inside the
taxonomy aggregate, keyed on `(tenant_id, taxonomy_key, schema_version, key)`
with a composite foreign key that includes `tenant_id` — because referential
integrity checks run with row security bypassed, and a single-column key would
let one tenant's band point at another's taxonomy. The generation counter is one
row per tenant and references nothing.

Every one of the four is under `ENABLE` **and** `FORCE ROW LEVEL SECURITY` with
the tenant-owned tenant-wide policy from
[Database Standards § Table classes](../../standards/05-database.md).

## State diagrams

```mermaid
stateDiagram-v2
    [*] --> Draft: Register
    Draft --> Active: Publish
    Active --> Deprecated: Publish of a successor
    Draft --> [*]: deleted, nothing referenced it
    Deprecated --> [*]: never; rows that pinned it still resolve
```

Text fallback: a definition is registered as `Draft`, published to `Active`, and
retired to `Deprecated` when a successor is published. Only a `Draft`'s body is
mutable — a published revision was validated against stored instances that
cannot be re-validated retroactively, so a change a stored instance could fail
raises `schema_version` instead. A `Deprecated` revision is history and is never
removed: content rows pin the version they were written against.

## Sequence diagrams

### Primary write: publishing a successor

```mermaid
sequenceDiagram
    participant Admin
    participant Handler as PublishTenantContentTypeCommandHandler
    participant Store as ITenantContentTypeStore
    participant Gen as ICustomizationGenerationStore
    participant DB as PostgreSQL

    Admin->>Handler: PublishTenantContentTypeCommand(id)
    Handler->>Store: FindAsync(id)
    Store->>DB: SELECT … WHERE id = … AND deleted_at IS NULL
    Handler->>Store: FindActiveAsync(key)
    Store->>DB: SELECT … WHERE key = … AND status = 'Active'
    Handler->>Store: UpdateAsync(incumbent → Deprecated)
    Store->>DB: UPDATE … WHERE row_version = …
    Handler->>Store: UpdateAsync(successor → Active)
    Store->>DB: UPDATE … WHERE row_version = …
    Handler->>Gen: BumpAsync(tenant)
    Gen->>DB: INSERT … ON CONFLICT DO UPDATE … RETURNING generation
```

Text fallback: the handler loads the successor, refuses anything that is not a
`Draft`, finds the live revision of the same key, deprecates it, publishes the
successor, and bumps the generation. **The order is load-bearing**: the partial
index admits one live row per key, so publishing before retiring is the one
ordering PostgreSQL rejects — and it would reject it after the successor's
`UPDATE` had already been sent. All of it is one transaction, because
[ADR-0040](../../decisions/0040-ambient-unit-of-work.md) gives the scope one.

P02d-2's concurrent seed proof exposed a same-revision publication race: the active
read can see a competitor's commit while EF retains the earlier tracked Draft.
Both publish handlers return typed concurrency refusal when that read identifies
the successor itself, without retiring it. Any failed publication save marks the
ambient unit rollback-only, including first publication with no incumbent.
[CustomizationPublicationConcurrencyTests](../../../backend/tests/LearnStack.Tests.Integration/Database/CustomizationPublicationConcurrencyTests.cs)
coordinates the intervening commit and proves absorbed post-save failures cannot
commit either the dirty successor or a later write.

Both publish commands accept an optional `RequireNoIncumbent` precondition.
Contextual Off queries check the logical key's Active identity before registration.
Convergent seed also passes true: a different Active revision is refused before mutation
as `business_rule_violation` / `lockey_customization_key_already_live`. Default false
retains ordinary revision succession. The same-row race remains typed concurrency;
an Active winner from another revision is never retired or adopted by authored seed.
Built-ins use the separate semantic initialization path described below.

Ordinary content-type registration enforces the
[accepted authoring quota](../../roadmap/phase-04-cms-media-pages.md#content-type-quota-accounting).
Custom-key writers acquire a tenant-wide transaction advisory lock before their
concept lock, then count usage and insert inside the same ambient transaction.
The standard `limit_exceeded` refusal names the limit key and allowance, maps to
403 and a denied audit outcome, and is permanent for background-job failure
classification. This changes accounting, not deletion or restoration authority.

### Primary integration-event flow: provisioning defaults

**Implemented by P02b-3 Step 5; complete and merged through PR #31.** The
[merge closeout](../../roadmap/phase-02b-delivery-log.md#p02b-3-merge-and-closeout-2026-10-15)
owns final verification; step-level validation/reviews are recorded below.
[ADR-0057](../../decisions/0057-transactional-event-production-and-completion.md#first-useful-production-flow)
authorizes independent `card@1` content-type and `plain@1` taxonomy initialization
on completed tenant
provisioning:

```mermaid
flowchart LR
    T[Completed provisioning] --> O[Transactional outbox]
    O --> D[P02b-2 dispatcher]
    D --> C[P02b-3 card@1 subscription]
    D --> P[P02b-3 plain@1 subscription]
    C --> CT[One root + generation + inbox + MUST audit]
    P --> PT[One root + generation + inbox + MUST audit]
```

Text fallback: Tenancy persists the completed-provisioning fact; in P02b-3,
independent subscriptions each create one untouched built-in concept on their
own delivery transaction. One may finish before its sibling; retry completes
an incomplete pair without overwriting tenant decisions.

### Primary read flow: resolving a tenant's shapes

**P02d-3 Step 3 implemented — 2026-10-02; both review rounds passed.**
`ICustomizationDefinitionProjectionReader` resolves batched exact revision pins
through ADR-0010's application-contract mechanism. Values are immutable; no public
table, schema validation, HTTP endpoint or write is introduced. Active/Deprecated
nondeleted definitions are eligible; missing individual pins remain distinguishable
without failing unrelated members or substituting another revision. Labels resolve
per call with actual locale metadata from the caller's display-locale context.
P02d-4 Step 3 delivers the public response/refusal contract;
P02d-6 Steps 2–3 deliver the catalog, course and lesson views. The coherent loader
supplies
generation-keyed families; dirty or rollback-only
scopes bypass their cache. The writer reader remains uncached.

[Cache strategy § 8.2](../../architecture/32-tenant-customization-model.md#82-cache-strategy)
owns family keys, ambient snapshot loading, dirty-scope bypass, fault/cancellation
behavior and freshness. The two families include eligible retained revisions and
immutable bands; the writer's exact-purpose reader below stays uncached.
[P02d-3's accepted package](../../roadmap/phase-02d-delivery-log.md#p02d-3-decision-package-2026-10-02)
owns implementation steps and proof obligations. P02d-4's accepted Education
consumer uses exact pins and ordered allowlisted string fields, with whole-content
unavailable outcomes. Internal field requiredness comes from schema.required;
no public required flag or JSON Schema evaluation is added. Unknown fields are omitted;
Missing required/non-object/non-string body data makes that lesson's whole content
unavailable in the Education consumer. Customization supplies internal metadata;
unrelated members of its batched definition projection remain usable.

## P02d-2 accepted exact write contract

**Step 1 implemented, both reviews passed — 2026-10-02.** An application interface in
`Customization.Application.Contracts` resolves an exact content-type or taxonomy
revision for the caller's announced tenant. DTOs contain values only: key, version,
status, JSON Schema/composite and validated presentation, or immutable bands/labels.
The selected interface is `IExactCustomizationDefinitionReader`; it takes an explicit
binding purpose (`NewBinding` or `ExistingPin`), never an inferred live version.

- New Course taxonomy and new Lesson content-type bindings require the exact Active
  revision; a present band must be declared in that revision.
- A translation on an existing Lesson can use its exact Active or Deprecated pin.
  No command rewrites a pin or replaces it with the newest revision.
- Absent, deleted, Draft or cross-tenant definitions return the same bounded binding
  `validation_failed`; an error cannot expose another tenant's revision or label.
- Read uncached in the caller's ambient transaction/context. Eligibility is observed
  at this read, not promised Active-at-commit; immutable schema/bands protect the pin
  if it is concurrently deprecated. Strict commit-time eligibility is not selected.
- Body validation uses `IJsonSchemaValidator` against that returned exact schema.
  Generation-keyed read caching is delivered in P02d-3; P02d-4 public shape is
  delivered by Step 3. P02d-6 Step 3 implements the bounded ordered-text renderer
  and unavailable fallback. These are
  separate from the exact write contract.
- Module-owned contextual verification queries give the seeder exact IDs, revision
  data, labels/bands and state. They are audit Off and introduce no setter exception.

[ADR-0051](../../decisions/0051-ordered-text-card-presentation.md) defines the optional
root `x-fields` profile and semantic resolver. It preserves the four gates and legacy
schemas; P02d-2 Step 1 implements the parser and semantic resolver. Seed definitions opt
into the profile, whereas built-in `card`/`plain` remain unchanged and Active.

### Fixed-seed continuation

The six fixed-seed writer types (both built-in initializers and ContentType/
Taxonomy register/publish) use the
[ordinary-scope per-write contract](../../standards/05-database.md#every-tenant-bound-seed-write).
Module-local guards bind the exact active fixed declaration, take an exclusive
Tenant fence before quota/concept locks, then freshly verify pristine Tenancy
prerequisites, absence of all retained Identity Role history and this module's
exact retained target/predecessor state, natural keys, expected versions and
immutable authored pins. Owning Application.Contracts ports use that same owner;
pre-wait tracked objects are not evidence. No temporary seed hatch is used.
Standard 05 owns the monotonic Identity-stage and admitted immutable-content
proof, its cross-branch limits and Phase 06 reopening requirement.

The separate [fixed-origin completion operation](../../standards/05-database.md#fixed-seed-completion-read-authority)
uses a closed Customization Application.Contracts reader on the adoption owner's
ordinary narrow scope. It verifies required built-in/authored ContentType and
Taxonomy roots, finite child graphs, exact immutable revision/content pins and
declared completed publication state. It returns only admission/refusal; no
schema, label, child graph or immutable payload escapes. This reader is not a
seventh content writer or a pristine-read consumer and grants no repair or
publication authority. Standard 05 owns the fixed declaration and same-owner
completion boundary; Phase 06 reopens it before mutable content administration.

## Component diagram

```mermaid
graph TD
    Contracts[Customization.Application.Contracts<br/>four commands, BuiltInCustomizations]
    App[Customization.Application<br/>handlers, validators, ports]
    Domain[Customization.Domain<br/>two aggregates, the generation row]
    Infra[Customization.Infrastructure<br/>DbContext, stores, migration]
    Gate[SharedKernel IJsonSchemaValidator<br/>→ Infrastructure.Validation]
    Seeder[Tools.Seeder]

    Seeder --> Contracts
    Seeder --> App
    Seeder --> Infra
    App --> Contracts
    App --> Domain
    App --> Gate
    Infra --> App
    Infra --> Domain
```

Text fallback: the seeder is the second composition root, so it references the
Application and Infrastructure projects as well as the contracts — it has to
register the ports itself. Contracts name only `SharedKernel` types, so a module
sending a command does not take a dependency on `Customization.Domain` — the forbidden
`Module A → Module B.Domain` edge. Application declares the ports; Infrastructure
implements them and is wired at the composition root. The JSON-Schema gate is a
shared-kernel port with one adapter, and `JsonSchema_Net_Types_NotImportedOutsideInfrastructure`
keeps the library's types inside it.

## Integration-event catalogue

Customization publishes no event and implements two subscribers. ADR-0057 fixes
the useful effects; ADR-0046 fixes their stable identities and transport-owned
inbox. P02b-3 Step 5 activates both real subscriptions together over durable
acceptance and the semantic Seeder handoff. Each effect owns one root, generation
change, inbox outcome and MUST audit. Customization mints UUIDv7 IDs and resolves
semantic key/version
identity; Tenancy carries neither schemas nor Customization IDs.

| Consumed topic | Event | Stable subscription | Independent effect | Owner |
|---|---|---|---|---|
| `learnstack.tenancy.tenant` | `TenantCreatedIntegrationEventV1` | `customization.initialize-content-type-default.v1` | Create untouched `card@1` content type (`BuiltInCustomizations.Card`) | P02b-3 Step 5; Active |
| `learnstack.tenancy.tenant` | `TenantCreatedIntegrationEventV1` | `customization.initialize-taxonomy-default.v1` | Create untouched `plain@1` taxonomy (`BuiltInCustomizations.Plain`) | P02b-3 Step 5; Active |

Initialization preserves Draft, Active, Deprecated, successor and deliberately
deleted definitions; it never overwrites, republishes or resurrects them. Each
parameterless initializer takes its tenant from trusted context, acquires at
least the canonical shared Tenant fence before quota/concept locks, including the
transaction-owned semantic lock shared by ordinary registration, then checks all
revision/status/deletion history under RLS. A new root is validated and Active
before one save, generation bump and MUST audit; a preserved attempt changes no
root or generation. The coordinating handlers are explicitly Off and send one
classified nested command, refusing a failed result rather than completing inbox.

The built-in IntegrationEvent source remains distinct from the exact fixed Seed
source and receives no seed prerequisite witness. The new fence/competitor proof
is part of the ADR-0080 extension; the phase Status block owns qualification.

Seeder discovers built-ins by key/version and verifies their exact Active shape,
retaining warm/generated IDs. Authored curriculum identities/pins stay exact.
The [Step 5 record](../../roadmap/phase-02b-delivery-log.md#p02b-3-step-5--useful-defaults-and-semantic-seed-handoff-2026-10-13)
owns initialization, race and Seeder validation/review status.

No other module reacts to a customization change through events today. Public
readers use the delivered generation-keyed cache with fresh generation probes; a cache
key that changes is a cheaper invalidation than an event every pod has to
receive. An event becomes owed when a second module needs to *act* on a change
rather than merely notice it; [Phase 04](../../roadmap/phase-04-cms-media-pages.md)
is where that is decided, because it brings the content rows a schema change
would have to be reconciled against.

## Permission matrix

In [permissions.md](permissions.md), the file
[Permission Standards](../../standards/19-permissions.md) names.

## Audit coverage matrix

In [audit.md](audit.md), the file
[Audit Coverage](../../standards/18-audit-coverage.md) names.

## Performance budget

| Path | Budget | Why this number |
|---|---|---|
| Resolve batched definitions (warm) | **< 1 ms** for in-memory resolution, excluding SQL probe | One fresh generation SELECT; no definition query. End-to-end timing measured separately in P02d-3 |
| Resolve batched definitions (cold/partial/fault) | **< 20 ms** production p95 target, not proven by the local sample | At most two SELECTs: probe plus coherent generation/rows snapshot; [seeded measurements](../../roadmap/phase-02d-delivery-log.md#step-3-generation-cache-and-read-safety) report rows/bytes and query plans, not production p95 |
| Admit a tenant-authored schema (four gates) | **< 50 ms** p95 | Interactive, on save, and rare |
| Validate one instance at the § 8.4 caps | **742 ms, 1.6 GB** | Measured worst case, not a budget — see below |
| Publish a successor (2 reads, 2 updates, 1 upsert) | **< 100 ms** p95 | Interactive but rare |

The instance-validation number is the one to read carefully: it is a **measured
worst case** at
[§ 8.4](../../architecture/32-tenant-customization-model.md)'s declared caps —
100 properties over a 1 MiB instance — not a target. It is why the caps exist,
and why the compiled schema does not outlive a call: compiling is measured
*cheaper* than evaluating, so a cache would have added a hit-rate problem to
solve none.

## Risks and open questions

- **Which lifecycle stage a `schema_revision` bump belongs to is open.**
  [ADR-0043 § 6](../../decisions/0043-customization-payload-validation.md)
  deliberately leaves it open, having removed the cache that would have had to
  key on it. Today `AddItem` and `RemoveItem` each count as one additive edit, so
  a taxonomy registered with three bands is at revision 3 — consistent with the
  aggregate's own rule and unconstrained by anything else.
  [Phase 04 § Customization Key Shape](../../roadmap/phase-04-cms-media-pages.md)
  owns the answer.
- **No permission check gates these commands.** Reachability stands in for
  authorization exactly as it does in Tenancy, and for the same reason: there is
  no HTTP endpoint. [Phase 03](../../roadmap/phase-03-identity-admin.md) brings
  the registry, and [permissions.md](permissions.md) is the forward declaration.
- **Audit rows ride the pipeline.** Since Packet 9 the four shipped commands write
  MUST rows through `AuditLogBehavior` and `TransactionBehavior`
  ([ADR-0033](../../decisions/0033-audit-durability-model.md)); [audit.md](audit.md)
  classifies them and the eight `(planned)` operations.
- **The additive claim on a revision is unchecked.** `ReviseSchema` enforces the
  half an aggregate can — that the body is still a draft — and leaves the diff
  that decides whether a change only *adds* to the editor,
  [Phase 04](../../roadmap/phase-04-cms-media-pages.md).
- **`x-language` is admitted without resolving.** `x-renderer` resolves against
  the twelve generic primitives and `x-taxonomy` against the tenant's own
  taxonomies, both on save; the set of languages a `code` field may declare is
  decided by nothing in the corpus and belongs to
  [Phase 04](../../roadmap/phase-04-cms-media-pages.md) with the field type that
  carries one. [§ 8.1](../../architecture/32-tenant-customization-model.md) records
  the gap rather than leaving the invariant reading as though it were closed.
- **Publishing an older revision silently demotes a newer live one.** `Publish`
  retires whatever is `Active` for the key without comparing `schema_version`, so
  a `Draft` left over at v1 makes v1 live again and deprecates v3. That is
  *coherent* with the model — an entry pins the version it was written against,
  and [ADR-0013](../../decisions/0013-page-block-schema-versioning.md) keeps the
  deprecated revision resolvable — so it is a rollback rather than a corruption,
  and refusing it would forbid one. What is missing is the editor that makes the
  choice deliberate: [Phase 04](../../roadmap/phase-04-cms-media-pages.md) owns
  the diff that decides additive from breaking, and this question with it.
- **The lifecycle guards do not mention soft delete, and nothing can reach them.**
  `Publish` and `ReviseSchema` do not ask whether the row is deleted. They cannot
  be handed one: there is no delete command, and both store reads exclude
  `deleted_at IS NOT NULL`. The guard is owed by whichever phase ships the delete
  command — [Phase 04](../../roadmap/phase-04-cms-media-pages.md) for content
  types, per [audit.md](audit.md)'s `soft delete` row — and writing it now would
  be a branch no test could kill.
- **A tenant can strand its own content.** Deprecating the only live revision of
  a key leaves content rows pinned to a version nothing publishes. The schema
  permits it and no command refuses it, because "is this key still needed?" is a
  question only the module that owns the content rows can answer —
  [Phase 04](../../roadmap/phase-04-cms-media-pages.md) again.

## P02d-2 Step 1 delivery

`IExactCustomizationDefinitionReader` now returns immutable exact-revision DTOs
through the ambient tenant-filtered context, without a cache or live-key
substitution. New bindings require Active; existing pins admit Active/Deprecated.
Draft, deleted and invisible definitions share the bounded validation refusal.

ADR-0051's optional root `x-fields` is admitted by the generic schema profile and
semantically resolved by Customization after all four gates, before persistence.
Descriptors cover every direct string property once, preserve array order and
carry validated `LocalizedText` labels; only `default-card` is compatible. Legacy
schemas without the extension retain their original admission and empty presentation.
The exact reader returns the same resolved descriptors; a new rendering consumer
still belongs to P02d-6, rather than being implied by valid schema storage.

The contextual content-type/taxonomy queries supply exact seed verification DTOs
and are classified Off. Their adapters and the exact reader are registered in both
API and Seeder composition roots.
