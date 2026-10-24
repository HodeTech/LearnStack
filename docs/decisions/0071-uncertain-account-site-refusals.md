# ADR-0071: Uncertain AccountSite Refusals

## Status

Accepted — 2026-10-24, under maintainer authority to close verified review
findings and then accept and implement without another approval request.

**Date:** 2026-10-24
**Deciders:** @cemil

**Relationship:** Complete ADR-0070's unspecified ordinary AccountSite status
mapping and ratify the ordinary-read availability policy recorded in its
Amendment 1. That amendment names no HTTP status classification. ADR-0064's API
response contract and ADR-0070's grant-publication, refresh, capacity and lifetime
rules remain unchanged. Ordinary-read retention for availability failures is
already implemented; the 401 mapping awaits the dependent implementation.

## Decision Drivers

- ADR-0064 deliberately returns 401 for both invalid and undecidable
  credentials. A valid token whose issuer discovery fails receives that
  same response; 401 alone cannot prove that its identity was rejected.
- The BFF currently maps AccountSite 401, 403 and 404 to `identity`.
  An ordinary read then closes its live session, defeating the intended
  availability distinction when the API cannot reach its identity provider.
- Failed authority checks must withhold that response's authenticated content.
  Retaining credentials must not grant API access, extend idle time or replay
  an uncertain provider grant.
- A response-status table needs an explicit decision owner. The ordinary-read
  policy selected in ADR-0070 Amendment 1 must be stated as a decision rather
  than inferred solely from its clarification of the refresh boundary.

## Considered Options

1. **Retain on an ordinary AccountSite 401** (chosen). Classify it as
   `provider_refused`, deny the checked response and preserve the live owner
   without advancing idle time. This accepts delayed local revocation when
   401 really describes invalid credentials.
2. **Revoke on every 401** (rejected). Simple and conservative about custody,
   but also signs users out when issuer discovery is unavailable. Subsequent
   callback authority checks still fail, so immediate re-login cannot repair
   the dependency outage.
3. **Change API indeterminate authentication to 503 or add a refusal witness**
   (viable separate decision, not selected). This would distinguish the
   outcomes but changes ADR-0064's intentionally uniform contract. No
   response body, challenge text or library exception is a trusted witness
   under the current contract. A future API contract revision belongs to
   Phase 03's identity boundary work and requires its own explicit decision.
4. **Retry, refresh immediately or decode token claims to guess the cause**
   (rejected). An immediate automatic retry supplies no reliable new cause
   distinction; an extra refresh may consume a grant; local claims do not
   establish current API authority. A later user request remains permitted.
5. **Retain a newly refreshed package without positive AccountSite validation**
   (rejected). This would relax ADR-0070's publication rule for a new revision.
   Keeping an unvalidated replacement is a different custody/authority policy;
   this decision preserves the accepted fail-closed publication boundary.

## Decision

LearnStack treats AccountSite 401 as an uncertain authority refusal. The BFF
maps a valid API error response with this status to `provider_refused`.
During an ordinary read it retains the live session, withholds authenticated
content for that checked response and leaves its idle deadline unchanged.
Public documents render anonymously; protected checked reads return 503.
The request neither retries automatically nor starts an additional refresh.

The following decision table owns the distinction:

| AccountSite observation | Ordinary read | Initial callback | After refresh |
|---|---|---|---|
| Valid 401 API problem | Retain; unavailable; no idle touch | Refuse publication | Revoke |
| Valid 403/404 API problem | Revoke as `identity` | Refuse publication | Revoke |
| Any other valid API problem, including other 4xx and 5xx | Retain; unavailable; no idle touch | Refuse publication | Revoke |
| Transport, deadline or invalid response | Retain; unavailable; no idle touch | Refuse publication | Revoke |
| Successful response with actor/scope conflict | Revoke as `identity` | Refuse publication | Revoke |
| Successful matching response | Admit and apply the existing activity rule | Publish once | Publish one revision |

