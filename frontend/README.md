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
pnpm install                                  # install workspace deps
pnpm --filter @learnstack/web dev             # native HTTPS ingress; prepare TLS first
pnpm lint                                     # eslint across the workspace
pnpm typecheck                                # tsc --noEmit across the workspace
pnpm test                                     # guarded Vitest workspace runner
```

Before starting web, follow the root [HTTPS Quickstart](../README.md#quickstart):
`make public-env` prepares the paired private configuration; hosts and mkcert trust
remain explicit developer steps. Start `make public-api` in another terminal.
The native ingress serves the configured tenant hosts over HTTPS on port 3000.
Direct stock `next dev/start` is unsupported. P6 implements localized public
catalog/course/lesson pages; its [closeout](../docs/roadmap/phase-02d-walking-skeleton.md#p02d-6-step-4-product-proof-and-accessibility-closeout)
tracks remaining review/manual accessibility work. P7 owns the browser/demo harness.
