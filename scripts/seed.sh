#!/usr/bin/env bash
# LearnStack local seed: application-role data convergence followed by the shared
# development-realm reconciler. Existing provider credentials and unrelated data
# survive reruns; direct .NET Seeder remains an application-data tool.
set -eu -o pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"
if [[ $# -gt 1 || ($# -eq 1 && "$1" != "--validate-config") ]]; then
    echo "seed: unsupported arguments." >&2
    exit 2
fi
seed_env="${LEARNSTACK_COMPOSE_ENV_FILE:-.env}"
if [[ ! -f "$seed_env" ]]; then
    echo "seed: selected environment file is missing." >&2
    exit 1
fi
if ! command -v node >/dev/null 2>&1; then
    echo "seed: node is required and was not found on PATH." >&2
    exit 1
fi
# The selected private source must be complete before readiness, builds, migration
# or public-local preparation. Never copy a missing secret into an older file.
node scripts/seed.mjs --validate-config
if [[ "${1:-}" == "--validate-config" ]]; then exit 0; fi
for _dep in docker dotnet; do
    if ! command -v "$_dep" >/dev/null 2>&1; then
        echo "seed: required tooling was not found on PATH." >&2
        exit 1
    fi
done
# The Node owner confines stack/migration prerequisites, supplies only the app
# credential to the fresh Release DLL, and joins its commands on failure.
node scripts/seed.mjs

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

printf "\033[32m%s\033[0m\n" "✓ Seed complete."
