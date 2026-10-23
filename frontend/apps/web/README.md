# `@learnstack/web`

The single tenant-facing Next.js application per
[ADR-0009](../../../docs/decisions/0009-frontend-single-app-first.md).

The operator portal (`learnstack-hub-web`) lives in the separate `learnstack-hub`
repository — not under this `frontend/` directory.

## Route Groups

Route groups (`(public)`, `(studio)`, `(portal)`) organize files without
affecting URLs — each surface owns a distinct URL prefix so the three roots
don't collide at `/`:

| Route group                           | URL prefix                   | Purpose                                          | Phase that fills it in   |
| ------------------------------------- | ---------------------------- | ------------------------------------------------ | ------------------------ |
| `(public)/[locale]/courses/`          | `/{locale}/courses`          | Implemented catalog, course and lesson pages     | 02d; expanded by 04 / 06 |
| `(public)/[locale]/status/not-found/` | `/{locale}/status/not-found` | Localized fixed 404 recovery document            | 02d                      |
| `(studio)/studio/`                    | `/studio`                    | Admin + content studio                           | 04 / 06                  |
| `(portal)/portal/`                    | `/portal`                    | Learner + instructor portal                      | 07                       |
| `api/`                                | `/api/*`                     | Thin BFF route handlers (`/api/healthz` shipped) | 02a+                     |

## Optional tenant sign-in

[ADR-0070](../../../docs/decisions/0070-owned-tenant-bff-sessions.md) selects a
native openid-client session owner. Set `LEARNSTACK_BFF_ENABLED=true`, a canonical
`LEARNSTACK_BFF_ISSUER` in `/realms/learnstack`, and the existing confidential
`KEYCLOAK_WEB_CLIENT_SECRET`. Root `.env`, optional web `.env.local` and process
values must agree. Enabled mode requires regular, single-link, owner-private
source files (`chmod 600`); startup never repairs permissions or rotates secrets.
`make public-web` permits a loopback HTTP development issuer. Production web
requires HTTPS and normal certificate verification. Both modes discover the
provider lazily, without a startup network dependency.

The native process suppresses Next's automatic request/fetch logs and development
performance trace events before importing the framework. This covers public
requests too: raw callback/logout query values must not enter `.next/trace` or
console output. The separately invoked production build keeps its build traces;
API/business telemetry is unchanged. Requalify the pinned threshold seam and its
real development canary/counter-control whenever Next changes.

Absent/false opt-in keeps the anonymous renderer independent of Keycloak. Auth
paths then return 404; `/studio` and `/portal` return a localized unavailable
document with 503. Enabled mode exposes the closed `/api/auth` actions in native
middleware and requires a session for those two shells. The shells show the
read-only AccountSite projection, not future authoring or role permissions.

The browser receives only host-only opaque cookies and a safe session projection.
The warning uses the actual effective expiry; an explicit extension refreshes the
provider and reports an unchanged deadline honestly. Logout revokes locally first;
other institution tabs sharing provider SSO may need to sign in again. Ordinary
document anchors and a pre-navigation history veil require fresh admission when
returning to a protected document. `TenantBffSessionTests` owns the real warm
Keycloak/native BFF/application-role API journey and the separate actual HTTPS
browser cookie/history proof. The browser case uses fake grants/API, observes
natural persisted restores separately and kills a copied history-reload removal
with an explicit persisted-event control. It does not claim a full browser OIDC
journey, a universal browser cache or a new assistive-technology result.

The guarded frontend test entry compiles current native web and authenticated SDK
sources first. Docker execution uses the same explicit preparation, then builds
the copied current native and Next sources in its owned temporary directory.
Fixture IPC carries private configuration and responses only between owning test
processes; retained results contain closed outcomes, counts and timings. No
application test route, provider transport override or system trust installation
is added.

