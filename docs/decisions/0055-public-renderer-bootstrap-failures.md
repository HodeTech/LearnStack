# ADR-0055: Distinguish Temporary Public Renderer Bootstrap Failures

## Status

Proposed — 2026-10-10. Maintainer approval is pending.

**Date:** 2026-10-10
**Deciders:** @cemil (approval pending)

This proposal changes only P02d-6 G40's second, RSC bootstrap failure contract.
It accepts no gate and authorizes no dependent implementation. Existing Accepted
ADR decisions and the original G40 record remain unchanged until approval.

## Decision Drivers

- Middleware and RSC independently read current site configuration; no shared
  representation cache or middleware-to-RSC configuration carrier is permitted.
- An API outage, quota refusal or invalid response after middleware succeeds
  currently becomes a not-found outcome in the RSC admission loader.
- A temporary inability to admit a tenant document does not establish that the
  requested resource is missing. No Education read or tenant theme is safe then.
- App Router Server Components do not own arbitrary HTTP status/response headers.
  An exact 429/503 design would require a different response owner.

## Considered Options

1. **Distinguish temporary failure and use a sanitized pre-shell framework 500**
   (recommended). Preserve live admission and framework response ownership while
   avoiding a false 404. Require an actual production-wire proof before delivery.
2. **Keep every RSC bootstrap failure as 404**. Rejected because temporary
   unavailability is incorrectly reported as resource absence.
3. **Render a neutral HTTP 200/noindex fallback after failed bootstrap**.
   Rejected here because it extends the accepted content-error compromise to
   failed admission and does not provide a temporary-server-error HTTP signal.
4. **Deliver exact 429/503 through a new response-owner design**. A valid larger
   alternative, requiring its own concrete design and approval. Do not implement
   it through an invented trusted DTO header, quota bypass, extra content
   preflight or shared site cache.

## Decision

The public renderer distinguishes missing/denied admission from temporary second
bootstrap failure. Missing or denied admission keeps 404. Temporary RSC bootstrap
failure raises only a sanitized pre-shell framework failure and returns a neutral
HTTP 500, without a tenant document, Education read or configuration-derived
branding. Middleware-owned refusals retain their actual 404/429/503 and bounded
Retry-After. This decision does not claim RSC-owned 429/503 or Retry-After.

Temporary outcomes include non-404 site-read failures, transport/invalid-response
outcomes, configuration exceptions and a site entry reporting 503 after a
successful read. Invalid provenance, the client's verification refusal, API 404
and entry 404 remain missing/denied admission; they do not become server errors.

The proposal preserves the approved normal three-call path, five-call followed
missing path, missing-detail 307 to the fixed localized 404, and admitted
content-error HTTP 200/noindex states. It changes no provenance, quota, transport
deadline, byte bound, site-data transfer or request-memo lifetime.

## Context

At `ed6f81e`, middleware classifies its bootstrap failures, while
`getPublicRequest` collapses both API failure and entry refusal to `null`.
`requirePublicResource` then calls `notFound()` from the root layout. The same
request can therefore pass the first bootstrap and report a false 404 when the
second one is rate limited or unavailable.

The existing
[G40 package](../roadmap/phase-02d-walking-skeleton.md#routes-pagination-metadata-and-chrome)
already distinguishes admitted content failures from bootstrap refusals. Approval
adds a dated G40 replacement for the RSC part, rather than rewriting the original
accepted answer or claiming that Server Components can set arbitrary statuses.

The neutral framework failure may use the framework's generic error document;
this proposal does not promise tenant branding, localized recovery copy or an
arbitrary response status from `error.tsx`. A future change to that response
experience needs a concrete response-owner design before implementation.

## Consequences

### Positive

- Temporary site-read failure no longer declares a valid resource missing.
- Failed admission cannot load Education content or reuse stale tenant styling.
- Existing provenance, fresh reads and quota accounting remain intact.

### Negative

- A second-bootstrap 429 becomes neutral 500, rather than preserving its exact
  status and Retry-After. This limitation is explicit and must be tested.
- The neutral failure document is less informative than the admitted translated
  content-error views. No complete recovery or localization claim is made.

### Neutral

- Admitted content-error and cursor SEO policies retain their earlier approval.
- Public API responses and ADR-0052's API eligibility contract do not change.
- Studio/portal remain admitted scaffolds, with authentication/UI owned by their
  existing phases; they inherit the same bootstrap failure classification.

## Implementation Notes

P02d-6 remediation owns the typed admission result, sanitized pre-shell failure,
carrier reconciliation and production regression. On acceptance, add the dated
G40 replacement and cite this ADR from Standards 07/09 and Frontend Architecture.
Do not modify earlier Accepted ADR bodies or their historical delivery notes.

Delivery requires second-bootstrap fault injection for quota, unavailable,
transport and invalid-response outcomes after a successful middleware bootstrap.
Assert the actual original-document HTTP status, no-store behavior, no Education
read, no private diagnostics and no tenant-themed fallback. Include a recovered
next request and existing real missing/denied controls. If the pinned framework
cannot prove pre-shell 500, stop and revisit this proposal; do not silently ship
another status or a streamed 200.

## Architecture Tests

Extend the existing public page-state and isolation proofs after acceptance.
This Proposed document registers no passing test and changes no Implemented
catalogue status. Existing request-local memo and source-boundary controls remain
mandatory.

## References

- [ADR-0027 — Frontend UI Localization](0027-frontend-i18n.md)
- [ADR-0052 — Anonymous Public Read Boundary](0052-anonymous-public-read-boundary.md)
- [ADR-0053 — Trusted Public Server Rendering](0053-trusted-public-server-rendering.md)
- [ADR-0054 — Bounded Public Renderer Admission](0054-bounded-public-renderer-admission.md)
- [P02d-6 review decision package](../roadmap/phase-02d-walking-skeleton.md#p02d-6-systematic-review-decision-package-2026-10-10)
- [Frontend Error Handling](../standards/09-error-handling.md#public-page-status-and-recovery)
- [Google — HTTP status and network errors](https://developers.google.com/crawling/docs/troubleshooting/http-status-codes)
