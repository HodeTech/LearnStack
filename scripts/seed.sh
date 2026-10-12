#!/usr/bin/env bash
# LearnStack — local dev seed.
#
# Invoked by `make seed`. Idempotent: runs end-to-end every time, no
# destructive operations.
#
# Phase 01 scope (this file): verify the compose stack is healthy, confirm
# the two Keycloak realms are imported (`learnstack` + `learnstack-hub`),
# print a session summary with the demo credentials.
#
# Phase 02a Packet 7 scope (wired): provision the two demo tenants through
# `LearnStack.Tools.Seeder`, which sends the same commands a request sends —
# ProvisionTenantCommand, then CreateOrganizationCommand and
# MapHostToTenantCommand under each tenant's own announcement. Idempotent: a
# second run verifies contextual postconditions without overwriting tenant data.
#
# Phase 02a Packet 8 scope (wired): a third act per tenant installs the two
# built-in customizations — a `card` content type drawn by `default-card`, and
# a `plain` level taxonomy with three bands — through the same four commands a
# tenant admin uses, so a tenant that has authored nothing still has something
# for the runtime to resolve. Idempotent by the same mechanism.
#
# There is no platform-admin user to seed. This packet creates no `users`
# table — Phase 03's Identity migration owns it — and `UserId.SystemActor` is
# a CLR constant with no row behind it, deliberately, because the audit
# subsystem depends on an erased actor becoming an orphan surrogate.

