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
# second run recognises its own first by the uniqueness refusal and exits 0.
#
# There is no platform-admin user to seed. This packet creates no `users`
# table — Phase 03's Identity migration owns it — and `UserId.SystemActor` is
# a CLR constant with no row behind it, deliberately, because the audit
# subsystem depends on an erased actor becoming an orphan surrogate.

set -eu -o pipefail

# Hard dependencies. python3 is not obvious from the name of this script and
# is not listed in any prerequisites document, so check it here rather than
# letting `set -o pipefail` kill the run mid-gate with a raw shell error. It
# parses `docker compose config --format json` to derive the per-service
# healthcheck exemption; jq is not used because it is less commonly present
# on a fresh macOS box than python3.
for _dep in docker python3; do
    if ! command -v "$_dep" >/dev/null 2>&1; then
        echo "seed: '$_dep' is required and was not found on PATH." >&2
        exit 1
    fi
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

COMPOSE_FILE="infra/compose/dev.yml"

# Compose resolves its default env file from the PROJECT directory — the
# directory of the first `-f` file, i.e. `infra/compose/` — not from the cwd.
# Without this the repo-root `.env` that `.env.example` documents is silently
# ignored and every `${VAR:-default}` falls back. `--env-file` on a missing
# path is a hard error, so the flag is conditional.
# The `[@]+` guard is not decoration: macOS ships bash 3.2, where `set -u`
# treats an EMPTY array's `"${a[@]}"` as an unbound variable and aborts. Without
# it this script dies at its first docker call whenever `.env` is absent —
# before printing the "run make dev first" message written for exactly that case.
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
running=$(docker compose "${ENV_FILE_ARGS[@]+"${ENV_FILE_ARGS[@]}"}" -f "$COMPOSE_FILE" ps --status running --quiet 2>/dev/null | wc -l | tr -d ' ')
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
    #
    # The exemption is PER SERVICE, derived from the compose file — not "any row
    # whose Health is empty". A crash-looping service reports
    # `State=running, Health=""` for the instant after each restart attempt,
    # before Docker's health subsystem writes `starting`; a value-based skip
    # passes it. Forced with `chmod 000` on valkey's data dir, that read green on
    # two of three runs while `docker inspect` showed `restarting` with a
    # climbing RestartCount.
    exempt=$(docker compose "${ENV_FILE_ARGS[@]+"${ENV_FILE_ARGS[@]}"}" -f "$COMPOSE_FILE" \
             config --format json \
             | python3 -c 'import json,sys; print(" ".join(n for n,s in json.load(sys.stdin)["services"].items() if "healthcheck" not in s or s["healthcheck"].get("disable")))')

    not_healthy=$(docker compose "${ENV_FILE_ARGS[@]+"${ENV_FILE_ARGS[@]}"}" -f "$COMPOSE_FILE" \
                  ps -a --format '{{.Service}}\t{{.State}}\t{{.Health}}' \
                  | awk -F'\t' -v exempt=" $exempt " '
                      $2 != "running"                          { print $1 " (state=" $2 ")"; next }
                      index(exempt, " " $1 " ") > 0            { next }
                      $3 != "healthy"                          { print $1 " (health=" ($3 == "" ? "none reported" : $3) ")"; next }
                    ')
    # A row that is absent is not a row that is healthy: compare the count we
    # actually saw against what the compose file declares, so a service that
    # never got created fails the gate instead of passing by omission.
    declared=$(docker compose "${ENV_FILE_ARGS[@]+"${ENV_FILE_ARGS[@]}"}" -f "$COMPOSE_FILE" config --services | wc -l | tr -d ' ')
    observed=$(docker compose "${ENV_FILE_ARGS[@]+"${ENV_FILE_ARGS[@]}"}" -f "$COMPOSE_FILE" ps -a --format '{{.Service}}' | wc -l | tr -d ' ')
    # Compare the SETS, not just the counts. One service missing plus one
    # orphan present gives observed == declared, and a count-only test would
    # break out of the loop reporting success on a stack that is missing a
    # declared service.
    missing=$(comm -13 \
        <(docker compose "${ENV_FILE_ARGS[@]+"${ENV_FILE_ARGS[@]}"}" -f "$COMPOSE_FILE" ps -a --format '{{.Service}}' | sort -u) \
        <(docker compose "${ENV_FILE_ARGS[@]+"${ENV_FILE_ARGS[@]}"}" -f "$COMPOSE_FILE" config --services | sort -u) \
        | xargs)
    extra=$(comm -23 \
        <(docker compose "${ENV_FILE_ARGS[@]+"${ENV_FILE_ARGS[@]}"}" -f "$COMPOSE_FILE" ps -a --format '{{.Service}}' | sort -u) \
        <(docker compose "${ENV_FILE_ARGS[@]+"${ENV_FILE_ARGS[@]}"}" -f "$COMPOSE_FILE" config --services | sort -u) \
        | xargs)

    if [[ -z "$not_healthy" && -z "$missing" && -z "$extra" ]]; then
        break
    fi

    if [[ -n "$missing" || -n "$extra" ]]; then
        if [[ -n "$not_healthy" ]]; then
            not_healthy="${not_healthy}"$'\n'
        fi
        detail="saw $observed of $declared declared services"
        if [[ -n "$missing" ]]; then
            detail="$detail; never created: $missing"
        fi
        # An orphan makes the count exceed the declaration, so the message has to
        # point at --remove-orphans rather than at a service that failed to start.
        if [[ -n "$extra" ]]; then
            detail="$detail; orphaned (docker compose down --remove-orphans): $extra"
        fi
        not_healthy="${not_healthy}($detail)"
    fi
    if (( elapsed >= HEALTH_TIMEOUT_SECONDS )); then
        red "Services still not healthy after ${HEALTH_TIMEOUT_SECONDS}s — inspect with:"
        red "  docker compose ${ENV_FILE_ARGS[*]+${ENV_FILE_ARGS[*]} }-f $COMPOSE_FILE ps"
        red "  docker compose ${ENV_FILE_ARGS[*]+${ENV_FILE_ARGS[*]} }-f $COMPOSE_FILE logs --tail=200"
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

# ─── Step 3: application-level tenant seeding ────────────────────────────
cyan "▶ Step 3/3: seeding the two demo tenants"

# The application role, not the migration role. The seeder writes through the
# same policies a request does, so a seed that succeeds is evidence the request
# path works — and a seed run as the owner would pass with every policy inert.
SEED_CONNECTION="${ConnectionStrings__Default:-}"

if [[ -z "$SEED_CONNECTION" ]]; then
    red "seed: ConnectionStrings__Default is not set."
    red "  It lives in .env — copy .env.example to .env and re-run, or export it."
    exit 1
fi

# --nologo keeps the build banner out of a script whose output is read as a
# report; the exit code is what gates the step either way.
if ! dotnet run --project backend/src/LearnStack.Tools.Seeder --nologo -- \
        --connection-string "$SEED_CONNECTION"; then
    red "seed: tenant seeding failed."
    red "  Has the schema been applied? → make migrate"
    exit 1
fi

green "  ✓ demo-english and demo-yoga present."

cat <<'HOSTS'

  Both tenants resolve by host. Add them to /etc/hosts to reach either in a
  browser — Phase 02d is what renders them:

    127.0.0.1  demo-english.learnstack.local
    127.0.0.1  demo-yoga.learnstack.local

  demo-english's host maps to its default organization; demo-yoga's maps to
  the tenant as a whole, so both live host classifications are exercised.

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
