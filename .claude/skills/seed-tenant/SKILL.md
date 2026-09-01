---
name: seed-tenant
description: >
  Provision a tenant for local development or the request-level isolation suite —
  the `tenants` row, its organizations, locales, settings, feature flags, domain,
  and its `platform_host_to_tenant` mapping. USE FOR: bringing up a demo tenant,
  adding a second tenant for cross-tenant isolation testing, reseeding after a
  tenancy schema change. DO NOT USE FOR: production tenant provisioning (operator
  action via Hub), Self-Hosted license issuance (Hub-side), customization data or
  course content (later phases own those aggregates — see § What a later phase
  adds), or domain-specific code (forbidden by ADR-0018 — everything is data).
---

# Seeding a tenant

## Purpose

Stand up a tenant + its organizations + its host mapping, all as **data**, so:

- Local dev has two hosts that resolve to two different tenants.
- The [Packet 7](../../../docs/roadmap/phase-02a-kernel-tenancy.md) request-level
  isolation suite has a deterministic two-tenant fixture.
- Two tenants in unrelated domains exist from Packet 7 onward and render side by
  side from [Phase 02d](../../../docs/roadmap/phase-02d-walking-skeleton.md), so
  genericity is proven **continuously**, by construction.

[Phase 10](../../../docs/roadmap/phase-10-english-learning-mvp.md) is **not** the
genericity proof and disclaims the attribution itself: it is the depth showcase —
the first place one tenant fills all eight customization aggregates at once.

## When to use

- Local-dev first-run seed.
- Adding a parallel tenant for the cross-tenant isolation tests.
- Reseeding after a schema change to the tenancy aggregates.
- Authoring a new "domain showcase" tenant (music school, dance studio).

## When not to use

- Production tenant create. That's an operator action from the Hub portal
  (`operator-portal`) via `POST /api/internal/tenants`.
- Self-Hosted license issuance. Hub-side (Phase 02c / 09b).
- Reseeding production data. Never.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Tenant id | Yes | Assigned by the registry that owns the tenant — the Hub in SaaS / Dedicated, configuration in Self-Hosted, the seeder here. `Tenant.Create` never mints one; its policy keys on `id`, so a factory-minted id could not satisfy its own `WITH CHECK`. |
| Tenant slug | Yes | URL-safe, ≤ 63 chars, unique across `tenants`: `demo-english`, `demo-yoga`. |
| Tenant name | Yes | Human-readable, ≤ 200 chars: "English Hero", "Anatolia Yoga". |
| Domain showcase | Yes | The "shape" the tenant demonstrates — drives the customization-data set, once the aggregates that hold it exist. |
| Organization slugs | Yes | Two per tenant; the first becomes `tenants.default_organization_id`. |
| Locale set | Yes | At least one, with **exactly one** `is_default`. |
| Host | Yes | One `platform_host_to_tenant` row per tenant, with or without `organization_id`. |

## Workflow

### Step 1: Pick the showcase

The two seeded showcases:

| Showcase | Slug | Display name | Host |
|----------|------|--------------|------|
| Online English school | `demo-english` | English Hero | `demo-english.learnstack.local` |
| Yoga studio | `demo-yoga` | Anatolia Yoga | `demo-yoga.learnstack.local` |

**A coding bootcamp is not a candidate.** Its defining feature — running a
learner's submitted code — is external capability invocation, which
[Platform Vision § Genericity boundary](../../../docs/architecture/01-platform-vision.md)
puts outside the customization model. Choosing it forces either a domain-specific
runner module or a showcase that omits the one thing that made the domain
interesting; [Phase 10](../../../docs/roadmap/phase-10-english-learning-mvp.md)
records the same rejection. A yoga studio's distinctive content and taxonomy are
pure shape, so it is honest about what the model can do.

### Step 2: Run the seed

```bash
make seed
```

The target brings the stack up and runs `scripts/seed.sh`, which verifies compose
health and the two Keycloak realms, then, from Packet 7, invokes the seeder:

```bash
dotnet run --project backend/src/LearnStack.Tools.Seeder -- \
    --tenants demo-english,demo-yoga \
    --platform-admin demo-admin@learnstack.test \
    --connection-string "$ConnectionStrings__Default"
```

`backend/src/LearnStack.Tools.Seeder` is the **reserved path** —
`scripts/seed.sh` names it in the placeholder section Packet 7 replaces. There is
no `make seed-tenant` and no `infra/seed/` tree.

The seed is **idempotent** — running it twice produces the same state.

### Step 3: What Packet 7's seed creates

**The provisioning transaction** — `BEGIN` → `SET LOCAL app.tenant_id` to the
assigned id → `INSERT tenants` → `INSERT organizations` → `UPDATE tenants SET
default_organization_id` → `COMMIT`:

- Row in `tenants` — id, slug, display_name, `status = Trial`. **Not `Active`**:
  `Tenant.Create` produces `Trial` and `ChangeStatus` is the only way out of it,
  so a seed that wants `Active` calls the transition rather than writing the
  column.
- One row in `organizations` — **the default one only** — and
  `AssignDefaultOrganization` pointing the tenant at it. Tenant + default
  organization in one transaction is the single bounded cross-aggregate write
  ([ADR-0042](../../../docs/decisions/0042-tenant-provisioning-cross-aggregate-transaction.md)),
  and it is bounded by enumeration — one operation, one allow-list entry. The
  seeder **invokes** `ProvisionTenantCommand` rather than writing the two roots
  itself, so the allow-list stays at one entry and the seed exercises the same
  path production does.

**The follow-on writes**, each its own command in its own transaction:

- The second row in `organizations`. It is a third aggregate root, and the
  exception covers the two named above and nothing else.
- Rows in `tenant_domains` and `tenant_settings`. Under Packet 7's promotion
  `TenantDomain` and `TenantSetting` are aggregate roots in their own right, so
  each is written the way any other root is.
- Rows in `tenant_locales` (exactly one `is_default`) and `tenant_feature_flags`.
  These are navigations inside `Tenant` rather than roots, and ADR-0042's
  enumeration names them among the rows it does **not** cover: neither carries an
  atomicity invariant against the tenant row.
- One row in `platform_host_to_tenant` — **per tenant, not per organization**. It
  is a projection rather than an aggregate, outside the rule entirely, and it does
  not share the provisioning transaction. `demo-english` leaves `organization_id`
  NULL (a `TenantHost`); `demo-yoga` sets it (an `OrgHost`), so both live
  classification classes from
  [ADR-0036](../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md) are
  exercised by the seed and not only by a fixture. Neither host belongs in
  `Tenancy:PlatformHosts`, which lists hosts that map to **no** tenant.

Two mechanics the seeder cannot skip:

- **`app.tenant_id` is set once per transaction, before that transaction's first
  insert.** `SET LOCAL` does not survive `COMMIT`, so every one of the writes
  above sets it again rather than inheriting it. Every table's `WITH CHECK` is
  live from the moment the migration finishes, and `tenants` keys its policy on
  `id`, so the provisioning transaction sets the session variable to the assigned
  id before the `INSERT`.
- **`platform_host_to_tenant` rows go in as `learnstack_app`.** Its policies are
  qualified `TO learnstack_app`, so the table owner is denied on it — the one
  table where the migration role cannot seed.

Packet 6's `SchemaFixture` keeps its own `alpha` / `beta` tenants. It asserts
against the applied schema and does not read this seed; changing one does not
change the other.

### Step 4: What a later phase adds

The seed above is the whole of the tenancy slice. Everything a "complete" demo
tenant eventually carries belongs to a phase that has not written its schema yet:

