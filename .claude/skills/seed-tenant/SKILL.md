---
name: seed-tenant
description: >
  Provision a tenant for local development or integration tests, including its
  default organization, branding tokens, seed users (admin / instructor /
  learner), customization data (content types, page blocks, lesson item types,
  level taxonomy, scoring rules, completion rules, custom fields, templates), and
  a sample course / lessons. USE FOR: bringing up a new demo tenant, adding a
  second tenant for cross-tenant isolation testing, regenerating customization
  data after a schema change. DO NOT USE FOR: production tenant provisioning
  (operator action via Hub), Self-Hosted license issuance (Hub-side), or
  domain-specific code (forbidden by ADR-0018 — everything is data).
---

# Seeding a tenant

## Purpose

Stand up a new tenant + organization + memberships + customization data + a small
course tree, all as **data**, so:

- Local dev has something to render.
- Integration tests have a deterministic fixture.
- A second non-English tenant proves the substrate is generic per
  [Phase 10 exit criteria](../../../docs/roadmap/phase-10-english-learning-mvp.md).

## When to use

- Local-dev first-run seed.
- Adding a parallel tenant for the cross-tenant isolation tests.
- Reseeding after a schema change to the customization aggregates.
- Authoring a new "domain showcase" tenant (yoga, coding bootcamp, music school).

## When not to use

- Production tenant create. That's an operator action from the Hub portal
  (`learnstack-hub-web`) via `POST /api/internal/tenants`.
- Self-Hosted license issuance. Hub-side (Phase 02c / 09b).
- Reseeding production data. Never.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Tenant slug | Yes | URL-safe: `demo-english`, `demo-yoga`. |
| Tenant name | Yes | Human-readable: "English Hero", "Anatolia Yoga". |
| Domain showcase | Yes | The "shape" the tenant demonstrates — drives the customization-data set chosen. |
| Default org slug | Yes | One default org seeded per tenant: `main`, `studio-1`. |
| Locale set | Yes | At least one (e.g. `en-US`); typically two. |
| Hub-backed? | No | If yes, the local Hub stack must be up; tenant create routes through `POST /api/internal/tenants`. |

## Workflow

### Step 1: Pick the showcase

The two MVP showcases (per
[phase-10-english-learning-mvp.md](../../../docs/roadmap/phase-10-english-learning-mvp.md)):

| Showcase | Slug | Customization data set |
|----------|------|------------------------|
| English learning | `demo-english` | CEFR levels, vocabulary cards, speaking prompts, placement-to-CEFR scoring, lesson-package completion. |
| Coding bootcamp (or yoga) | `demo-coding` / `demo-yoga` | Track / difficulty taxonomy, code-challenge / asana content type, track-or-attendance completion. |

Both seed scripts live under `infra/seed/<showcase>/`.

### Step 2: Run the seed

```bash
make seed-tenant SHOWCASE=english SLUG=demo-english
```

The make target runs:

```bash
dotnet run --project infra/seed -- \
    --showcase english \
    --slug demo-english \
    --hub-backed=false        # true requires the local Hub
```

The seed is **idempotent** — running it twice produces the same state.

### Step 3: What gets created

In order:

#### 3.1 Tenant + organization

- Row in `tenants` (id, slug, display_name, status=Active).
- Row in `organizations` (default org with the chosen slug; every tenant has at
  least one).
