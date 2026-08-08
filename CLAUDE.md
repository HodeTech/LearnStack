# Working in this repository

This file is read first by Claude Code (and any other agent following the
convention). It tells you what this project is, what state it is in, and
the conventions you must follow when contributing.

## What this is

LearnStack is a **white-label platform for multi-branch education
businesses that teach live** — not a single LMS, and not an education
product of its own. One binary, one schema, and one set of container
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

LearnStack ships in three production deployment modes — SaaS, Dedicated,
Self-Hosted — backed by the companion **LearnStack Hub** control plane
(separate repository, see
[ADR-0019](docs/decisions/0019-learnstack-hub.md)). On developer
workstations the Hub repo is the sibling directory `../LearnStack-Hub`;
GitHub: https://github.com/cemililik/LearnStack-Hub. The Hub repository
**owns its own roadmap** at `../LearnStack-Hub/docs/roadmap/`; this
repository holds only LearnStack's side of the boundary, in
[Phase 02c](docs/roadmap/phase-02c-hub-foundation.md).

## What state this is in

**Phase 01 complete.
[Phase 02a](docs/roadmap/phase-02a-kernel-tenancy.md) in progress —
packets 0–3 shipped; packets 3b–10 were re-scoped on 2026-08-08 after a
four-report audit of the corpus.**

**Phase 01** shipped the .NET 10 solution scaffold under `backend/`
(core + 7 modules × 4 projects + 4 test projects including the
non-skippable `LearnStack.Tests.Architecture`), the `pnpm` frontend
monorepo under `frontend/` (`apps/web` Next.js App Router +
`packages/{config,ui,sdk}`), the local-dev compose stack at
`infra/compose/dev.yml`, and the DX + CI surround (repo-root `Makefile`,
`.env.example` single source of truth, `.githooks/pre-commit` formatter +
Leakwatch, `infra/compose/e2e.yml` ephemeral overlay,
`.github/workflows/ci.yml` with backend + frontend + meta + secret-scan
required checks, `scripts/seed.sh`).

**Phase 02a packets 0–3** shipped the decision set (Vogen, API
versioning, audit partition management), the shared kernel core
(`Result<T>` + `LocalizedMessage`, `Entity<TId>` / `AuditableEntity<TId>`,
domain events, cursor pagination, `IClock` / `IRandom` / `IGuidFactory`),
and the [ADR-0032](docs/decisions/0032-exception-handling-logging-and-observability.md)
cross-cutting foundation (L1 `IExceptionHandler`, the eight-step MediatR
pipeline, Serilog → OTLP, `TenantContextSpanProcessor`,
`IErrorTrackingProvider`, `IProviderResilience<TPort>`, the `LS0001`
analyzer). Those records are frozen delivery history.

**The 2026-08-08 restructure** re-scoped packets 3b–10 along three lines,
all recorded in the Phase 02a Status block:

- *Correctness moved earlier.* The Row Level Security template that four
  documents carried produced two **permissive** policies, which
  PostgreSQL combines with `OR` — leaking every tenant-wide row across
  tenants. [ADR-0003 Amendment 3](docs/decisions/0003-tenant-isolation-defense-in-depth.md)
  corrects it, and [ADR-0033](docs/decisions/0033-audit-durability-model.md)
  makes MUST-class audit a durable intent inside the business
  transaction — which is also what stops the corrected policy from
  rejecting every audit insert.
- *Additive infrastructure moved later.* Per
  [ADR-0035](docs/decisions/0035-demand-gated-infrastructure.md), Packet 5
  ships the foundation **ports and their default implementations**; the
  Dapr, Kafka, APISIX and Vault adapters land in
  [Phase 11](docs/roadmap/phase-11-production-hardening.md) against
  written triggers.
- *Proof moved earlier.* Two seed tenants in unrelated domains land in
  Packet 7, and [Phase 02d](docs/roadmap/phase-02d-walking-skeleton.md)
  renders both of them in a browser.

**[Phase 02d: Two-Tenant Walking Skeleton](docs/roadmap/phase-02d-walking-skeleton.md)
is the next user-visible milestone** — the first phase whose output
someone who does not read C# can evaluate: two hosts, two tenants, two
education sites, one binary and one database.

Every module assembly is still empty of domain code. Module-level
references in the docs (e.g. `LearnStack.Modules.Education.Application`,
`ILiveClassProvider`, `ITenantSearch`) describe **intended** shape that
the corpus anchors against; Phase 02a packets 6–9 and Phase 02d are
where the first of those types actually land.

## Where to start

For any task, read in this order:

1. [README.md](README.md) — direction at a glance.
2. [docs/architecture/01-platform-vision.md](docs/architecture/01-platform-vision.md) — what we build and why.
3. [docs/architecture/05-mvp-scope.md](docs/architecture/05-mvp-scope.md) — what is in / out / deferred.
4. [docs/roadmap/README.md](docs/roadmap/README.md) — phased plan with explicit dependencies and the one-way-door sequencing principle.
5. [docs/standards/00-principles.md](docs/standards/00-principles.md) — the beliefs every other standard descends from.
6. [docs/glossary.md](docs/glossary.md) — terminology; the single source of truth for project-specific terms.

Then read the two phases that are live:

- [docs/roadmap/phase-02a-kernel-tenancy.md](docs/roadmap/phase-02a-kernel-tenancy.md)
  — the current phase, with a dated Status block listing every packet.