| Aggregate / artefact | Owning phase |
|---|---|
| `User`, `Membership`, roles, invitations | [Phase 03](../../../docs/roadmap/phase-03-identity-admin.md) |
| Keycloak OIDC wiring and the realm's `tenant_id` claim mapper | [Phase 02b](../../../docs/roadmap/phase-02b-events-auth.md) |
| `TenantContentType`, `TenantLevelTaxonomy` | [Phase 02a Packet 8](../../../docs/roadmap/phase-02a-kernel-tenancy.md) |
| `Course`, `Lesson` and their translation satellites | [Phase 02d](../../../docs/roadmap/phase-02d-walking-skeleton.md) |
| `TenantCustomFieldDef` | [Phase 03](../../../docs/roadmap/phase-03-identity-admin.md) |
| `TenantPageBlock` | [Phase 04](../../../docs/roadmap/phase-04-cms-media-pages.md) |
| `TenantLessonItemType`, `TenantScoringRule`, `TenantCompletionRule` | [Phase 05](../../../docs/roadmap/phase-05-education-learning-content.md) |
| Branding tokens and the surface that writes them | [Phase 06](../../../docs/roadmap/phase-06-renderer-admin-studio.md) |
| `TenantTemplateLibrary` | [Phase 08a](../../../docs/roadmap/phase-08a-assessment-notifications.md) |
| `InstructorAvailability`, `LiveSession`, `LiveBooking` | [Phase 08b](../../../docs/roadmap/phase-08b-scheduling.md) / [Phase 08c](../../../docs/roadmap/phase-08c-classroom.md) |
| Hub tenant mirror and the entitlement projection | [Phase 02c](../../../docs/roadmap/phase-02c-hub-foundation.md) / Packet 9 |

The entitlement projection is demand-gated infrastructure, so its row owes four
things and a phase is only one of them: the port is `IEntitlementProvider`, the
working default is `NullEntitlementProvider`, the owners are the Phase 02c /
Packet 9 pair above, and the trigger — *a tenant must be billed or plan-gated* —
is in
[ADR-0035](../../../docs/decisions/0035-demand-gated-infrastructure.md)'s trigger
table.

There is **no `tenant_branding` table** and no `tenant_branding` row to write.
Branding tokens are read from `TenantSetting`; the configuration surface that
writes them is Phase 06.

Keycloak users are **not** seeded by this skill. `infra/keycloak/realms/learnstack.json`
imports them at compose boot and `scripts/seed.sh` prints their credentials; there
is no `SEED_USER_PASSWORD` in `.env.example`, and adding one would put a second
source of truth beside the realm import.

### Step 5: Hosts file alias

To browse a tenant on a host that matches production-like custom domains:

```
# /etc/hosts
127.0.0.1   demo-english.learnstack.local
127.0.0.1   demo-yoga.learnstack.local
```

Then visit `http://demo-english.learnstack.local:3000`. The middleware resolves
the host through `IHostToTenantResolver`, which reads `platform_host_to_tenant`
and nothing else — never the Hub
([ADR-0034](../../../docs/decisions/0034-hub-contract-surface-invariant.md)).

### Step 6: Verify

Connect as `learnstack_app`, inside a transaction, with the tenant context set —
the same way the application does. Without it every tenant-owned table correctly
returns zero rows, which reads exactly like "the seed did not run".

`psql` takes its own arguments here. `$ConnectionStrings__Default` is a .NET
keyword string, which libpq rejects (`invalid connection option "Host"`), and
`.env` is read by compose rather than sourced into a shell, so the variable is
usually empty anyway. The password is the `learnstack_app` one in `.env`.

```bash
psql -h localhost -p 5432 -U learnstack_app -d learnstack <<'SQL'
BEGIN;
SELECT set_config('app.tenant_id', '<tenant-id>', true);
SELECT slug, display_name, status FROM tenants;
SELECT slug, display_name FROM organizations;
SELECT locale, is_default FROM tenant_locales;
COMMIT;
SQL

# platform_host_to_tenant is read before any tenant context exists, so its read
# policy admits exactly the host the resolver declares in `app.resolving_host`,
# or the caller's own tenant via `app.tenant_id`. With neither set,
# `learnstack_app` sees nothing — that is what stops an anonymous session
# enumerating the host map, not a failed seed. Check the second row under the
# other host, or a tenant's own row under `app.tenant_id`.
psql -h localhost -p 5432 -U learnstack_app -d learnstack <<'SQL'
BEGIN;
SELECT set_config('app.resolving_host', 'demo-english.learnstack.local', true);
SELECT host, organization_id, is_active, is_publicly_live FROM platform_host_to_tenant;
COMMIT;
SQL
```