Missing or malformed API problem bodies cannot manufacture an identity refusal.
The existing SDK/adapter response and transport categories remain closed.
403/404 remain deliberate access-boundary refusals: authorization, tenant/host
scope and masked not-found outcomes do not grant this session access to the
fixed AccountSite surface. Their `identity` reason is a legacy closed category,
not proof that the user's identity itself is invalid. A masked 404 can also
describe a hop/host configuration failure; retaining revocation on that status
accepts that availability cost. This decision changes only the 401 ordinary-read
cell, not that existing conservative choice.

The usual expiry/near-expiry refresh rule still applies before a checked read;
this decision adds no refresh trigger after an uncertain ordinary read.

An initial callback needs positive API authority before session publication.
Every failed post-refresh AccountSite check still revokes: the newly obtained
package has no positive API authority witness and cannot publish a revision.
The completed refresh has already used its preceding grant; replaying that old
grant is not recovery. `invalid_grant`, provider uncertainty, epochs, late-result
fences and actual-cleanup ownership retain ADR-0070's rules.

The shared caller also classifies callback/extension AccountSite 401 as
`provider_refused`. Their BFF POST response consequently changes from
400/`identity` to 503/`provider_refused`, while publication still refuses and a
failed refresh still revokes. A 503 is not a promise that a session survived or
that a consumed grant can be retried. API response statuses are unchanged.

Passive projections continue to describe local session state. They do not
establish live API/provider acceptance, refresh credentials or touch idle time.
This decision adds no business permission or new passive-request authority.

## Context

The source at `c12f4eed` shows the complete chain:

- `createAccountSiteCaller` maps 401/403/404 to `identity`.
- `InMemoryBffSessionStore.admit(..., 'read')` closes only for `identity`.
- The actual API control
  `Discovery_failure_on_an_actual_public_route_is_401_and_never_anonymous`
  signs a normal token, returns discovery 503 and observes API 401. It passes
  independently during this decision pass.

The API deliberately makes these outcomes indistinguishable. No comparison of
the error message or bearer challenge can recover information it does not
provide. A retained BFF session does not make a rejected token usable at the API.
The discovery-failure control above uses the public pipeline; the same real
handler's protected AccountSite route is an additional required control. A warm
API can continue accepting tokens signed by a cached known key despite a
discovery outage. The uncertain 401 is relevant when validation cannot obtain
the needed configuration/key, rather than to every warm-instance outage.

### Relationship and acceptance vehicle

From ADR-0064's accepted API contract:

> On classified known hosts, a present invalid or undecidable credential is 401,
> including on public endpoints.

That contract is unchanged.

From ADR-0070 Amendment 1:

> A proven identity refusal or actor/scope conflict still
> revokes.

A bare 401 is not that proof. Amendment 1 does not enumerate statuses; this
record fills that decision gap and replaces the implementation's overly broad
401 classification. Acceptance appends a dated ADR-0070 amendment
linking this explicit classification and clarifying the earlier amendment's
decision status. Its original heading, body, metadata and rationale remain
historical. The ordinary-read availability choice recorded on 2026-10-24 is
ratified here as a policy decision; this does not claim that ADR-0070's original
Decision already specified every ordinary AccountSite status mapping. The
earlier amendment's grant-publication explanation remains valid.

This is an explicit decision completion and ratification, not an ADR-0041
factual correction or a claim that Amendment 1 contained a contradictory 401
rule. Acceptance reconciles Security 11 and Architecture 13 to reference this
table. They distinguish credentials held by the session owner from a successful
API access check.

Reopen this choice if the API adopts a trusted distinct indeterminate outcome,
if passive shells gain sensitive/business data, or if the provider/revocation
contract changes. Phase 03 owns identity disablement/back-channel logout;
Phase 11 owns production and distributed-session qualification.

## Consequences

### Positive

- An uncertain 401 does not destroy an otherwise live local session during an
  ordinary read that does not first require refresh.
