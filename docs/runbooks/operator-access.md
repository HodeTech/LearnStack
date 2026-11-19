# Platform operator access

How a LearnStack or deployment operator authenticates to the platform surface, how a
grant is given and withdrawn, and how to read a refusal. Decided in
[ADR-0101](../decisions/0101-platform-operator-authority.md).

## Who this is for

Operators only: LearnStack staff, or the customer's staff in a Self-Hosted deployment.
An operator is not a tenant user and holds no tenant role. Nothing here is reachable
with a tenant token.

## What must be configured

The surface does not exist until an issuer is configured; with none, every
`/api/platform` path answers 404.

| Setting | Meaning |
|---|---|
| `PlatformOperator:Issuer` | The deployment-controlled OIDC issuer, for example the `learnstack-hub` realm. HTTPS only; loopback HTTP in Development. Never the tenant issuer |
| `PlatformOperator:AcceptedAcr` | The `acr` values that show the second factor. The shipped realm emits `mfa`. `0` and `1` are single-factor levels and are refused at start |
| `PlatformOperator:AuthorizedParties` | The allowed `azp` clients. The shipped realm's is `learnstack-operator` |
| `PlatformOperator:MaxAuthenticationAge` | How old `auth_time` may be, as a time span such as `00:15:00`; at most one hour, default 15 minutes |
| `PlatformOperator:Grants` | Each operator's `Subject` (the issuer's `sub`) and the Platform permission keys held |
| `ConnectionStrings:PlatformAdmin` | The `learnstack_platform` credential the killswitch act runs under. Required for that act: without it the first act fails with an error that names this key. The [onboarding](tenant-onboarding.md) acts run on the application role and do not use it |

List settings are indexed in environment variables, and the double underscore is the
separator:

```sh
PlatformOperator__Issuer=https://identity.example.com/realms/learnstack-hub
PlatformOperator__AcceptedAcr__0=mfa
PlatformOperator__AuthorizedParties__0=learnstack-operator
PlatformOperator__MaxAuthenticationAge=00:15:00
PlatformOperator__Grants__0__Subject=<the operator's sub>
PlatformOperator__Grants__0__Permissions__0=platform.killswitch.write
```

A Self-Hosted issuer other than the shipped realm must issue access tokens that meet the
profile below. If it cannot, no operator can act, by design.

## The token profile

A token is accepted only when every line holds:

- It is signed with RS256 by a key the issuer publishes, its header `typ` is `JWT`, and
  it has not expired (30 seconds of clock skew).
- `iss` equals `PlatformOperator:Issuer` exactly.
- `aud` contains `learnstack-platform`.
- `azp` is one of `PlatformOperator:AuthorizedParties`.
- `acr` is one of `PlatformOperator:AcceptedAcr`.
- `auth_time` is present, not in the future, and no older than
  `PlatformOperator:MaxAuthenticationAge`.
- `iat` and `exp` are present and `exp - iat` is at most 15 minutes.
- `sub` is present.

Roles and every other claim carry no authority. To see what a token claims, decode its
middle segment:

```sh
printf %s "$TOKEN" | cut -d. -f2 | tr '_-' '/+' \
  | awk '{ while (length($0) % 4) $0 = $0 "="; print }' | base64 -d | jq .
```

## Getting a token

You need `curl`, `openssl` and `jq`. In the local stack the API answers at
`http://localhost:5080`; set `API` to the address of the deployment you are calling.

The shipped realm provides the public client `learnstack-operator` (authorization code
with PKCE, redirect `http://127.0.0.1:8765/*`). In the compose stack the issuer is
`http://localhost:8080/realms/learnstack-hub`; the demo operator is
`demo-operator@learnstack.test` with the password `demo-dev-secret`, and its first login
asks it to set up an authenticator application (scan the code, or use the manual key
with any TOTP tool).

1. Make a verifier and its challenge:

   ```sh
   ISSUER=http://localhost:8080/realms/learnstack-hub
   verifier=$(openssl rand -base64 48 | tr '+/' '-_' | tr -d '=\n')
   challenge=$(printf %s "$verifier" | openssl dgst -sha256 -binary \
     | openssl base64 -A | tr '+/' '-_' | tr -d '=')
   ```

2. Open the address in a browser (`xdg-open` on Linux). `prompt=login` makes the issuer
   ask again even when a browser session exists; without it a stale `auth_time` is
   refused later. Sign in with the password and the one-time code:

   ```sh
   URL="$ISSUER/protocol/openid-connect/auth?client_id=learnstack-operator"
   URL="$URL&response_type=code&scope=openid&prompt=login&state=x"
   URL="$URL&redirect_uri=http://127.0.0.1:8765/callback"
   URL="$URL&code_challenge=$challenge&code_challenge_method=S256"
   open "$URL"
   ```
3. The browser lands on the callback address and shows a connection error. Copy `code`
   from its address bar and set `CODE` to it.
4. Redeem it. The access token lasts at most 15 minutes:

   ```sh
   TOKEN=$(curl -s "$ISSUER/protocol/openid-connect/token" \
     -d grant_type=authorization_code -d client_id=learnstack-operator \
     -d redirect_uri=http://127.0.0.1:8765/callback \
     -d code="$CODE" -d code_verifier="$verifier" | jq -r .access_token)
   ```

