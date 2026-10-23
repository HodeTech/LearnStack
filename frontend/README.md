# LearnStack Frontend Monorepo

pnpm workspaces under `frontend/`. Tenant-facing surface ships as a **single**
Next.js application per [ADR-0009](../docs/decisions/0009-frontend-single-app-first.md).
The operator portal (`learnstack-hub-web`) lives in the separate `learnstack-hub`
repository — not here.

## Layout

```
frontend/
  apps/
    web/                       # @learnstack/web (Next.js 15, App Router)
  packages/
    config/                    # eslint + tsconfig + tailwind presets
    sdk/                       # generated types + four injected public GET wrappers
    ui/                        # design-system primitives (placeholder)
```

There is **no `extensions/` folder** — ADR-0018 model is data, not code.

## Prerequisites

- Node 22.23.1 (see [.nvmrc](.nvmrc)).
- pnpm 9.x — bootstrap via `corepack enable && corepack prepare pnpm@9.12.3 --activate`.

## Common Commands

```bash
pnpm install --frozen-lockfile                # install workspace deps
pnpm --filter @learnstack/web dev             # native HTTPS ingress; prepare TLS first
pnpm lint                                     # eslint across the workspace
pnpm typecheck                                # tsc --noEmit across the workspace
pnpm test                                     # guarded Vitest workspace runner
```

Before starting web, follow the root [HTTPS Quickstart](../README.md#quickstart):
`make public-env` prepares the paired private configuration; hosts and mkcert trust
remain explicit developer steps. Start `make public-api` in another terminal.
The native ingress serves the configured tenant hosts over HTTPS on port 3000.
Optional tenant sign-in and its private opt-in are documented in the
[web setup](apps/web/README.md#optional-tenant-sign-in). Public-only operation is
the default; realm configuration alone does not activate a browser session.
Direct stock `next dev/start` is unsupported. P6 implements localized public
catalog/course/lesson pages; its [closeout](../docs/roadmap/phase-02d-delivery-log.md#p02d-6-step-4-product-proof-and-accessibility-closeout)
records completed reviews and passing manual accessibility. P6 is complete and
merged through PR #27 on 2026-10-10. P7 delivers the shared demo and narrow
Playwright library/Lighthouse proof; Phase 06 owns the broader Playwright Test/axe
suite. Automated scores retain P6's separately attributed manual evidence.

## Seeded browser and Lighthouse proof

From a fresh checkout without root `.env`, web `.env.local` or
`artifacts/public-demo`, run:

```bash
# from frontend/, after the locked dependency install above
pnpm exec playwright install --with-deps chromium
cd ..
make demo DEMO_MODE=ci
```

The shared lifecycle builds the production API/native renderer, seeds an isolated
tmpfs-backed Compose project, stops its Keycloak and runs nine linked product
pages plus 27 cold audits. Docker, .NET, Node, pnpm, OpenSSL and make are required;
fixed application/Compose ports must be free. It uses disposable private TLS and
certificate-specific browser trust without system hosts/trust changes. Review
`artifacts/public-demo/receipt.json`, all 27 JSON/HTML report pairs and browser
screenshots; proof and owned cleanup must both pass. Follow
[Running tests locally](../.claude/skills/run-tests-locally/SKILL.md#public-demo-managed-browser-and-lighthouse)
for the complete workflow. The packet's Step 4 record and
[branch protection](../.github/CONTRIBUTING.md#branch-protection-settings-on-main)
own real CI receipts and required-check activation; local success alone does not
establish activation. Human foreground `make demo` retains the manual HTTPS setup.