### Step 7: Reset

There is no `make seed-reset`. The seed is idempotent, so re-running it is the
normal repair; a genuine reset drops the volumes and starts over:

```bash
make clean      # stops the stack and drops named volumes — destructive
make dev        # brings the stack back up
make migrate    # applies both migration chains — `make seed` does not
make seed       # reseeds
```

### Step 8: Authoring a new showcase

To add a third domain showcase (e.g. music school):

1. Add its tenant, organizations, locales, settings, feature flags, domain and
   host row to the seeder's data set.
2. Register the host in `/etc/hosts` and, from Phase 02d, expect it to render.
3. Run `make seed`.

Its customization data — content types, level taxonomy, blocks, rules, templates —
is added as each owning phase from § What a later phase adds lands the aggregate
that holds it.

**No LearnStack code change is required for the domain shape.** That is the
substrate-genericity claim per
[ADR-0018](../../../docs/decisions/0018-tenant-driven-customization-model.md); if
you find yourself touching a module to express a domain, the design is wrong. The
claim's edge is
[Platform Vision § Genericity boundary](../../../docs/architecture/01-platform-vision.md):
stateful entitlement and external capability invocation are platform features
gated by plan, not customization rows.

## Validation

- `make seed` exits 0, and exits 0 again on a second run.
- Both tenants are present with `status = Trial`, each with two organizations and
  a non-null `default_organization_id`.
- `platform_host_to_tenant` holds one row per tenant — one carrying
  `organization_id`, one leaving it NULL. Checked **one host at a time**, each under
  its own `app.resolving_host` (§ Step 6): the read policy admits the declared host or
  the caller's own tenant, so no single `learnstack_app` query can see both rows, and a
  count of two is not observable to the role this skill tells you to connect as.
- Both host rows carry `is_active` **and** `is_publicly_live` true. The resolver
  requires both terms, so a row that is only `is_active` is a host that 404s under
  a seed the checks above report as healthy.
- The Packet 7 request-level isolation suite is green **connected as
  `learnstack_app`**, against both seeded tenants.
- From Phase 02d, both hosts render their own catalog page in a browser.

## Common pitfalls

- **Domain-specific code in the seeder.** The seeder reads its data set; it does
  not contain `if (showcase == "english") ...` business logic. If you feel pulled
  toward that, the data is missing a field.
- **Seeding `status = Active` directly.** `Tenant.Create` produces `Trial`. Write
  the column and the aggregate's state diagram and the seed disagree from the
  first row.
- **Minting the tenant id in the seeder's factory.** `Tenant.Create` takes the id;
  the registry assigns it. A minted id has no `app.tenant_id` to match and the
  `WITH CHECK` refuses its own insert.
- **A host row per organization.** One row per tenant. An `OrgHost` is a tenant
  row that also carries `organization_id`, not a second row.
- **Seeding `platform_host_to_tenant` as the migration role.** Its policies are
  qualified `TO learnstack_app`; the owner is denied and the insert fails.
- **Two locales flagged `is_default`.** The invariant lives in the database as a
  partial unique index — `UNIQUE (tenant_id) WHERE is_default` — with an
  aggregate guard for the message. An aggregate check alone does not hold across
  concurrent transactions.
- **Non-idempotent seed.** Running twice should produce the same state. Reference
  rows by stable keys.
- **Two tenants sharing the same slug.** Slugs are unique across `tenants`; the
  seed will refuse. Note the consequence the aggregate documents: a duplicate-slug
  insert reveals that *some* tenant holds the slug, which is accepted only because
  slugs appear in hostnames and are public by construction.
- **`/etc/hosts` change for production.** Local-only. Production custom domains
  resolve through `platform_host_to_tenant` rows the Hub writes.
