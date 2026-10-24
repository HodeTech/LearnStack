# ADR-0071: Uncertain AccountSite Refusals

## Status

Proposed — maintainer approval pending.

**Date:** 2026-10-24 **Deciders:** @cemil

**Bounded supersession:** ADR-0070 Amendment 1's ordinary AccountSite
failure classification. ADR-0064's API response contract and ADR-0070's
grant-publication, refresh, capacity and lifetime rules remain unchanged.
No dependent runtime policy is implemented by this proposal.

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
   under the current contract.
4. **Retry, refresh immediately or decode token claims to guess the cause**
   (rejected). Retries add no evidence; an extra refresh may consume a grant;
   local claims do not establish current API authority.

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
| Valid 429/5xx API problem | Retain; unavailable; no idle touch | Refuse publication | Revoke |
| Transport, deadline or invalid response | Retain; unavailable; no idle touch | Refuse publication | Revoke |
| Successful response with actor/scope conflict | Revoke as `identity` | Refuse publication | Revoke |
| Successful matching response | Admit and apply the existing activity rule | Publish once | Publish one revision |

Missing or malformed API problem bodies cannot manufacture an identity refusal.
The existing SDK/adapter response and transport categories remain closed.
The usual expiry/near-expiry refresh rule still applies before a checked read;
this decision adds no refresh trigger after an uncertain ordinary read.

An initial callback needs positive API authority before session publication.
Every failed post-refresh AccountSite check still revokes: the preceding grant
may already have been consumed. `invalid_grant`, provider uncertainty, epochs,
late-result fences and actual-cleanup ownership retain ADR-0070's rules.

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

### Relationship and acceptance vehicle

From ADR-0064's accepted API contract:

> On classified known hosts, a present invalid or undecidable credential is 401,
> including on public endpoints.

That contract is unchanged.

From ADR-0070 Amendment 1:

> A proven identity refusal or actor/scope conflict still
> revokes.

A bare 401 is not that proof. Acceptance appends a dated ADR-0070 amendment
linking this explicit classification and clarifying the earlier amendment's
decision status. Its original heading, body, metadata and rationale remain
historical. The ordinary-read availability choice recorded on 2026-10-24 is
ratified here as a policy decision; this does not claim that ADR-0070's original
Decision already specified every ordinary AccountSite status mapping. The
earlier amendment's grant-publication explanation remains valid.

This is a bounded policy replacement, not an ADR-0041 factual correction.
Security 11 and the current architecture carry the explicit decision table by
reference. They must distinguish retained custody from accepted authority.

Reopen this choice if the API adopts a trusted distinct indeterminate outcome,
if passive shells gain sensitive/business data, or if the provider/revocation
contract changes. Phase 03 owns identity disablement/back-channel logout;
Phase 11 owns production and distributed-session qualification.

## Consequences

### Positive

- An issuer-discovery outage does not destroy an otherwise live local session.
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

### Neutral

- No API status, role, grant, endpoint, quota, cookie or session lifetime changes.
- No new retry, background poll, refresh trigger or audit-write path is added.

## Implementation Notes

- Accept and commit this record before changing the dependent status mapping.
- Reconcile ADR-0070 through its own dated amendment, Security 11's BFF rules,
  Architecture 13, the decision index and the P02b-7 delivery record.
- Extend the existing BFF confinement/session/CSRF catalogue obligations and
  their actual controls; do not invent a second rule spelling.
- Exercise real `createAccountSiteCaller` plus the real store with a held,
  valid 401 problem response. Prove retained ownership, unchanged idle expiry,
  unavailable checked projection and recovery on a later valid response.
- Independently exercise actual HTTP routes: public anonymous/protected 503,
  preserved cookies and no second grant. Keep the actual API discovery-failure
  control as the proof of the API half of the contract.
- Use valid 403/404/429/5xx problem bodies and transport/invalid-body controls to
  isolate the reason mapping. Assert the initial-callback and post-refresh 401
  negatives independently. The attempted callback must publish no new session;
  clear its owned credentials after actual settlement while preserving any
  prior live replacement session. A failed post-refresh check revokes its
  current owner. Pending work stays rooted until actual settlement; no heap
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
- [Actual API authentication controls](../../backend/tests/LearnStack.Tests.Integration/TenantAuthenticationHttpTests.cs).