- Row in `tenant_settings` (locale set, timezone, default-sender).
- Row in `tenant_branding` (theme tokens — primary, secondary, font family).
- Row in `tenant_domains` (subdomain on the platform's local default).

#### 3.2 Keycloak users

In the `learnstack` realm:

- 1 tenant admin (`tenantadmin@<slug>.local`).
- 1 instructor (`instructor1@<slug>.local`).
- 2 learners (`learner1@<slug>.local`, `learner2@<slug>.local`).
- Memberships in `(user_id, tenant_id, organization_id)` for each.

Passwords are seeded from a project-local secret in `.env`
(`SEED_USER_PASSWORD`), never committed.

#### 3.3 Customization data

All eight aggregates per
[32-tenant-customization-model.md](../../../docs/architecture/32-tenant-customization-model.md):

- `TenantContentType` — domain content types.
- `TenantPageBlock` — domain block keys pointing at built-in composites.
- `TenantLessonItemType` — domain lesson item types.
- `TenantLevelTaxonomy` — the level taxonomy (CEFR for English, Track for coding).
- `TenantScoringRule` — placement-test scoring DSL.
- `TenantCompletionRule` — lesson-package completion DSL.
- `TenantCustomFieldDef` — custom fields on built-in entities.
- `TenantTemplateLibrary` — locale-aware notification templates.

Each item is **versioned**; bumping a schema regenerates with `v+1` and keeps
old data valid.

#### 3.4 Education catalog

- 1 `Program`.
- 1 `Course` with 2 published `CourseVersion`s.
- 4 `Module`s with 3 `Lesson`s each, mixing built-in and tenant-defined lesson
  item types.
- 1 `Assessment` (placement test) using the tenant's `TenantScoringRule`.
- 1 `Cohort` with all 2 learners enrolled.

#### 3.5 Live classroom artefacts

- 1 `InstructorAvailability` window.
- 1 `LiveSession` scheduled in 7 days.
- 1 `LiveBooking` for one learner.

#### 3.6 Hub mirror (only when `--hub-backed=true`)

When the local Hub stack is up, the seed additionally:

- Creates the tenant on Hub via `POST /api/internal/tenants`.
- Receives `PUT /api/internal/tenants/{id}/entitlements` push with a default plan.
- `platform_entitlement_cache` is populated by the Dapr event consumer.
- `platform_host_to_tenant` gets the slug → tenant mapping.

When `--hub-backed=false`, `NullEntitlementProvider` covers entitlement (all
features enabled, no limits) and the host mapping is config-only.

### Step 4: Hosts file alias (optional)

To browse the tenant on a host that matches production-like custom domains:

```
# /etc/hosts
127.0.0.1   demo-english.learnstack.local
127.0.0.1   demo-yoga.learnstack.local
```

Then visit `http://demo-english.learnstack.local:3000`. The middleware resolves
the host through `IHostToTenantResolver`.

### Step 5: Verify

```bash
# DB sanity
psql $DATABASE_URL -c "SELECT id, slug, display_name FROM tenants;"

# Customization data
psql $DATABASE_URL -c "
    SELECT key, schema_version, created_at
    FROM tenant_content_types
    WHERE tenant_id = '<tenant-id>';"

# Keycloak users (admin endpoint requires admin token)
curl -fsS $KEYCLOAK_URL/admin/realms/learnstack/users \
    -H "Authorization: Bearer $KC_TOKEN" | jq '.[] | .username'

# Web app
open http://demo-english.learnstack.local:3000
```

### Step 6: Reset

```bash
make seed-reset                       # drops every demo tenant; re-runs seed
make seed-reset SHOWCASE=english      # only the English tenant
```

### Step 7: Authoring a new showcase

To add a third domain showcase (e.g. music school):

1. Create `infra/seed/music/` with:
   - `content-types/` JSON Schemas.
   - `page-blocks.json` mapping keys → composite renderer keys.
   - `lesson-item-types/` JSON Schemas.
   - `level-taxonomy.json` (difficulty bands or kyu/dan ranks).
   - `scoring-rule.dsl`.
   - `completion-rule.dsl`.
   - `custom-fields.json`.
   - `templates/` per-locale Liquid / Handlebars templates.
2. Add a `--showcase music` branch to the seed runner.
3. Run `make seed-tenant SHOWCASE=music SLUG=demo-music`.

**No LearnStack code change is required.** This is the substrate-genericity
proof per ADR-0018; if you find yourself touching a module, the design is
wrong.

## Validation

- `make seed-tenant` exits 0.
- The web app renders the tenant's landing page using the seeded customization
  data.
- The Studio editor surfaces every customization aggregate the seed populated.
- A learner login works; "My Courses" shows the seeded `CourseVersion`.
- A placement-test attempt scored via the tenant's `TenantScoringRule` returns
  the expected level.
- Cross-tenant test (`Tenant_A_cannot_read_Tenant_B`) passes after seeding two
  tenants.

## Common pitfalls

- **Domain-specific code in the seed runner.** The runner reads JSON / DSL files;
  it does not contain `if (showcase == "english") ...` business logic. If you
  feel pulled toward that, the data files are missing a field.
- **Non-idempotent seed.** Running twice should produce the same state. Use
  `INSERT ... ON CONFLICT DO NOTHING` and reference items by stable keys.
- **Seed password committed.** `SEED_USER_PASSWORD` lives in `.env`. Never check
  it in.
- **Hub-backed seed without Hub up.** The seed will fail; either run the
  `learnstack-hub` stack or pass `--hub-backed=false`.
- **Schema version bump without resync.** When a customization aggregate's schema
  changes (`v1` → `v2`), the seed creates the new version; existing tenants
  still need a migration of stored entries. The seed shouldn't bulk-migrate
  silently.
- **`/etc/hosts` change for production.** Local-only. Production custom-domains
  resolve via `platform_host_to_tenant` populated by Hub.
- **Two tenants sharing the same slug.** Slugs are unique on `tenants`; the seed
  will refuse.
