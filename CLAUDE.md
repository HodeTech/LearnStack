# Working in this repository

This file is read first by Claude Code (and any other agent following the
convention). It tells you what this project is, what state it is in, and
the conventions you must follow when contributing.

## What this is

LearnStack is a **white-label platform for multi-branch education
businesses that teach live**. Its endorsed product direction adds an optional
LearnStack-branded Course Marketplace while preserving independent institution
sites. [ADR-0049](docs/decisions/0049-institution-sites-and-course-marketplace.md)
and [Phase 09a](docs/roadmap/phase-09a-course-marketplace-pilot.md) remain Proposed;
marketplace architecture, commerce and delivery are not implemented or Accepted.
One binary, one schema, and one set of container
images serve a language school, a yoga studio, a music school, or a
coding bootcamp. What differs between them is **tenant customization
data** loaded at provisioning, not code
([ADR-0018](docs/decisions/0018-tenant-driven-customization-model.md)).

That claim has a stated edge, and the edge lives in exactly one place:
[Platform Vision § Genericity boundary](docs/architecture/01-platform-vision.md).
Content shape, presentation, and pure rule evaluation are tenant data.
Stateful entitlement (credit packs, session quotas) and external
capability invocation (running submitted code, scoring speech) are
platform features gated by plan — they need a release, not a
customization row. Link to that section; do not restate it.