- [docs/roadmap/phase-02d-walking-skeleton.md](docs/roadmap/phase-02d-walking-skeleton.md)
  — what Phase 02a is building toward. `02d` sorts after `02b`/`02c` but
  runs **before** them; the roadmap dependency map is authoritative for
  order, filename order is not.

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
| `docs/decisions/` | ADRs — one-time decisions with status, context, decision, consequences. Redirect / superseded ADRs live under `_redirects/`. | Accepted ADRs are immutable except for dated Amendments. |
| `docs/standards/` | Engineering rules (`NN-topic.md`, 00 – 21). Each anchored standard carries a `**Derives from:** ADR-NNNN` header. | Editable as the team learns; standard changes cite an ADR. |
| `docs/roadmap/` | Phased plan (`phase-NN-topic.md`, 00 – 12 with 02a/02b/02c/**02d**, 08a/08b/08c, and 09/09b splits). Every phase doc carries the same six sections: Goal, Scope, Deliverables, Completion Criteria, Risks, Phase Exit Decision. | Editable per phase; the Status block of a shipped packet is a dated delivery record and is not rewritten. |
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
- **Self-hosted infrastructure preferred** for Keycloak (auth, with two realms — `learnstack` + `learnstack-hub`), LiveKit OSS (live classroom), SeaweedFS (object storage), Meilisearch (search), Kafka (pub/sub backend), Vault (secrets). See ADRs 0004, 0005, 0014. **What** LearnStack uses is settled; **when** each arrives is [ADR-0035](docs/decisions/0035-demand-gated-infrastructure.md)'s trigger table.
- **The core platform stays domain-generic.** Domain-specific shapes (CEFR levels, English placement-test scoring, kyu/dan ranks, yoga asana catalogs, …) live as **tenant customization data** ([ADR-0018](docs/decisions/0018-tenant-driven-customization-model.md)), never as code in any module. There is no `Verticals/` folder. ADR-0011 is superseded. The boundary of that claim is in [Platform Vision § Genericity boundary](docs/architecture/01-platform-vision.md).
- **Irreversible now, additive on demand — the one-way-door test** ([ADR-0035](docs/decisions/0035-demand-gated-infrastructure.md)): *if I add this six months from now, will I have to touch code that is already written?*
  - **Yes → ship it now.** Tenant + organization isolation, the corrected RLS policies, the `outbox_messages` table and its ownership, strongly-typed identifiers, the localization schema, MUST-class audit durability, module boundaries and their architecture tests. These touch every query, every migration, and every job payload.
  - **No → ship the port now, the adapter on a named trigger.** Dapr pub/sub, Kafka, Valkey-backed cache, Vault, APISIX, the Hub entitlement source, signed licence keys, custom-domain TLS automation, `audit_log` partitioning. Each has a port in `LearnStack.SharedKernel`, a working default implementation (`InProcessEventBus`, `InMemoryCacheService`, `EnvironmentSecretProvider`, `NullEntitlementProvider`), an owning phase, and a written trigger condition. A building block missing any of those four is not demand-gated — it is missing.
- **Provider adapters everywhere.** Payments, auth, storage, search, live classroom, notifications, **event bus, cache, secrets, Hub contract, entitlement source, host resolver** — all sit behind interfaces. No SaaS lock-in in `Domain` or `Application`. See [20-infrastructure-stack.md](docs/standards/20-infrastructure-stack.md).
- **The Hub contract is governed by two invariants, not by a count** ([ADR-0034](docs/decisions/0034-hub-contract-surface-invariant.md)): (1) the Hub stores **no tenant content** — courses, lessons, learners, enrollments, sessions and media live only in LearnStack, and the Hub holds tenant *metadata* only; (2) **every LearnStack↔Hub crossing goes through a named adapter** — `IEntitlementProvider`, `IUsageReporter`, `IHubTenantSync`, and nothing else may hold a Hub client. Adding an endpoint still requires an ADR, because the surface is a cross-repository contract both repositories have to agree on.
- **One binary, five `DeploymentMode` values, two of them wired.** Selection happens at the composition root; module code never branches on the mode ([ADR-0020](docs/decisions/0020-triple-deployment-hybrid-license.md), enforced by `Modules_Do_Not_Reference_DeploymentMode`). `Development` and `SaaS` are wired end to end; `Dedicated`, `SelfHostedOnline` and `SelfHostedAirGapped` are **prepared seams, not supported deployments**, until [Phase 11](docs/roadmap/phase-11-production-hardening.md) builds their adapters and integration suites.

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
- Commits made with AI assistance carry the trailer
  `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.

## Things to never do

- Edit an Accepted ADR's decision section. Write a new ADR that
  supersedes the old one instead.
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
  audit is a **durable intent enrolled in the same `SaveChanges` as the
  state change it describes** ([ADR-0033](docs/decisions/0033-audit-durability-model.md)),
  so it commits with that change or not at all — and so it executes while
  `app.tenant_id` is set and RLS accepts it. A tenant `AuditConfig` may
  narrow SHOULD/MAY coverage but never removes baseline MUST coverage, and
  a config-store read failure **fails closed**.
- Inject `IConnectionMultiplexer` / `IDistributedCache` / `KafkaProducer` /
  `VaultClient` directly — use `IEventBus` / `ICacheService` /
  `ISecretProvider`.
- Read `DeploymentMode` from inside a module — the composition root
  branches once, modules never.
- Write `audit_log`, `platform_entitlement_cache`, or `outbox_messages`
  directly — use `IAuditStore`, `IEntitlementProvider.RefreshAsync`,
  `IOutbox`.
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