set -eu -o pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# The combined lifecycle must never start one database and migrate/seed another
# through stale exported connection strings. Standalone `make migrate` retains
# its explicit connection override; `make seed` validates before any stack act.
if [[ $# -gt 1 || ($# -eq 1 && "$1" != "--validate-config") ]]; then
    echo "seed: unsupported arguments." >&2
    exit 2
fi
seed_env="${LEARNSTACK_COMPOSE_ENV_FILE:-.env}"
if [[ ! -f "$seed_env" ]]; then
    echo "seed: selected environment file is missing." >&2
    exit 1
fi
for _suffix in Migration Default; do
    _name="ConnectionStrings__${_suffix}"
    _selected=$(sed -n "s/^${_name}=//p" "$seed_env" \
        | tail -1 | tr -d "\r" | sed "s/^['\"]//; s/['\"]$//")
    _ambient="${!_name:-}"
    if [[ -z "$_selected" || (-n "$_ambient" && "$_ambient" != "$_selected") ]]; then
        echo "seed: connection configuration disagrees with the selected environment." >&2
        exit 1
    fi
done
if [[ "${1:-}" == "--validate-config" ]]; then exit 0; fi

# Readiness uses Node for bounded Compose and exact OIDC issuer checks.
for _dep in docker node dotnet; do
    if ! command -v "$_dep" >/dev/null 2>&1; then
        echo "seed: '$_dep' is required and was not found on PATH." >&2
        exit 1
    fi
done

# And `docker` on PATH is not `docker compose`: the V1 `docker-compose` binary satisfies the
# loop above and then every call below fails with "'compose' is not a docker command" — a
# message that names the subcommand rather than the missing plugin. Checked once, here.
if ! docker compose version >/dev/null 2>&1; then
    echo "seed: the Docker Compose V2 plugin is required ('docker compose'), and was not found." >&2
    echo "      The standalone 'docker-compose' binary is V1 and is not a substitute." >&2
    exit 1
fi

# One explicit selection is shared with make/demo; readiness has real command
# and HTTP deadlines, plus exact service and both-realm issuer checks.
node scripts/wait-compose.mjs

cyan()  { printf "\033[36m%s\033[0m\n" "$*"; }
green() { printf "\033[32m%s\033[0m\n" "$*"; }
red()   { printf "\033[31m%s\033[0m\n" "$*" >&2; }

# ─── Step 3: application-level tenant seeding ────────────────────────────
cyan "▶ Step 3/3: seeding the two demo tenants"

# The application role, not the migration role. The seeder writes through the
# same policies a request does, so a seed that succeeds is evidence the request
# path works — and a seed run as the owner would pass with every policy inert,
# which is the failure mode ADR-0003 Amendment 3 names by role.
#
# Read from the environment, then from `.env`. The Makefile does not export
# `.env` into the recipe's environment — `--env-file` feeds docker compose's own
# variable substitution and nothing else — so on the documented first-run path
# (`make install` → `make dev` → `make migrate` → `make seed`) the variable is
# unset and only this fallback finds it. `make migrate` learned the same lesson
# and this mirrors its reader, CR-strip and quote-strip included: .env.example
# single-quotes the values.
seed_cs="${ConnectionStrings__Default:-}"
if [[ -z "$seed_cs" && -f "$seed_env" ]]; then
    seed_cs=$(sed -n "s/^ConnectionStrings__Default=//p" "$seed_env" \
        | tail -1 | tr -d "\r" | sed "s/^['\"]//; s/['\"]$//")
fi

if [[ -z "$seed_cs" ]]; then
    red "seed: ConnectionStrings__Default is not set and .env does not carry it."
    red "  It arrives with the four-role model in Phase 02a Packet 6: copy the"
    red "  'four connection strings' block out of .env.example into your .env"
    red "  and re-run. A .env written before that packet has neither."
    exit 1
fi

seed_role=$(printf '%s' "$seed_cs" | awk -f scripts/connection-string.awk -v field=user)
seed_redacted=$(printf '%s' "$seed_cs" | awk -f scripts/connection-string.awk -v field=redacted)

if [[ "$seed_role" != "learnstack_app" ]]; then
    red "seed: ConnectionStrings__Default names Username='$seed_role', not learnstack_app:"
    red "  $seed_redacted"
    red "Seeding requires learnstack_app so runtime grants and RLS are exercised."
    red "A privileged bypass role makes isolation proof invalid; the migration owner"
    red "is still subject to FORCE RLS but is not the supported runtime credential."
    exit 1
fi

# Passed in the environment, not on argv: the value carries the database
# password, and an argument is visible to any local user through `ps`. The
# seeder reads this variable and takes no flag for it.
# Run from `backend/`, where the SDK pin lives (`backend/global.json`): from the
# repository root no global.json applies and whichever SDK is newest answers, which is
# not the one CI and `make migrate` use.
# The dispatcher belongs to the API host, even when exported by the caller.
if ! (cd backend && unset ConnectionStrings__OutboxDispatcher && ConnectionStrings__Default="$seed_cs" \
        dotnet run --project src/LearnStack.Tools.Seeder); then
    red "seed: tenant seeding failed."
    red "  Has the schema been applied? → make migrate"
    exit 1
fi

green "  ✓ demo-english and demo-yoga present."

# Accepted P02d-5/G32 keeps these SeedData hosts and manual local HTTPS setup.
# P02d-6 owns product pages; P02d-7/G45 owns make demo and the browser harness.
# README Quickstart owns the complete host/CA/leaf preparation instructions.
cat <<'HOSTS'

  Both tenants resolve by host. Manually add these entries to your hosts file:

    127.0.0.1  demo-english.learnstack.local
    127.0.0.1  demo-yoga.learnstack.local

  demo-english's host maps to the tenant as a whole; demo-yoga's maps to its
  default organization, so both live host classifications are exercised.

  Follow README Quickstart for manual mkcert trust and leaf certificates, then:
    make public-env
    make public-api    (terminal 1)
    make public-web    (terminal 2)

  Web readiness: https://localhost:3000/api/healthz
  make demo starts both production processes together; Ctrl+C preserves local data.
  These commands never edit hosts or install system trust.

HOSTS

cyan "▶ Demo identities ready"
cat <<'IDENTITIES'

  Keycloak admin console:  http://localhost:8080  (admin / admin-dev-secret)

  Realm: learnstack
    demo-admin@tenant-a.test      / demo-dev-secret   (tenant-admin)
    demo-learner@tenant-a.test    / demo-dev-secret   (tenant-learner)

  Realm: learnstack-hub
    demo-operator@learnstack.test / demo-dev-secret   (hub-operator; CONFIGURE_TOTP required-action)

IDENTITIES

green "✓ Seed complete."
