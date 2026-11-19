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
| `PlatformOperator:Issuer` | The deployment-controlled OIDC issuer, for example the `learnstack-hub` realm. HTTPS only; loopback HTTP in Development |
| `PlatformOperator:AcceptedAcr` | The `acr` values that show the second factor. The shipped realm emits `mfa` |
| `PlatformOperator:AuthorizedParties` | The allowed `azp` clients. The shipped realm's is `learnstack-operator` |
| `PlatformOperator:MaxAuthenticationAge` | How old `auth_time` may be, at most one hour; default 15 minutes |
| `PlatformOperator:Grants` | Each operator's `Subject` (the issuer's `sub`) and the Platform permission keys held |

Environment variables use double underscores, for example `PlatformOperator__Issuer`.

A Self-Hosted issuer other than the shipped realm must issue access tokens with:
audience `learnstack-platform`; `azp` in the allowed list; an `acr` in the accepted list
that is reached only after a second factor; an `auth_time` claim; and a lifetime of at
most 15 minutes. If it cannot, no operator can act, by design.

## Getting a token

The shipped realm provides the public client `learnstack-operator` (authorization code
with PKCE, redirect `http://127.0.0.1:8765/*`). The first login of the demo operator
asks it to set up an authenticator application.

1. Make a verifier and its challenge:
   `verifier=$(openssl rand -base64 48 | tr '+/' '-_' | tr -d '=\n')` and
   `challenge=$(printf %s "$verifier" | openssl dgst -sha256 -binary | openssl base64 -A | tr '+/' '-_' | tr -d '=')`.
2. Open
   `$ISSUER/protocol/openid-connect/auth?client_id=learnstack-operator&response_type=code&scope=openid&redirect_uri=http://127.0.0.1:8765/callback&code_challenge=$challenge&code_challenge_method=S256&state=x`
   and sign in with the password and the one-time code.
3. The browser lands on the callback address and shows a connection error. Copy `code`
   from its address bar.
4. Redeem it:
   `curl -s $ISSUER/protocol/openid-connect/token -d grant_type=authorization_code -d client_id=learnstack-operator -d redirect_uri=http://127.0.0.1:8765/callback -d code=$CODE -d code_verifier=$verifier`.
   Use `access_token` as the bearer token. It lasts at most 15 minutes.

## Making a call

Send `Authorization: Bearer $TOKEN`. Every write may carry an optional `ticket` of 1 to
64 characters of `A-Z a-z 0-9 . _ : / # -`. The response carries a `correlationId`;
record it against your own ticket, because the audit rows a tenant can read never hold
the ticket.

The operator contract is the document `/openapi/platform-v1.json`. It is served only
when an issuer is configured and is not part of the tenant contract or SDK.

## Giving and withdrawing a grant

Edit `PlatformOperator:Grants` in the deployment configuration: the operator's `sub`
from the issuer's console and the permission keys. Each entry is validated when the
process starts, and a key that is not registered stops the start. A configuration
provider that reloads (a file or a mounted secret) applies a change on the operator's
next act; one that cannot reload needs a restart. Withdrawal is the same edit, and it
takes effect on the next act, not at the token's expiry.

The demo operator's grant is accepted in Development only; the process refuses to start
in any other mode while it is present. Never import the shipped realm's demo user into a
production realm.

## Reading a refusal

| Answer | Meaning | What to do |
|---|---|---|
| 401, `Bearer` challenge | The token is missing, expired, for another audience or client, lacks an accepted `acr`, has no `auth_time`, was authenticated more than the allowed age ago, or lives longer than 15 minutes | Sign in again; check the issuer settings |
| 403 | The token is good and this operator holds no grant for the act | Ask for the grant |
| 400 | The ticket or the body is malformed | Fix the request |
| 404 | Unknown killswitch key, or the surface is not configured | Check the key and the issuer setting |
| 503 | The audit row could not be written, so nothing changed | Retry; the act is safe to repeat |

Signals: counters `learnstack.platform_operator.admissions`, `.refusals` and `.results`;
a Warning log for every refusal naming the operation and the operator's digest (event
7102 for a refused act, 7103 for a refused credential); and, for every admitted platform
act, the Warning log of the platform scope (event 7001). A refused operator leaves no
database row.

## Not recoverable here

A refusal leaves no record in the audit log, only the counter and the log line. An
operator cannot grant themselves anything: grants are configuration, edited by whoever
deploys it.
