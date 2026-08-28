# LearnStack — repo-root orchestrator.
#
# Run `make help` for the target list. Every recipe runs from the repo root,
# so `${VAR:-default}` interpolation in `infra/compose/dev.yml` reads the
# repo-root `.env` (the developer's copy of `.env.example`).

# /bin/bash, not `/usr/bin/env bash`: GNU Make execs SHELL without splitting
# it on whitespace, so the two-word form makes every recipe die with
# `/usr/bin/env bash: No such file or directory` under .ONESHELL. macOS
# ships make 3.81, which ignores .ONESHELL entirely - which is why this
# worked here and failed on every Linux/WSL contributor's make 4.x.
SHELL := /bin/bash
.SHELLFLAGS := -eu -o pipefail -c
.DEFAULT_GOAL := help
.ONESHELL:

# Compose layering — dev.yml is always the base; e2e.yml overlays for the
# end-to-end test suite (Playwright + Testcontainers harness).
# Compose resolves its default env file from the PROJECT directory — the
# directory of the first `-f` file, i.e. `infra/compose/` — not from the cwd.
# Without `--env-file` the repo-root `.env` that `.env.example` documents is
# silently ignored and every `${VAR:-default}` falls back. `--env-file` on a
# missing path is a hard error, so the flag is conditional.
# Recursively expanded (`=`, not `:=`) AND $(shell), not $(wildcard): `.env` is
# a prerequisite that the rule below creates. `:=` would evaluate at parse time,
# before the file exists; $(wildcard) would not help either, because make caches
# its directory listing for the whole invocation and keeps answering "absent"
# even after the prerequisite wrote the file. Either way the first `make dev` on
# a fresh clone ran without the `.env` it had just written, and silently picked
# up any stray `infra/compose/.env` instead.
ENV_FILE      = $(shell test -f .env && echo --env-file .env)
COMPOSE_DEV   = docker compose $(ENV_FILE) -f infra/compose/dev.yml
COMPOSE_E2E   = docker compose $(ENV_FILE) -f infra/compose/dev.yml -f infra/compose/e2e.yml

# Kafka, Valkey, Vault, APISIX and the two Dapr services sit behind the `gated`
# compose profile per ADR-0035: their ports ship now, their adapters land in
# Phase 11, and nothing the backend runs today calls any of them. `make dev`
# therefore starts 7 services rather than 14.
#
# Every teardown and inspection target uses `--profile '*'`, and that is not
# tidiness. Measured: `docker compose down` without the profile LEAVES a running
# profiled container behind — `down -v` too, and `--remove-orphans` does not help,
# because a profiled service is not an orphan, merely unselected. Without this,
# a developer who ran the gated stack once and then `make clean` would keep a
# Kafka broker, a Vault and their volumes, while `make ps` said the stack was
# down.
GATED_PROFILE = gated
COMPOSE_ALL   = docker compose $(ENV_FILE) --profile '*' -f infra/compose/dev.yml
COMPOSE_E2E_ALL = docker compose $(ENV_FILE) --profile '*' -f infra/compose/dev.yml -f infra/compose/e2e.yml

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
.PHONY: dev dev-gated
dev: .env ## Bring the local dev stack up (Postgres, Keycloak, SeaweedFS, …).
	$(COMPOSE_DEV) up -d
	@printf "\n$(CYAN)Stack up.$(RESET) Tail logs with: make logs\n"
	@printf "Kafka, kafka-ui, Valkey, Vault, APISIX and Dapr are behind the '$(GATED_PROFILE)' profile — $(CYAN)make dev-gated$(RESET).\n"

.PHONY: dev-gated
dev-gated: .env ## Bring the dev stack up INCLUDING the demand-gated services (Kafka, kafka-ui, Valkey, Vault, APISIX, Dapr).
	COMPOSE_PROFILES=$(GATED_PROFILE) $(COMPOSE_DEV) up -d
	@printf "\n$(CYAN)Full stack up.$(RESET) Nothing the backend runs today calls these — see ADR-0035.\n"

.PHONY: down
down: ## Stop the dev stack, gated services included (preserves volumes).
	$(COMPOSE_ALL) down

.PHONY: clean
clean: ## Stop the dev stack AND drop named volumes (destructive — wipes data).
	$(COMPOSE_ALL) down -v

.PHONY: logs
logs: ## Tail compose logs (Ctrl+C to detach).
	$(COMPOSE_ALL) logs -f --tail=100

.PHONY: ps
ps: ## Show service health summary, gated services included.
	$(COMPOSE_ALL) ps

.PHONY: e2e-up
e2e-up: .env ## Bring the default dev services up with the e2e overlay (set COMPOSE_PROFILES=gated for all 14).
	$(COMPOSE_E2E) up -d
	@printf "\n$(CYAN)E2E stack up.$(RESET) Data is ephemeral — every restart wipes state.\n"