Restart signs users out. One native process owns the session store; deploying
multiple web instances requires the distributed adapter and qualification owned
by Phase 11. [Security 11](../../../docs/standards/11-security.md#tenant-bff-sessions)
owns limits, custody and ongoing rules.

P02d-4 delivers the API reads; P02d-5 delivers live bootstrap and membership-first
entry under [ADR-0053](../../../docs/decisions/0053-trusted-public-server-rendering.md).
`/` redirects through live default-locale admission; it is not a placeholder page.
Exact scaffold roots continue only after bootstrap; enabled locale membership takes
precedence. P02d-6 implements localized catalog/course/lesson pages, ordered
plain-text presentation, safe four-color themes and G40 page states. Its
[product closeout](../../../docs/roadmap/phase-02d-delivery-log.md#p02d-6-step-4-product-proof-and-accessibility-closeout)
records completed verification, reviews and scoped browser/VoiceOver observations.
P6 is complete and merged through PR #27 on 2026-10-10; its
[merge closeout](../../../docs/roadmap/phase-02d-delivery-log.md#p02d-6-merge-and-closeout-2026-10-10)
owns final verification. P7's shared demo/browser package was approved on
2026-10-11, together with ADR-0056. The shared local/CI lifecycle and narrow
Playwright library proof are delivered: nine seeded pages and 27 Lighthouse audits.
The [Step 3 record](../../../docs/roadmap/phase-02d-delivery-log.md#p02d-7-step-3--browser-and-audit-proof-2026-10-11)
owns local execution and reviews; the packet's Step 4 record and CONTRIBUTING own
real CI receipts and required-check activation. Phase 06 retains the broader
Playwright Test/axe suite.

There is **no `extensions/` folder for vertical-provided components** — per
[ADR-0018](../../../docs/decisions/0018-tenant-driven-customization-model.md),
tenant-specific renderers are composite renderer keys resolved by
[`src/lib/customization/`](src/lib/customization/) against a closed set.

## Customization Runtime (ADR-0018)

[`src/lib/customization/`](src/lib/customization/) holds the runtime resolver for
the closed primitive + composite renderer sets. A tenant's `TenantPageBlock`
row carries a `renderer_key` string — `resolveRendererKey()` is the only
sanctioned path from that string to a real component. Domain-flavoured keys
(`english.vocabulary-list`, `yoga.asana-card`) are forbidden; tenants compose
primitives or ask LearnStack to add a new composite.

## Local Run

```bash
# from the repository root, after the documented hosts/certificate preparation
make demo          # production API + native web; Ctrl+C joins owned processes
# Optional separate development/watch terminals: make public-api / make public-web
```

Follow the root [HTTPS Quickstart](../../../README.md#quickstart) for dependencies,
manual host/CA trust steps and leaf certificate paths. The native ingress binds
IPv4 loopback port 3000 and serves configured hosts over HTTPS. `/api/healthz` is
the exact web health path. The paired API origin comes from private local
configuration, never from the visitor's URL. Direct stock `next dev/start` does
not establish trusted provenance and is unsupported.

For the isolated browser/Lighthouse proof, use a fresh checkout without root
`.env`, web `.env.local` or `artifacts/public-demo`:

```bash
(cd frontend && pnpm install --frozen-lockfile)
(cd frontend && pnpm exec playwright install --with-deps chromium)
make demo DEMO_MODE=ci
```

Run these commands from the repository root with Docker and the documented
toolchain available and fixed application/Compose ports free. CI mode generates
private disposable TLS/configuration, uses certificate-specific browser trust and
stops its Keycloak before the product proof. Review the sanitized
`artifacts/public-demo/receipt.json`, 27 JSON/HTML report pairs and browser
screenshots; proof and owned cleanup must both pass. See
[Running tests locally](../../../.claude/skills/run-tests-locally/SKILL.md#public-demo-managed-browser-and-lighthouse)
for artifact freshness and cleanup rules. Local success alone does not establish
required-check activation or a new manual accessibility pass.