LearnStack targets three production deployment modes — SaaS, Dedicated,
Self-Hosted — with current readiness recorded in
[Deployment Models](docs/architecture/25-deployment-models.md#supported-today-versus-prepared-seam).
The companion **LearnStack Hub** control plane lives in a separate repository (see
[ADR-0019](docs/decisions/0019-learnstack-hub.md)). On developer
workstations the Hub repo is the sibling directory `../LearnStack-Hub`;
GitHub: https://github.com/HodeTech/LearnStack-Hub. The Hub repository
**owns its own roadmap** at `../LearnStack-Hub/docs/roadmap/`; this
repository holds only LearnStack's side of the boundary, in
[Phase 02c](docs/roadmap/phase-02c-hub-foundation.md).

## What state this is in

**Phase 01, Phase 02a and Phase 02d are complete; Phase 02b is the live phase.**
[Phase 02d](docs/roadmap/phase-02d-walking-skeleton.md), the two-tenant walking
skeleton and the first user-visible milestone, merged through PR #28 on
2026-10-11: two hosts, two tenants, two education sites, one binary and one
database. In [Phase 02b](docs/roadmap/phase-02b-events-auth.md), P02b-0 through
P02b-5 are complete and P02b-1–5 merged through PRs #29–#33 — transactional event
production, durable dispatch, subscriber delivery, background jobs and tenant API
authentication — and P02b-6's realm and identity implementation is merged through
PR #34. The phase document records its correction closeout and owns the delivery
state of P02b-7 (BFF session) and P02b-8 (exit enforcement). Phase 06 retains the full
renderer/Studio expansion.

**Status lives in one place.** Each phase document's Status block owns packet
state and its decision register owns gate state; the phase's delivery log
(`docs/roadmap/phase-<id>-delivery-log.md`) owns the dated decision packages, step
records, reviews and closeouts
([ADR-0069](docs/decisions/0069-phase-documents-and-delivery-logs.md)). This
section stays a pointer: it links phase documents, never a delivery log, and does
not restate a packet's history.

**What the kernel already guarantees** — tenancy and Row Level Security
isolation, the audit write path, the entitlement socket, the API conventions and
the foundation ports — is recorded packet by packet in
[Phase 02a](docs/roadmap/phase-02a-kernel-tenancy.md), whose delivery records are
frozen history, together with the 2026-08-08 restructure that moved correctness
earlier, additive infrastructure later and proof earlier.

**Four modules hold domain code:** Tenancy, Customization, Audit and Education.
Content, Identity and Media remain scaffolded. Module-level references in the docs
to types such as `ILiveClassProvider` or `ITenantSearch` describe intended shape
owned by their named phases.

## Where to start

For any task, read in this order:

1. [README.md](README.md) — direction at a glance.
2. [docs/architecture/01-platform-vision.md](docs/architecture/01-platform-vision.md) — what we build and why.
3. [docs/architecture/05-mvp-scope.md](docs/architecture/05-mvp-scope.md) — what is in / out / deferred.
4. [docs/roadmap/README.md](docs/roadmap/README.md) — phased plan with explicit dependencies and the one-way-door sequencing principle.
5. [docs/standards/00-principles.md](docs/standards/00-principles.md) — the beliefs every other standard descends from.
6. [docs/glossary.md](docs/glossary.md) — terminology; the single source of truth for project-specific terms.

Then read the phase that is live and the one it stands on:

- [docs/roadmap/phase-02b-events-auth.md](docs/roadmap/phase-02b-events-auth.md) —
  **the live phase**. Its Status block owns packet state; its decision register owns
  gate state and links each Accepted decision package in the delivery log, which is
  normative — read it before writing the code its gate governs. Navigate a delivery
  log by the generated index at its top; never read the whole file.
- [docs/roadmap/phase-02d-walking-skeleton.md](docs/roadmap/phase-02d-walking-skeleton.md) —
  complete; the delivered public-site baseline. It sorts after `02b`/`02c` but runs
  **before** them: the roadmap dependency map, not filename order, is authoritative.
- [docs/roadmap/phase-02a-kernel-tenancy.md](docs/roadmap/phase-02a-kernel-tenancy.md) —
  complete; the authority on what the kernel already guarantees, not on what to
  build next.

Once the high-level reading is done, pick **exactly one** skill entry point based
on the user's intent. The entry point dispatches the rest internally; do not
chain entry points yourself.

| Intent | Entry point |
|--------|-------------|
| "Implement / geliştir / yap / ekle / refactor X" (substantive work) | [implement-task](.claude/skills/implement-task/SKILL.md) — the **default**. Dispatches `start-task` in Step 1, then the workflow-specific `add-*` skill(s), then runs linter + tests, updates docs, commits, and emits a review-agent prompt. |
| "Plan / scope / orient / araştır / kapsamı çıkar" (no implementation yet) | [start-task](.claude/skills/start-task/SKILL.md) — standalone scoping pass. Stops at the plan. |
| "Review this diff / PR" | [standards-check](.claude/skills/standards-check/SKILL.md) first (5-min mechanical gate), then [code-review](.claude/skills/code-review/SKILL.md) (security + bugs + optimisation + refactor + LearnStack-specific lenses). |
| "Explain / what is X" (informational) | No skill — answer directly. |
| One-line typo / comment fix | No skill — edit directly. |

The full [skills catalogue](.claude/skills/README.md) lists every workflow skill
(`add-tenant-owned-entity`, `wire-dapr-pubsub`, `add-tenant-scoring-rule`, …) the
entry points dispatch to. You almost never invoke a workflow skill directly —
let the entry point pick it.

## Documentation layout

| Directory | Purpose | Mutability |
|-----------|---------|------------|
| `docs/architecture/` | Conceptual descriptions of what we are building. Numbered `NN-topic.md` linearly. | Editable as the system evolves. |
| `docs/decisions/` | ADRs — one-time decisions with status, context, decision, consequences. Redirect / superseded ADRs live under `_redirects/`. | Accepted ADRs are immutable except for dated Amendments and the two bounded corrections in [Documentation Standards § Correcting and Amending ADRs](docs/standards/13-documentation.md) ([ADR-0041](docs/decisions/0041-correcting-false-statements-in-accepted-adrs.md)). |
| `docs/standards/` | Engineering rules (`NN-topic.md`, 00 – 21). Each anchored standard carries a `**Derives from:** ADR-NNNN` header. | Editable as the team learns; standard changes cite an ADR. |
| `docs/roadmap/` | Phased plan (`phase-NN-topic.md`, 00 – 12 with 02a/02b/02c/**02d**, 08a/08b/08c, and 09/09b splits). Every phase doc carries the same six sections — Goal, Scope, Deliverables, Completion Criteria, Risks, Phase Exit Decision — with three declared exceptions listed in [the roadmap index](docs/roadmap/README.md): Phase 09b and Phase 12 are pointer documents into the Hub repository, and Phase 01 predates the convention. A phase with packets keeps its dated records — decision packages, step records, reviews, closeouts — in a sibling `phase-NN-delivery-log.md` ([ADR-0069](docs/decisions/0069-phase-documents-and-delivery-logs.md)); Phase 02a keeps its legacy single-file shape. | Phase doc editable per phase, its Status block holding packet state only. The delivery log is append-only: a shipped packet's records are not rewritten, and an Accepted decision package there is normative. |
| `docs/modules/` | Per-module specifications (`<module>/README.md` + `permissions.md` + `audit.md`), one directory per module, created with the first spec — [Tenancy](docs/modules/tenancy/README.md), Phase 02a Packet 6, [Customization](docs/modules/customization/README.md), Packet 8, and [Audit](docs/modules/audit/README.md), Packet 9. The ten sections are fixed by [Documentation Standards](docs/standards/13-documentation.md). | Editable with the module. |
| `docs/glossary.md` | Terminology source of truth. | Editable; new term goes here first, then used. |

> `docs/analysis/` exists locally but is **gitignored** — it is a private scratchpad
> for exploratory research, prior-art studies, and redesign drafts. **Never reference
> paths under `docs/analysis/`** from committed files (Markdown, code comments, commit
> messages, PR descriptions). See [Documentation Standards § Local-Only Directories](docs/standards/13-documentation.md).

## Hard rules

- **English** is the documentation language ([ADR-0007](docs/decisions/0007-documentation-language-and-conventions.md)). The Turkish-facing UI of any tenant is separate.
- **Mermaid** for diagrams in fenced ` ```mermaid ` blocks. Diagrams must remain readable in text form (titles + bullet fallbacks) for renderers that don't support Mermaid.
- **Single source of truth.** Each piece of knowledge lives in exactly one place. The glossary holds terms. ADRs hold decisions. Standards hold ongoing rules. Architecture docs hold conceptual descriptions. Roadmap holds phases. Do not duplicate.
- **ADR numbers are sequential and never reused.** Superseded ADRs become redirect stubs under `decisions/_redirects/`. Adding a new ADR uses the next free number.
- **Standards changes cite an ADR.** A new standard rule or a change to an existing one is paired with an ADR when the rule is non-trivial.
- **Modular monolith with four cross-module mechanisms** ([ADR-0010](docs/decisions/0010-cross-module-communication.md)): application contract, intra-module domain event, integration event via outbox (dispatched through `IEventBus` — `InProcessEventBus` today, the Dapr/Kafka adapter on its trigger), read-model projection. No fifth.
- **Tenant + organization isolation is defense-in-depth from day one** ([ADR-0003 Amendment 1](docs/decisions/0003-tenant-isolation-defense-in-depth.md), [ADR-0017](docs/decisions/0017-tenant-organization-hierarchy.md)): tenant + organization context + EF query filters + PostgreSQL RLS + architecture tests.
- **One canonical RLS template, in one file.** The corrected policy shape — one `AND`-ed policy per table, `ENABLE` **and** `FORCE ROW LEVEL SECURITY`, an explicit `WITH CHECK`, and the four-role model (`learnstack_migration` owns, `learnstack_app` connects with `NOBYPASSRLS`, `learnstack_platform` and `learnstack_outbox_admin` hold audited bypasses) — is decided in [ADR-0003 Amendment 3](docs/decisions/0003-tenant-isolation-defense-in-depth.md) and written as SQL in exactly one document: [Database Standards](docs/standards/05-database.md). Every other document links there. The superseded template lived in four documents and was wrong in all four — two *permissive* policies, which PostgreSQL combines with `OR`, so every tenant-wide row was visible across tenants.
- **Self-hosted infrastructure preferred** for Keycloak (auth, with two realms — `learnstack` + `learnstack-hub`), LiveKit OSS (live classroom), SeaweedFS (object storage), Meilisearch (search), Kafka (pub/sub backend), Vault (secrets). See ADRs 0004, 0005 and 0038 (which superseded 0014). **What** LearnStack uses is settled; **when** each arrives is [ADR-0035](docs/decisions/0035-demand-gated-infrastructure.md)'s trigger table.
- **The core platform stays domain-generic.** Domain-specific shapes (CEFR levels, English placement-test scoring, kyu/dan ranks, yoga asana catalogs, …) live as **tenant customization data** ([ADR-0018](docs/decisions/0018-tenant-driven-customization-model.md)), never as code in any module. There is no `Verticals/` folder. ADR-0011 is superseded. The boundary of that claim is in [Platform Vision § Genericity boundary](docs/architecture/01-platform-vision.md).
- **Irreversible now, additive on demand — the one-way-door test** ([ADR-0035](docs/decisions/0035-demand-gated-infrastructure.md)): *if I add this six months from now, will I have to touch code that is already written?*
  - **Yes → ship it now.** Tenant + organization isolation, the corrected RLS policies, the `outbox_messages` table and its ownership, strongly-typed identifiers, the localization schema, MUST-class audit durability, module boundaries and their architecture tests. These touch every query, every migration, and every job payload.
  - **No → ship the port now, the adapter on a named trigger.** Dapr pub/sub, Kafka, Valkey-backed cache, Vault, APISIX, the Hub entitlement source, signed licence keys, custom-domain TLS automation, `audit_log` partitioning. Each has a port in `LearnStack.SharedKernel` wherever [ADR-0035 § The gated set](docs/decisions/0035-demand-gated-infrastructure.md#the-gated-set) names one, a working default implementation (`InProcessEventBus`, `InMemoryCacheService`, `ConfigurationSecretProvider`, `NullEntitlementProvider`), an owning phase, and a written trigger condition. A building block missing any of those, other than a port that table records as absent, is not demand-gated — it is missing.
- **Provider adapters everywhere.** Payments, auth, storage, search, live classroom, notifications, **event bus, cache, secrets, Hub contract, entitlement source, host resolver** — all sit behind interfaces. No SaaS lock-in in `Domain` or `Application`. See [20-infrastructure-stack.md](docs/standards/20-infrastructure-stack.md).
- **The Hub contract is governed by two invariants, not by a count** ([ADR-0034](docs/decisions/0034-hub-contract-surface-invariant.md)): (1) the Hub stores **no tenant content** — courses, lessons, learners, enrollments, sessions and media live only in LearnStack, and the Hub holds tenant *metadata* only; (2) **every LearnStack↔Hub crossing goes through a named adapter** — `IEntitlementProvider`, `IUsageReporter`, `IHubTenantSync`, and nothing else may hold a Hub client. Adding an endpoint still requires an ADR, because the surface is a cross-repository contract both repositories have to agree on.
- **One binary, five `DeploymentMode` values.** Selection happens at the composition
  root; module code never branches on the mode
  ([ADR-0020](docs/decisions/0020-triple-deployment-hybrid-license.md), enforced by
  `Modules_Do_Not_Reference_DeploymentMode`).
  [Deployment Models § Supported today versus prepared seam](docs/architecture/25-deployment-models.md#supported-today-versus-prepared-seam)
  owns the current foundation wiring, remaining adapters and production-readiness
  boundary; a selectable enum value is not a supported deployment.

## Conventions when editing docs

- **Short and declarative** — heading + bullets over essay paragraphs.
- **Present tense decisions** ("LearnStack uses ..."), not future tense ("LearnStack will use ...").
- **Cross-link liberally** — to glossary, related architecture docs, standards, ADRs. Use relative paths.
- **TODO comments** include a date and an owner: `// TODO(YYYY-MM-DD, @owner): refactor when X lands`.
- **Don't redefine glossary terms in other docs**; link to them.

## Conventions when editing code (future)

Once application code lands, the engineering standards under
`docs/standards/` are the authority for every PR. The most load-bearing
rules:

- C# / .NET 10, strongly-typed ids, records, MediatR pipeline, EF Core
  with per-module `DbContext` ([02](docs/standards/02-backend-coding.md),
  [05](docs/standards/05-database.md)).
- TypeScript strict + Next.js App Router; one frontend app under
  `frontend/apps/web` with route segments
  ([03](docs/standards/03-frontend-coding.md),
  [07](docs/standards/07-frontend-architecture.md)). The operator portal
  is a separate app, `frontend/apps/operator-portal`, in the
  `LearnStack-Hub` repository.
- REST + RFC 7807 Problem Details + cursor pagination + idempotency
  keys + ETag concurrency ([04](docs/standards/04-api-design.md)).
- OpenTelemetry + correlation id end to end ([10](docs/standards/10-observability.md)).
- WCAG 2.2 AA across all surfaces ([16](docs/standards/16-accessibility.md)).
- Audit-coverage matrix required per module ([18](docs/standards/18-audit-coverage.md)).
- Permission keys `{module}.{resource}.{action}` with closed action set + scope
  (Platform / Tenant / Organization) ([19](docs/standards/19-permissions.md)).
- Infrastructure-stack rules (foundation ports and their default
  implementations, the Hub contract surface, outbox + inbox, entitlement
  projection) in [20](docs/standards/20-infrastructure-stack.md).
- The architecture-test catalogue in
  [21](docs/standards/21-architecture-tests-catalogue.md) — canonical rule
  names live there; do not invent a second spelling.
- Zero-tolerance review blockers enumerated in [17](docs/standards/17-code-review.md).

## Commit conventions

- Conventional Commits style: `type(scope): subject`.
- Subject in imperative mood; ≤ 72 chars.
- For doc-only commits: `docs(scope): ...` where scope is one of
  `architecture`, `decisions`, `standards`, `roadmap`, or omitted for
  cross-cutting changes.

## Things to never do

- Edit an Accepted ADR's body, except by the two bounded mechanisms
  [ADR-0041](docs/decisions/0041-correcting-false-statements-in-accepted-adrs.md)
  defines: an **inline erratum** beside the false text, which is the default, or
  **in-place replacement** where the text is a canonical artifact for reuse — a
  template others are told to copy, a DDL or command meant to be applied. Both
  are for a statement that was false **when it entered the record**, both leave
  § Status / § Date / § Deciders and all rationale untouched, and both owe a
  dated Amendment **in every Accepted ADR the diff changes**. A statement that
  was true then and is stale now is history: amend it, or write a new ADR that
  supersedes the old one.
- Introduce a fifth cross-module communication mechanism.
- Add domain-specific code (CEFR, exam, English placement, kyu/dan,
  asana, code-challenge runner, …) to **any** module. Such shapes live
  as tenant customization data per
  [ADR-0018](docs/decisions/0018-tenant-driven-customization-model.md).
  There is no `Verticals/` folder; the architecture test
  `No_Source_Folder_Named_Verticals` enforces it.
- Add an endpoint to the Hub contract surface without an ADR. The count
  is not the rule — [ADR-0034](docs/decisions/0034-hub-contract-surface-invariant.md)'s
  two invariants are — but the surface is a cross-repository contract, so
  it changes by decision record, in both repositories, or not at all.
- Call Hub endpoints from anywhere except the dedicated
  `IEntitlementProvider` / `IUsageReporter` / `IHubTenantSync` adapters.
- Resolve a host by calling the Hub. `IHostToTenantResolver` reads
  `platform_host_to_tenant` and nothing else
  ([ADR-0034](docs/decisions/0034-hub-contract-surface-invariant.md)); an
  anonymous page load must never depend on a control plane being
  reachable.
- Carry TLS certificates or private keys in the entitlement payload. Cert
  material moves by secret-store replication and is referenced by path
  from `PUT /api/internal/tenants/{id}/host-mappings`, never by value
  through a payload LearnStack caches, logs, audits and mirrors.
- **Append a dated packet record to a phase document, or narrate packet status
  anywhere but its Status block.** Decision packages, step records, reviews,
  corrections and closeouts are entries in the phase's delivery log. This file and
  the repository README point at the phase document and link no delivery log, and
  the roadmap index's § Phases links none either
  ([ADR-0069](docs/decisions/0069-phase-documents-and-delivery-logs.md)).
  `scripts/roadmap-structure.py` fails the build otherwise, and
  `scripts/markdown-fragment-audit.py` fails it on any anchor a move leaves broken.
- Copy the RLS template into a second document. It lives only in
  [Database Standards](docs/standards/05-database.md); everywhere else
  links to it. The last duplication shipped a broken policy into four
  files at once.
- Run a tenant- or organization-isolation test as the table owner or as a
  `BYPASSRLS` role. Isolation tests connect as **`learnstack_app`** — a
  test that runs as `learnstack_migration`, `learnstack_platform` or
  `learnstack_outbox_admin` passes even when every policy is inert, and
  therefore proves nothing.
- Write a MUST-class audit row outside the business transaction. MUST-class
  audit is written on the **same transaction** as the state change it describes
  ([ADR-0033](docs/decisions/0033-audit-durability-model.md)) — `AuditLogBehavior`
  classifies and parks the intents, `TransactionBehavior` writes them immediately
  before `COMMIT` — so they commit with that change or not at all, and so they
  execute while `app.tenant_id` is set and RLS accepts it. "The same
  `SaveChanges` as the business write" was the earlier formulation and ADR-0033
  **withdraws** it: the guarantee is the transaction, which is what a reader of
  `audit_log` observes and which needs no cross-`DbContext` machinery. A tenant `AuditConfig` may
  narrow SHOULD/MAY coverage but never removes baseline MUST coverage. Exactly
  two failures reject the operation: an operation the catalogue does not
  classify at all, and a MUST-class row that cannot be written durably. A
  tenant-override **read** failure does not — it falls back to the in-process
  catalogue, which carries the same MUST floor, so nothing proceeds unaudited
  and a cache outage does not deny every request platform-wide. The second of
  those two is **narrowed for the standalone class** by
  [ADR-0033 Amendment 1](docs/decisions/0033-audit-durability-model.md): a
  standalone MUST-class write failure changes the response only when the
  operation would otherwise have **succeeded**. A row recording an operation
  already being refused — a `denied` authorisation outcome, a rejected tenant
  assertion — keeps its own 403 / 404 and logs at `Critical`; downgrading a
  refusal to a `503` an anonymous caller can provoke tells them more, not less.
  The in-transaction class is untouched.
- **Write one audit row per request when the request audits two resources.**
  Intents are plural ([ADR-0044](docs/decisions/0044-audit-write-path.md), and
  [ADR-0033 Amendment 2](docs/decisions/0033-audit-durability-model.md)): one per
  audited `(resource, operation)`, held as an ordered list, and only the
  **owning** unit-of-work frame writes them or reports the commit boundary — a
  joiner that reports `Committed` claims durability for a row nothing committed.
  `ProvisionTenantCommand` is the shipped case: two aggregates, one transaction,
  two MUST rows.
- **Write two instances of an operation's declared aggregate without naming which one
  the row is about.** A row describes one instance. A handler that writes two — a
  publication retires the incumbent and activates the successor — calls
  `IAuditSubject.Designate(aggregate)`, and the other instance travels in `changes`
  under its own instance-qualified pointer; undesignated, the composer refuses the pair
  and the request fails closed. An entity the aggregate contains through a navigation
  needs nothing: it is captured inside the aggregate's row
  ([ADR-0044 Amendment 6](docs/decisions/0044-audit-write-path.md)).
- **Declare an audit value type in the Audit module's `Domain`.**
  `LearnStack.SharedKernel.Audit` holds the value types beside the ports —
  `OperationType`, `OperationClass`, `AuditOutcome`, `AuditClassification`,
  `AuditIntentState` and `CapturedEntityChange`
  ([ADR-0044 Amendment 3](docs/decisions/0044-audit-write-path.md)) — because
  `AuditIntent` and `AuditEntryDraft` are SharedKernel records that name them
  and every module's `IAuditCatalogSource` names the first two. Declaring them
  in the module is a project cycle. `AuditEntry` consumes them; it does not
  declare them.
- **Announce `TenantId.PlatformSentinel` as a request's tenant.** Exactly one
  class of row carries it — a platform-scope operation with no resolvable
  tenant, written standalone — and Packet 9 puts the guard where the value
  enters: `SetProvisioningTenantContextAsync` refuses the sentinel exactly as it
  already refuses `Guid.Empty`, and `Tenant.Create` refuses it in the factory
  ([ADR-0044 Amendment 3](docs/decisions/0044-audit-write-path.md)). The
  `tenants` CHECK is the backstop, not the control — a constraint cannot stop a
  GUC from being announced.
- **Store a killswitch in `tenant_feature_flags`.** That table has a foreign key
  to `tenants` and the platform sentinel deliberately has no `tenants` row, so
  the write is refused — and a foreign key is a constraint no role and no
  `BYPASSRLS` moves. Killswitches live in the platform-scoped
  `platform_killswitches`
  ([ADR-0045](docs/decisions/0045-entitlement-and-feature-flag-socket.md)).
  Packet 9 ships that table, its policies, the overlay and its cache family —
  and **no writer**: every toggle runs inside `EnterPlatformAdminScope(reason)`,
  whose registered gate is `DenyAllPlatformAdminGate`, so nothing can enter that
  scope until the Platform-scope permission arrives, and
  [Phase 03](docs/roadmap/phase-03-identity-admin.md) owns the toggle command,
  its permission and its runbook
  ([ADR-0045 Amendment 1](docs/decisions/0045-entitlement-and-feature-flag-socket.md)).
- **Read `platform_entitlement_cache` from a module, `IFeatureFlags` included.**
  The only sanctioned reader *and* writer is an `IEntitlementProvider`
  implementation; `IFeatureFlags` composes over the port, which is what makes
  swapping the registered provider change the answer.
- **Spell a limit key in LearnStack's own vocabulary.** The limit-key set is the
  Hub's, under the `limits.` prefix, because the Hub has merged code and two
  plan validators built on it and this side has a declaration and no
  implementing line
  ([ADR-0045 Amendment 1](docs/decisions/0045-entitlement-and-feature-flag-socket.md),
  [ADR-0021](docs/decisions/0021-feature-based-entitlement.md)). The earlier
  `tenancy.max_learners` / `classroom.minutes_per_month` / `media.storage_gb`
  spellings are withdrawn: a key the Hub never sends misses on every real
  projection and falls through to the catalog default, so a paid tenant reads as
  unentitled and the read reports success. The **feature**-key set is not
  changed with it.
- Inject `IConnectionMultiplexer` / `IDistributedCache` / `KafkaProducer` /
  `VaultClient` directly — use `IEventBus` / `ICacheService` /
  `ISecretProvider`.
- Read `DeploymentMode` from inside a module — the composition root
  branches once, modules never.
- Write `audit_log`, `platform_entitlement_cache`, or producer `outbox_messages`
  rows directly — use `IAuditStore`, `IEntitlementProvider.RefreshAsync`, `IOutbox`.
  Only ADR-0046's confined dispatcher writes outbox status through fenced
  parameterized SQL; its terminal UPDATE and narrow witness-bound audit INSERT
  share one transaction.
- Write `inbox_messages` or `event_deliveries` from a module or Seeder. ADR-0046's
  transport-owned acceptance, inbox/business owner and confined scheduler alone
  write their granted columns.
- Write rows through EF Core's set-based APIs — `ExecuteUpdate`, `ExecuteDelete`,
  `ExecuteSql*`. The audit capture sees only what the `ChangeTracker` holds
  ([ADR-0044 § 7](docs/decisions/0044-audit-write-path.md)), so such a write commits with
  no before, after or changes and nothing fails;
  `No_Set_Based_Write_Bypasses_The_Audit_Capture` enforces it.
- Accept `learnstack-hub` realm tokens on tenant-facing endpoints, or
  `learnstack` realm tokens on `/api/internal/*`.
- Reuse an ADR number.
- Add an architecture or standard document whose existence makes one of
  the existing documents ambiguous about ownership; if a topic needs
  more space, expand the existing doc rather than splintering.
- Mention a feature as "deferred to a later phase" without naming the
  phase that owns it. For an infrastructure building block the bar is
  higher: name the **port**, the **default implementation**, the **owning
  phase**, and the **trigger condition**
  ([ADR-0035](docs/decisions/0035-demand-gated-infrastructure.md)). Three
  out of four is not demand-gating.
- Throw `DomainException` for expected business-rule violations — use
  `Result.Fail(business_rule_violation, ...)`.
  `DomainException` is reserved for programmer errors / aggregate invariant
  bugs ([ADR-0032 § Sub-decision 4](docs/decisions/0032-exception-handling-logging-and-observability.md)).
  The Roslyn analyzer `LearnStackException-DomainExceptionThrow` flags
  violations; full catalogue entry in
  [docs/standards/21-architecture-tests-catalogue.md](docs/standards/21-architecture-tests-catalogue.md).
- Throw `FluentValidation.ValidationException` from `ValidationBehavior` —
  the behavior returns `Result.Fail(validation_failed)` and never throws.
- Reference `Sentry.SentrySdk` directly from any module assembly — error
  capture goes through `IErrorTrackingProvider`; the L1 `IExceptionHandler`
  is the only sanctioned caller in application code.
- Add an `ExceptionHandlingBehavior` to the MediatR pipeline —
  `AuditLogBehavior` (catches handler exceptions, audits, rethrows via
  `ExceptionDispatchInfo`) plus the L1 `IExceptionHandler` cover every
  exception path.
- Register the OpenTelemetry `LoggerProvider`
  (`AddOpenTelemetry().WithLogging()`) alongside Serilog. Logs flow through
  Serilog → OTLP sink only; double-export would duplicate every line.
- Import `Serilog.ILogger` from a module assembly — modules use
  `Microsoft.Extensions.Logging.ILogger<T>`; Serilog is the implementation
  wired once at the composition root.
- Import a provider SDK exception type outside the adapter's
  `LearnStack.Infrastructure.<Adapter>` namespace — adapters translate SDK
  exceptions into `ProviderException` subclasses at the boundary.
- Tag a span with `tenant.id` / `organization.id` / `user.id` /
  `correlation.id` from module code — the `TenantContextSpanProcessor`
  enriches every span centrally.

## Where to look when stuck

- Term means what? — [docs/glossary.md](docs/glossary.md).
- Why was this decided? — `docs/decisions/`. Each ADR carries context.
- What rule applies to my change? — `docs/standards/`. The index is in
  [docs/standards/README.md](docs/standards/README.md).
- What's next? — [docs/roadmap/README.md](docs/roadmap/README.md).
- What's the shape of the live classroom / auth / search / etc.? —
  the corresponding `docs/architecture/NN-topic.md`.
