# LearnStack — repo-root orchestrator.
#
# Run `make help` for the target list. Every recipe runs from the repo root,
# so `${VAR:-default}` interpolation in `infra/compose/dev.yml` reads the
# repo-root `.env` (the developer's copy of `.env.example`).

SHELL := /usr/bin/env bash
.SHELLFLAGS := -eu -o pipefail -c
.DEFAULT_GOAL := help
.ONESHELL:

# Compose layering — dev.yml is always the base; e2e.yml overlays for the
# end-to-end test suite (Playwright + Testcontainers harness).
COMPOSE_DEV  := docker compose -f infra/compose/dev.yml
COMPOSE_E2E  := docker compose -f infra/compose/dev.yml -f infra/compose/e2e.yml

# Colour helpers (no-op when stdout is not a TTY).
ifeq ($(shell test -t 1 && echo 1),1)
  CYAN  := \033[36m
  RESET := \033[0m
else
  CYAN  :=
  RESET :=
endif

# ─── Help ─────────────────────────────────────────────────────────────────
.PHONY: help
help: ## Show this help, listing every target and its one-line description.
	@printf "LearnStack Makefile — common targets:\n\n"
	@awk 'BEGIN {FS = ":.*?## "} /^[a-zA-Z0-9_.-]+:.*?## / {printf "  $(CYAN)%-18s$(RESET) %s\n", $$1, $$2}' $(MAKEFILE_LIST)

# ─── Dev infrastructure ───────────────────────────────────────────────────
.PHONY: dev
dev: .env ## Bring the local dev stack up (Postgres, Valkey, Keycloak, …).
	$(COMPOSE_DEV) up -d
	@printf "\n$(CYAN)Stack up.$(RESET) Tail logs with: make logs\n"

.PHONY: down
down: ## Stop the dev stack (preserves volumes).
	$(COMPOSE_DEV) down

.PHONY: clean
clean: ## Stop the dev stack AND drop named volumes (destructive — wipes data).
	$(COMPOSE_DEV) down -v

.PHONY: logs
logs: ## Tail compose logs (Ctrl+C to detach).
	$(COMPOSE_DEV) logs -f --tail=100

.PHONY: ps
ps: ## Show service health summary.
	$(COMPOSE_DEV) ps

.PHONY: e2e-up
e2e-up: .env ## Bring the dev stack up with the e2e overlay (tmpfs volumes — ephemeral).
	$(COMPOSE_E2E) up -d
	@printf "\n$(CYAN)E2E stack up.$(RESET) Data is ephemeral — every restart wipes state.\n"

.PHONY: e2e-down
e2e-down: ## Stop the e2e overlay (tmpfs volumes evaporate automatically).
	$(COMPOSE_E2E) down

# ─── Build ────────────────────────────────────────────────────────────────
.PHONY: build
build: build-backend build-frontend ## Build backend + frontend.

# Multi-line recipes that `cd` into different subdirs MUST wrap each `cd` in
# a subshell (`(cd X && …)`), because `.ONESHELL:` keeps every line of the
# recipe in the SAME shell — without subshells the cwd of line 1 leaks into
# line 2 and the second `cd <relative-path>` blows up.

.PHONY: build-backend
build-backend: ## `dotnet build` the solution.
	(cd backend && dotnet build LearnStack.slnx --nologo)

.PHONY: build-frontend
build-frontend: ## `pnpm -r build` the frontend monorepo.
	(cd frontend && pnpm -r build)

# ─── Tests ────────────────────────────────────────────────────────────────
.PHONY: test
test: test-backend test-frontend ## Run all test suites (backend + frontend).

.PHONY: test-backend
test-backend: ## `dotnet test` (unit + architecture + contract; integration skipped — see test-integration).
	(cd backend && dotnet test LearnStack.slnx \
	  --filter "FullyQualifiedName!~LearnStack.Tests.Integration" \
	  --nologo)

.PHONY: test-integration
test-integration: ## Testcontainers-backed integration tests (requires Docker).
	(cd backend && dotnet test tests/LearnStack.Tests.Integration/LearnStack.Tests.Integration.csproj --nologo)

.PHONY: test-frontend
test-frontend: ## `pnpm -r test` (Vitest component + lib tests).
	(cd frontend && pnpm -r test)

# ─── Lint / format ────────────────────────────────────────────────────────
.PHONY: lint
lint: lint-backend lint-frontend ## Run linters (backend dotnet-format check + frontend ESLint).

.PHONY: lint-backend
lint-backend: ## `dotnet format` verify (no changes — fails on diff).
	(cd backend && dotnet format LearnStack.slnx --verify-no-changes --no-restore)

.PHONY: lint-frontend
lint-frontend: ## `pnpm -r lint` (Next/ESLint).
	(cd frontend && pnpm -r lint)

.PHONY: format
format: ## Apply formatters in place (backend dotnet-format + frontend prettier).
	(cd backend && dotnet format LearnStack.slnx --no-restore)
	(cd frontend && pnpm -r exec prettier --write .)

# ─── Typecheck (frontend) ─────────────────────────────────────────────────
.PHONY: typecheck
typecheck: ## `pnpm -r typecheck` (tsc --noEmit across the monorepo).
	(cd frontend && pnpm -r typecheck)

# ─── Seed ─────────────────────────────────────────────────────────────────
.PHONY: seed
seed: dev ## Bring the stack up and seed demo data (idempotent).
	./scripts/seed.sh

# ─── Bootstrap ────────────────────────────────────────────────────────────
.PHONY: install
install: .env hooks ## Restore backend NuGet + frontend pnpm deps + activate git hooks.
	(cd backend && dotnet restore LearnStack.slnx)
	(cd frontend && pnpm install --frozen-lockfile)

.PHONY: hooks
hooks: ## Activate the repo's pre-commit hook (.githooks/pre-commit).
	@git config core.hooksPath .githooks
	@printf "$(CYAN)git hooks → .githooks/ (pre-commit: dotnet format + prettier + eslint + leakwatch if available)$(RESET)\n"

# ─── Env scaffolding ──────────────────────────────────────────────────────
# `.env` is gitignored; this rule copies `.env.example` on first run so the
# developer does not have to remember the step. `cp -n` (no-clobber) is
# portable across macOS + Linux and avoids overwriting an edited `.env`;
# `touch .env` afterward keeps the timestamp ahead of `.env.example` so the
# rule does not re-fire on every invocation after a rebase shifts mtimes.
.env: .env.example
	@cp -n .env.example .env
	@touch .env
	@printf "$(CYAN).env ready (copied from .env.example if missing).$(RESET)\n"
