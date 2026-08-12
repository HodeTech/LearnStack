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

# Compose resolves its default env file from the PROJECT directory — the
# directory of the first `-f` file, i.e. `infra/compose/` — not from the cwd.
# Without this the repo-root `.env` that `.env.example` documents is silently
# ignored and every `${VAR:-default}` falls back. `--env-file` on a missing
# path is a hard error, so the flag is conditional.
ENV_FILE_ARGS=()
[[ -f .env ]] && ENV_FILE_ARGS=(--env-file .env)
KEYCLOAK_REALM_TENANT="learnstack"
KEYCLOAK_REALM_HUB="learnstack-hub"
KEYCLOAK_URL="http://localhost:8080"
HEALTH_TIMEOUT_SECONDS=180   # Keycloak first boot + realm import can take ~90s on a cold cache.

cyan()  { printf "\033[36m%s\033[0m\n" "$*"; }
green() { printf "\033[32m%s\033[0m\n" "$*"; }
red()   { printf "\033[31m%s\033[0m\n" "$*" >&2; }

# ─── Step 1: compose health ──────────────────────────────────────────────
# `make seed` declares `: dev` as a prereq, so compose `up -d` has just
# returned and most services are in `starting` state. Poll until every
# service reports `healthy` or until the per-step timeout expires; only
# the "literally no services running" case is an immediate error (means
# the developer ran seed.sh directly without `make dev`).
cyan "▶ Step 1/3: wait for compose services to be healthy"

# Distinguish "nothing running at all" from "still starting".
running=$(docker compose "${ENV_FILE_ARGS[@]}" -f "$COMPOSE_FILE" ps --status running --quiet 2>/dev/null | wc -l | tr -d ' ')
if [[ "$running" == "0" ]]; then
    red "No compose services running. Run \`make dev\` first."
    exit 1
fi

elapsed=0
while true; do
    # Capture BOTH .State and .Health so we can distinguish:
    #   - service running + healthcheck reports `healthy`  → ok
    #   - service running + healthcheck still `starting`   → wait
    #   - service running + NO healthcheck defined         → skip
    #   - service not running (exited, dead, restarting)   → flag
    #
    # The empty-Health case is SKIPPED, not flagged. Two images in the stack
    # cannot carry a healthcheck at all — daprio/placement and daprio/daprd are
    # single-binary images on an empty base, with no shell and no wget/curl/nc
    # (`docker run --entrypoint sh` fails with "executable file not found").
    # Flagging them made this gate time out on every run, which is a gate nobody
    # can act on. Standards 12 § Healthchecks and the readiness gate names those
    # two as the only exempt images and requires the exemption to be marked at
    # the service in dev.yml, so the reason is readable where it applies rather
    # than duplicated into a service list here.
    #
    # `ps -a`, not `ps`: without it Compose omits `exited` and `created` rows
    # entirely, so the not-running branch below is unreachable for exactly the
    # states it exists to catch. Stopping two services and re-running printed
    # "All compose services running" and exited 0.
    not_healthy=$(docker compose "${ENV_FILE_ARGS[@]}" -f "$COMPOSE_FILE" \
                  ps -a --format '{{.Name}}\t{{.State}}\t{{.Health}}' \
                  | awk -F'\t' '
                      $2 != "running"            { print $1 " (state=" $2 ")"; next }
                      $3 == ""                   { next }
                      $3 != "healthy"            { print $1 " (health=" $3 ")"; next }
                    ')
    # A row that is absent is not a row that is healthy: compare the count we
    # actually saw against what the compose file declares, so a service that
    # never got created fails the gate instead of passing by omission.
    declared=$(docker compose "${ENV_FILE_ARGS[@]}" -f "$COMPOSE_FILE" config --services | wc -l | tr -d ' ')
    observed=$(docker compose "${ENV_FILE_ARGS[@]}" -f "$COMPOSE_FILE" ps -a --format '{{.Name}}' | wc -l | tr -d ' ')
    if [[ -z "$not_healthy" && "$observed" -eq "$declared" ]]; then
        break
    fi
    if [[ "$observed" -ne "$declared" ]]; then
        not_healthy="${not_healthy}"$'\n'"  (saw $observed of $declared declared services)"
    fi
    if (( elapsed >= HEALTH_TIMEOUT_SECONDS )); then
        red "Services still not healthy after ${HEALTH_TIMEOUT_SECONDS}s — inspect with:"
        red "  docker compose -f $COMPOSE_FILE ps"
        red "  docker compose -f $COMPOSE_FILE logs --tail=200"
        red "Still pending:"
        while IFS= read -r line; do red "  - $line"; done <<<"$not_healthy"
        exit 1
    fi
    sleep 3
    elapsed=$(( elapsed + 3 ))
done
green "  ✓ All compose services running; every healthcheck green."

# ─── Step 2: Keycloak realm verification ─────────────────────────────────
# Realm import happens during Keycloak's first boot — even after the
# `keycloak` container reports healthy, the OIDC discovery endpoint can
# take a few more seconds to surface each realm. Both realms get the same
# bounded retry loop.
cyan "▶ Step 2/3: verify Keycloak realms imported"

wait_for_realm() {
    local realm="$1"
    local elapsed=0
    while ! curl -sf "$KEYCLOAK_URL/realms/$realm/.well-known/openid-configuration" >/dev/null 2>&1; do
        if (( elapsed >= HEALTH_TIMEOUT_SECONDS )); then
            red "Keycloak realm '$realm' did not surface within ${HEALTH_TIMEOUT_SECONDS}s."
            red "  Was the realm JSON imported? → infra/keycloak/realms/${realm}.json"
            red "  Inspect with: docker compose -f $COMPOSE_FILE logs keycloak"
            return 1
        fi
        sleep 3
        elapsed=$(( elapsed + 3 ))
    done
    green "  ✓ Realm '$realm' OIDC discovery responds."
}

wait_for_realm "$KEYCLOAK_REALM_TENANT" || exit 1
wait_for_realm "$KEYCLOAK_REALM_HUB" || exit 1

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
