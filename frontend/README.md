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
    sdk/                       # generated typed API client (placeholder)
    ui/                        # design-system primitives (placeholder)
```

There is **no `extensions/` folder** — ADR-0018 model is data, not code.

## Prerequisites

- Node ≥ 20.11 (see [.nvmrc](.nvmrc)).
- pnpm 9.x — bootstrap via `corepack enable && corepack prepare pnpm@9.12.3 --activate`.

## Common Commands

```bash
pnpm install                                  # install workspace deps
pnpm --filter @learnstack/web dev             # run app on http://localhost:3000
pnpm lint                                     # eslint across the workspace
pnpm typecheck                                # tsc --noEmit across the workspace
pnpm test                                     # vitest across the workspace
```
