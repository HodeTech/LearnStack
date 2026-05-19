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
# Phase 02a scope (NOT YET WIRED): provision two application-level demo
# tenants + one platform-admin user via the `LearnStack.Tools.Seeder`
# console project against the real Tenancy module schema. The placeholder
# section at the bottom of this file lists the exact commands Phase 02a
# will swap the deferral notice for — leave it intact so the activation
# is a one-shot find-and-replace.

set -eu -o pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

COMPOSE_FILE="infra/compose/dev.yml"
KEYCLOAK_REALM_TENANT="learnstack"
KEYCLOAK_REALM_HUB="learnstack-hub"
KEYCLOAK_URL="http://localhost:8080"
HEALTH_TIMEOUT_SECONDS=180   # Keycloak first boot + realm import can take ~90s on a cold cache.

cyan()  { printf "\033[36m%s\033[0m\n" "$*"; }
green() { printf "\033[32m%s\033[0m\n" "$*"; }
red()   { printf "\033[31m%s\033[0m\n" "$*" >&2; }

# ─── Step 1: compose health ──────────────────────────────────────────────
cyan "▶ Step 1/3: verify compose services are healthy"

if ! docker compose -f "$COMPOSE_FILE" ps --status running --quiet >/dev/null 2>&1; then
    red "No compose services running. Run \`make dev\` first."
    exit 1
fi

unhealthy=$(docker compose -f "$COMPOSE_FILE" ps --format '{{.Name}}\t{{.Health}}' \
            | awk -F'\t' '$2 != "healthy" && $2 != "" {print $1 " (" $2 ")"}')
if [[ -n "$unhealthy" ]]; then
    red "Services not healthy yet — give them another minute, then re-run \`make seed\`:"
    while IFS= read -r line; do red "  - $line"; done <<<"$unhealthy"
    exit 1
fi
green "  ✓ All compose services healthy."

# ─── Step 2: Keycloak realm verification ─────────────────────────────────
cyan "▶ Step 2/3: verify Keycloak realms imported"

elapsed=0
while ! curl -sf "$KEYCLOAK_URL/realms/$KEYCLOAK_REALM_TENANT/.well-known/openid-configuration" >/dev/null 2>&1; do
    if (( elapsed >= HEALTH_TIMEOUT_SECONDS )); then
        red "Keycloak realm '$KEYCLOAK_REALM_TENANT' did not surface within ${HEALTH_TIMEOUT_SECONDS}s."
        red "Inspect with: docker compose -f $COMPOSE_FILE logs keycloak"
        exit 1
    fi
    sleep 3
    elapsed=$(( elapsed + 3 ))
done
green "  ✓ Realm '$KEYCLOAK_REALM_TENANT' OIDC discovery responds."

if ! curl -sf "$KEYCLOAK_URL/realms/$KEYCLOAK_REALM_HUB/.well-known/openid-configuration" >/dev/null 2>&1; then
    red "Realm '$KEYCLOAK_REALM_HUB' not reachable. Was the realm JSON imported?"
    red "  → infra/keycloak/realms/learnstack-hub.json"
    exit 1
fi
green "  ✓ Realm '$KEYCLOAK_REALM_HUB' OIDC discovery responds."

# ─── Step 3: Phase 02a deferral notice ───────────────────────────────────
cyan "▶ Step 3/3: application-level tenant seeding (deferred to Phase 02a)"

cat <<'NOTICE'

  The platform-level Tenant aggregate + Tenancy module DbContext do not
  exist yet (they ship in Phase 02a per docs/roadmap/phase-02a-kernel-tenancy.md).
  Phase 01 seeding therefore stops at:

    - Keycloak realms imported (done at compose boot, verified above)
    - Demo users present in each realm (seeded by the realm JSON files)

  Phase 02a swaps this section for:

    dotnet run --project backend/src/LearnStack.Tools.Seeder -- \
      --tenants demo-platform,demo-vertical                       \
      --platform-admin demo-admin@learnstack.test                 \
      --connection-string "$ConnectionStrings__Default"

  The console project does not exist yet; reserve the path now so the
  Phase 02a packet can drop the executable + edit this stub in one PR.

NOTICE

cyan "▶ Demo identities ready"
cat <<'IDENTITIES'

  Keycloak admin console:  http://localhost:8080  (admin / admin-dev-secret)

  Realm: learnstack
    demo-admin@tenant-a.test      / demo-dev-secret   (tenant-admin)
    demo-learner@tenant-a.test    / demo-dev-secret   (tenant-learner)

  Realm: learnstack-hub
    demo-operator@learnstack.test / demo-dev-secret   (hub-operator; CONFIGURE_TOTP required-action)

IDENTITIES

green "✓ Seed complete (Phase 01 scope)."