## Making a call

Send `Authorization: Bearer $TOKEN` and `Content-Type: application/json`. Every write
may carry an optional `ticket` of 1 to 64 characters of `A-Z a-z 0-9 . _ : / # -`. The
response carries a `correlationId`, the request's W3C trace identifier, which is also
the trace id in the logs; record it against your own ticket, because the audit rows a
tenant can read never hold the ticket.

```sh
API=http://localhost:5080
curl -sS -X PUT "$API/api/platform/v1/killswitches/killswitch.notifications.email" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"enabled": false, "ticket": "INC-123"}'
```

The operator contract is the document `/openapi/platform-v1.json`. It is served only
when an issuer is configured and is not part of the tenant contract or SDK.

## Giving and withdrawing a grant

Edit `PlatformOperator:Grants` in the deployment configuration: the operator's `sub`
from the issuer's console and the permission keys. Each entry is validated when the
process starts, and a key that is not registered stops the start. A configuration
provider that reloads (a file or a mounted secret) applies a change on the operator's
next act; one that cannot reload needs a restart. Withdrawal is the same edit, and it
takes effect on the next act, not at the token's expiry.

**Grant only after the operator's authenticator exists.** In the shipped realm an
account that has no one-time-password credential is asked to enrol one at its first
login, and that login then carries `mfa`. Until the operator has enrolled, anyone who
knows the password could enrol their own device. So the order is: the operator signs in
once and sets up the authenticator, you confirm it in the realm's console, and only then
is the grant added.

Password reset is switched off in the shipped realm, so a forgotten password or a lost
device is recovered by the realm's administrator, not by an emailed link that skips the
second factor.

The demo operator's grant is accepted in Development only; the process refuses to start
in any other mode while it is present. Never import the shipped realm's demo user into a
production realm.

## Reading a refusal

| Answer | Meaning | What to do |
|---|---|---|
| 401, `Bearer` challenge | The token is missing, malformed, expired, signed by another issuer, for another audience or client, lacks an accepted `acr`, has no `auth_time`, was authenticated too long ago, or lives longer than 15 minutes. It also covers an issuer that cannot be reached: at start, or for more than 15 minutes after its keys were last fetched | Decode the token and compare it with the profile; sign in again; check the issuer settings and the signals below |
| 403 | The token is good and this operator holds no grant for the act | Ask for the grant |
| 400 | The ticket or the body is malformed | Fix the request |
| 404 | Unknown killswitch key, or the surface is not configured | Check the key and the issuer setting |
| 415 | The body is not `application/json` | Send the header shown above |
| 503 | The audit row could not be written, so nothing changed. `Retry-After` says when to try again | Retry; the act is safe to repeat |
| 500 | Something unexpected failed. For a toggle, a commit whose outcome is unknown may have landed | Read the switch's state before repeating (see the [killswitch runbook](killswitch.md)) |

The answer to a refused credential is always the same; the reason is in the signals, not
the response.

## Signals

| Signal | Meaning |
|---|---|
| `learnstack_platform_operator_admissions_total{outcome,operation}` | Acts admitted or refused at the gate; `operation` is the audit slug, for example `tenancy.killswitch.toggle` |
| `learnstack_platform_operator_refusals_total{cause}` | Refusals, by cause: `request_aborted`, `metadata_stale`, `metadata_unavailable`, `token_missing`, `token_expired`, `token_lifetime`, `token_signature`, `token_issuer`, `token_audience`, `token_type`, `token_algorithm`, `token_invalid`, `profile_issuer`, `profile_subject`, `profile_azp`, `profile_acr`, `profile_iat`, `profile_auth_time`, `profile_lifetime`, `profile_auth_age`, and at the gate `invalid_ticket`, `missing_permission`, `unknown_operation`, `invalid_witness` |
| `learnstack_platform_operator_results_total{operation}` | MUST result rows committed |
| `learnstack_platform_operator_audit_failures_total{operation}` | Acts refused because the MUST row was not durable (the 503) |
| `learnstack_platform_operator_metadata_failures_total` | The issuer's discovery document or key set could not be read, was refused, or named another origin. While it climbs, new keys are not learned; a fetch that failed is not retried for 30 seconds, and once the keys held are more than 15 minutes old with a later fetch failed, every operator is refused (`metadata_stale`) |

A log line accompanies each. Search by event id: 7102 (Warning) a refused act, 7103
(Warning) a refused credential with its cause, 7106 (Warning) a metadata failure, 7105
(Error) a refused audit write, and 7101 (Error) a grant configuration that failed
validation on reload; while 7101 appears every operator act is refused until the
configuration is valid. Every admitted platform act logs a Warning with event 7001 and
the text "Platform-admin scope entered" (other hosts reuse the number for unrelated
lines, so match the text too). A refused operator leaves no database row.

## Not recoverable here

A refusal leaves no record in the audit log, only the counter and the log line. An
operator cannot grant themselves anything: grants are configuration, edited by whoever
deploys it.