- Failed checked responses remain unavailable; dependency recovery can admit
  a later matching read using the existing owner.
- Initial publication and uncertain-grant handling retain their stricter fences.

### Negative

- A genuine 401 also retains credentials until logout, a later definitive
  refusal, refresh failure or an existing local/provider deadline closes them.
  Immediate local revocation cannot be inferred from this API status.
- Passive projections can continue to show local session state during the
  uncertainty window. They are not a live identity acceptance statement.
- No availability guarantee follows: callbacks and post-refresh checks still
  fail closed, and repeated dependency failure can outlast the local session.
  Retention helps only until the existing refresh threshold; with a fresh
  900-second development access token and the 60-second refresh-ahead window,
  that window is at most approximately 14 minutes, not a new outage budget.
- Masked 403/404 still revoke, including host/hop configuration refusals. The
  shared `provider_refused` reason also groups API refusals with provider-endpoint
  refusals; it does not identify which dependency or cause failed.

### Neutral

- No API status, role, grant, endpoint, quota, cookie or session lifetime changes.
- No new retry, background poll, refresh trigger or audit-write path is added.

## Implementation Notes

- Commit acceptance before changing the dependent status mapping.
- Reconcile ADR-0070 through its own dated amendment, Security 11's BFF rules,
  Architecture 13, the decision index and the P02b-7 delivery record.
- Extend the existing `Bff_Credentials_And_Projections_Are_Confined`,
  `Bff_Session_Refresh_And_Revocation_Have_One_Owner` and
  `Bff_Auth_Admission_And_Csrf_Are_Closed` catalogue obligations and actual
  controls; do not invent a second rule spelling.
- Exercise real `createAccountSiteCaller` plus the real store with a held,
  valid 401 problem response. Prove retained ownership, unchanged idle expiry,
  unavailable checked projection and recovery on a later valid response.
- Independently exercise actual HTTP routes: public anonymous/protected 503,
  preserved cookies and no second grant. Exercise discovery failure on both
  actual public and protected API routes to prove the API half of the contract.
- Use valid 403/404/other-4xx/5xx problem bodies and transport/invalid-body controls
  to isolate the reason mapping. Assert the initial-callback and post-refresh 401
  negatives independently. The attempted callback must publish no new session;
  clear its owned credentials after actual settlement while preserving any
  prior live replacement session. A failed post-refresh check revokes its
  current owner. Assert callback/extension 503/`provider_refused` after a valid
  API 401, independently of the store's ownership result. Pending work stays
  rooted until actual settlement; no heap
  zeroization claim follows from logical entry/index cleanup.
- Do not label the composed controls as a fully browser-driven provider outage
  journey. Phase 06 owns the full real-browser OIDC/browser-matrix expansion.

## References

- [ADR-0064](0064-tenant-api-authentication-boundary.md).
- [ADR-0070 and Amendment 1](0070-owned-tenant-bff-sessions.md#amendment-1--delivered-proof-owners-and-read-failure-clarification-2026-10-24).
- [Security 11](../standards/11-security.md#authentication).
- [Architecture 13](../architecture/13-identity-and-auth.md).
- [Catalogue 21](../standards/21-architecture-tests-catalogue.md).
- [Phase 02b](../roadmap/phase-02b-events-auth.md).
- [Phase 03](../roadmap/phase-03-identity-admin.md), [Phase 06](../roadmap/phase-06-renderer-admin-studio.md), [Phase 11](../roadmap/phase-11-production-hardening.md).
- [AccountSite caller](../../frontend/apps/web/src/server/auth/account-client.ts).
- [Session owner](../../frontend/apps/web/src/server/auth/session-store.ts).
- [Session failure contract](../../frontend/apps/web/src/server/auth/session-contract.ts)
  and [provider failure taxonomy](../../frontend/apps/web/src/server/auth/provider-error.ts).
- [Actual API authentication controls](../../backend/tests/LearnStack.Tests.Integration/TenantAuthenticationHttpTests.cs).