.PHONY: e2e-down
e2e-down: ## Stop the e2e overlay (tmpfs volumes evaporate automatically).
	$(COMPOSE_E2E_ALL) down

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

.PHONY: migrate
migrate: ## Apply every module's EF migrations as `learnstack_migration` (the ONLY sanctioned carrier of that credential).
	@# Standards 05 § Database roles: ConnectionStrings:Migration must never appear
	@# in API or worker runtime configuration. The role OWNS every table it creates,
	@# and a runtime that is the owner is precisely the arrangement FORCE ROW LEVEL
	@# SECURITY exists to defeat — every isolation test would then pass against
	@# policies that constrain nothing. This target is where the credential lives.
	@#
	@# `--connection` is passed explicitly rather than letting the startup project
	@# resolve one, because that would be ConnectionStrings:Default — the
	@# learnstack_app role, which holds USAGE but not CREATE on schema public and
	@# fails with `permission denied for schema public`. The tempting fix for that
	@# error (granting it CREATE) is the ownership mistake above.
	@set -a; test -f .env && . ./.env; set +a; \
	if [ -z "$${ConnectionStrings__Migration:-}" ]; then \
		echo "ConnectionStrings__Migration is not set."; \
		echo "It arrives with the four-role model in Phase 02a Packet 6: copy the"; \
		echo "'four database roles' and 'four connection strings' blocks out of"; \
		echo ".env.example into your .env and re-run. A .env written before that"; \
		echo "packet has neither."; \
		exit 1; \
	fi; \
	dotnet tool restore >/dev/null; \
	found=0; \
	for proj in backend/src/Modules/*/LearnStack.Modules.*.Infrastructure; do \
		test -d "$$proj/Persistence/Migrations" || continue; \
		found=1; \
		echo "==> $$(basename $$proj)"; \
		dotnet ef database update \
			--project "$$proj" \
			--startup-project backend/src/LearnStack.Api \
			--connection "$$ConnectionStrings__Migration"; \
	done; \
	if [ "$$found" = "0" ]; then \
		echo "No module carries Persistence/Migrations yet — the first lands with the Tenancy schema in Phase 02a Packet 6."; \
	fi

.PHONY: sdk
sdk: ## Regenerate @learnstack/sdk types from a running API's OpenAPI document.
	@# Needs the API up. `make dev` starts the compose stack, NOT the API — run
	@# `dotnet run --project backend/src/LearnStack.Api` in another shell first.
	@# Override the source with LEARNSTACK_OPENAPI=<url-or-path>.
	(cd frontend && pnpm --filter @learnstack/sdk generate)

# ─── Tests ────────────────────────────────────────────────────────────────
.PHONY: test
test: test-backend test-frontend ## Run all test suites (backend + frontend).

.PHONY: test-backend
test-backend: ## `dotnet test` (unit + architecture + contract + HTTP integration — same set as CI).
	(cd backend && dotnet test LearnStack.slnx --nologo)

.PHONY: test-integration
test-integration: ## Just the LearnStack.Tests.Integration assembly (a subset of test-backend).
	@# Today this assembly holds only WebApplicationFactory HTTP tests and needs
	@# no Docker, so `make test-backend` already covers it. The target stays as
	@# the fast inner loop while working on that assembly, and becomes the
	@# Docker-bound entry point in Packet 7 when Testcontainers tests land.
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
	@# One invocation from `frontend/`, not `pnpm -r exec`: that runs prettier once
	@# per package with the PACKAGE as its working directory, where
	@# frontend/.prettierignore is not found — measured, it reformatted the
	@# generated SDK schema every run.
	(cd frontend && pnpm exec prettier --write .)

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
	@printf "$(CYAN)git hooks → .githooks/ (pre-commit: dotnet format *.cs | prettier frontend/ | next lint frontend/apps/web | leakwatch if present)$(RESET)\n"

# ─── Env scaffolding ──────────────────────────────────────────────────────
# `.env` is gitignored; this rule copies `.env.example` on first run so the
# developer does not have to remember the step. `cp -n` (no-clobber) is
# portable across macOS + Linux and avoids overwriting an edited `.env`;
# `touch .env` afterward keeps the timestamp ahead of `.env.example` so the
# rule does not re-fire on every invocation after a rebase shifts mtimes.
.env: .env.example
	@# `cp -n` returns NON-ZERO on BSD/macOS when the target exists, so a bare
	@# `cp -n` aborts this rule for every developer whose `.env` predates the
	@# last `.env.example` edit — i.e. everyone, the first time they pull one.
	@[ -f .env ] || cp .env.example .env
	@touch .env
	@printf "$(CYAN).env ready (copied from .env.example if missing).$(RESET)\n"
