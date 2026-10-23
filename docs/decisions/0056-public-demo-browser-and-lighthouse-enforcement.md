# ADR-0056: Public Demo Browser and Lighthouse Enforcement

## Status

> **Delivery navigation — 2026-10-11:** [Amendment 1](#amendment-1--p02d-7-enforcement-delivery-2026-10-11)
> records implemented proofs, passing real PR CI and required-check activation.
> Pending statements below describe the acceptance baseline.

Accepted

No implementation, passing audit or gate acceptance is claimed.

> **Acceptance-baseline clarification — 2026-10-11:** The preceding sentence
> records the state at acceptance, not current delivery status.
> [Amendment 2](#amendment-2--acceptance-and-reviewed-head-navigation-2026-10-11)
> distinguishes that baseline, activation history and reviewed-head evidence.

**Date:** 2026-10-11
**Deciders:** @cemil

## Decision Drivers

- P02d-7 must demonstrate two tenant sites from one production build and database.
- G44 already assigns Lighthouse activation to P7; its measurements are undecided.
- A fast error page must never satisfy a performance or accessibility check.
- The delivered visitor and peer limiters remain active during the entire proof.
- Reproducible gzip, browser and lab measurements need explicit semantics.
- The evidence must preserve the existing ingress, request-local and TLS boundaries.

## Considered Options

1. **Direct Lighthouse with managed Chromium** (chosen).
   Pin Lighthouse and the Playwright library/browser; validate the actual audited
   page, complete report inventory and explicitly defined budgets.
2. **LHCI with the runner's stock Chrome** (rejected). LHCI supplies useful
   assertions but still needs page-identity and gzip checks; stock Chrome drifts.
   LHCI 0.15.1 pins Lighthouse 12.6.1, rather than this proposal's 13.5.0.
3. **Human walkthrough or static HTML audit only** (rejected). Neither exercises
   the delivered navigation in a browser or closes G44's required full-stack job.
   This rejects human evidence as the sole gate; G33's human walkthrough remains.
4. **Make all existing timing targets hard immediately** (rejected). The landing
   page and lesson player are absent, and a shared CI lab is not a production
   p95 or field-INP baseline.

## Decision

LearnStack gates the delivered public catalog, course and lesson pages with a
seed-derived browser walkthrough and pinned Lighthouse audits over the shared
demo lifecycle. Every audit proves the identity of its actual product document;
normalized initial JavaScript gzip size, CLS and automated accessibility are hard
checks, while per-URL median timing measurements are advisory. A required CI job
runs these checks without changing the production anonymous admission budgets.

## Context

The maintainer accepted this enforcement decision on 2026-10-11. It
implements P02d-7's remaining G33/G38/G44 evidence; the
[phase package](../roadmap/phase-02d-delivery-log.md#p02d-7-decision-package-2026-10-10)
owns G20, G45, implementation steps and phase exit. The maintainer approved that
package's lifecycle and bounded seventh-check activation on 2026-10-11; this
revised ADR is accepted on the same date. G45 names the approved `make demo`
lifecycle and G33 names the approved CI topology in the register. The decision
commit records both dependencies before implementation; ADR acceptance does not
replace the lifecycle decision. It adds no production
API, new request authority, shared representation cache or deployment mode.

[ADR-0053](0053-trusted-public-server-rendering.md),
[ADR-0054](0054-bounded-public-renderer-admission.md) and
[ADR-0055](0055-public-renderer-bootstrap-failures.md) remain unchanged. Native
HTTPS ingress is mandatory; ordinary product documents cost two API calls.
The 60-call/minute visitor and 600-call/minute peer policies remain registered.

### Toolchain and page matrix

Use exact development-tool pins: `lighthouse@13.5.0` and
`playwright@1.64.0` as a library, without a new Playwright Test runner or a
separate axe suite. Install Playwright's managed full Chromium: revision 1248,
version 156.0.8078.4, with the new headless mode. The repository's Node 22.23.1
satisfies Lighthouse's Node >=22.19 requirement. Record resolved versions and the
OS/runner image; refuse a missing or mismatched browser instead of skipping.
Both tools are Apache-2.0 licensed. Pin them as development tools in
`frontend/package.json` and `frontend/pnpm-lock.yaml`, outside application runtime
dependencies. Record transitive license/security review with implementation.

Derive the URL/identity/expected-content manifest from `SeedData`; do not maintain
another list of seed slugs, titles, colors or field labels.

| Lane | Locale | Pages |
|---|---|---|
| English tenant-wide host | en | Catalog, public course, first public lesson |
| Yoga default-organization host | tr-TR | Catalog, public course, first public lesson |
| Yoga default-organization host | en | The same three resource kinds in English |

The exact seeded organization-scoped Yoga course supplies both Yoga lanes. This
is nine URLs and three cold audits per URL: exactly 27 JSON/HTML report pairs.
The browser opens each catalog, then follows rendered course and lesson links.
It checks host/locale, expected headings, ordered authored fields, distinct field
sets, computed theme colors, and exclusion of foreign/draft/restricted content.
It uses the same running API, web build and database across all lanes.
Keycloak is started for the existing seed gate, then stopped in the CI-owned
project before these fresh anonymous navigations.

These flows supplement the existing HTML/Flight and schema-replacement proofs.
[Phase 06](../roadmap/phase-06-renderer-admin-studio.md) still owns the broader
Playwright/axe golden-flow suite. Existing P6 manual/VoiceOver evidence keeps its
recorded scope and date; this ADR establishes neither a new manual pass nor full
WCAG conformance.

### Actual audited document and report admission

Before scoring, each run requires the expected URL and a successful live product
preflight. The audit additionally checks its own final URL, main-document status,
tenant/locale/content markers and original document/DOM. A healthy preflight
alone is insufficient. Inspect the audited target or its gathered artifacts
without a second post-audit navigation. A bounded custom gatherer is permitted.

Reject controlled error/noindex/fallback documents, wrong-tenant content, missing
expected fields, unexpected redirects and any API 429, including a content
refusal rendered as HTTP 200. Observe API responses as well as browser responses;
a genuine product DOM cannot turn a hidden API failure into a clean receipt.

Each report must belong to this invocation's commit, build, URL, run index,
configuration and browser. Missing, duplicate or stale reports, runtime errors,
audit errors, missing categories and null/nonfinite budgeted measurements fail
independently of the hard/advisory split. Both tenants and every locale/run must
pass; results are never averaged across URLs.

### Lab profile and budgets

Run only `performance` and `accessibility` from the pinned default configuration,
explicitly skipping `bf-cache`, whose gatherer navigates away and back. Preserve
the complete pinned accessibility audit/weight set and the four budgeted numeric
audits: `cumulative-layout-shift`, `largest-contentful-paint`,
`server-response-time` and `total-blocking-time`. The selected performance
category also gathers and scores FCP and Speed Index in Lighthouse's report;
neither has a P7 threshold, and the performance category score is not asserted.
Assert the resolved audit/artifact census and refuse missing or changed scoring
entries. Do not select `seo`, `best-practices` or `agentic-browsing`; no active
secondary-navigation probes run. Product-identity/gzip gatherers observe the
initial navigation and introduce no fetch or navigation of their own.

Use Lighthouse 13.5's mobile simulated slow-4G profile, recorded explicitly:
412 × 823 CSS pixels, DPR 1.75, 150 ms RTT, 1638.4 Kbps throughput and 4× CPU
slowdown. Record resolved settings in every run manifest. This is not the
fast-4G landing-page profile in Performance Standards.

| Measurement | Threshold | Verdict |
|---|---|---|
| `initial-js-gzip-bytes`, defined below | Strictly < 200000 bytes | Hard, every run |
| `cumulative-layout-shift.numericValue` | Strictly < 0.05 | Hard, every run |
| `categories.accessibility.score` | Exactly 1 | Hard, every run |
| `largest-contentful-paint.numericValue` | Median of three < 1500 ms | Advisory, per URL |
| `server-response-time.numericValue` | Median of three < 300 ms | Advisory, per URL |
| `total-blocking-time.numericValue` | Median of three < 200 ms | Advisory, per URL |

The advisory thresholds are P7 lab baselines across the nine delivered pages.
Main-document server response is not full-navigation TTFB; TBT is not INP.
No landing-page, lesson-player, production p95 or load-test result follows.
The 250 KB forbidden line does not replace the 200 KB route budget.
Accessibility score 1 covers Lighthouse's automated scored checks, not every
accessibility criterion.

### Normalized initial JavaScript gzip size

`initial-js-gzip-bytes` adopts an explicit, potentially stricter interpretation
of the existing public-route gzip budget in
[Performance Standards](../standards/15-performance.md#bundle-size):

- Use a cold navigation with no interactions or speculative prefetch. The
  collection window is that audit's navigation through collection completion.
- Inventory every actual external Script response in that window, same-origin
  and cross-origin, including async/lazy scripts loaded by initial scripts
  without interaction. Count each delivered response once; gzip its complete
  decoded UTF-8 source with Node `gzipSync` at level 9 and sum the byte lengths.
- Add the gzip length of original-document inline executable script bodies,
  concatenated in document order with one newline between bodies. Include Next's
  inline Flight push scripts; exclude inert JSON/data blocks and external-script
  elements already counted above. Scripts first requested after collection
  completion or through later user interaction are outside this measurement.
- Record source URL, digest, decoded byte length and gzip length, plus inline
  count/digest/lengths. Reconcile the network inventory with collected sources.
- Missing, undecodable or truncated bodies, inventory disagreement and an
  unexpectedly empty script census fail; unavailable sources never count as zero.

This is a deterministic normalized payload measurement using decimal KB. It is
neither actual wire bytes nor Next's build-time First Load JS. Lighthouse's
Script `transferSize` may use another content encoding; it cannot substitute for
this budget. Evaluation-generated code is not a delivered source response.
The collection window and inline/async coverage can reject a route that passed
a narrower build-time or entry-chunk-only reading. This tightening is intentional;
the enforcing Standards 15 update links this exact scope and its exclusions.

### Pacing, runtime and artifacts

Keep one running API throughout the proof. Use exactly four sequential batches:
one batch containing all three browser click paths, followed by three audit
batches, one per host/locale lane, each containing nine audits. Use one worker:
preflight/audit pairs execute serially, with at most one active browser target
and no overlapping audits. Record the runner's CPU count and OS/image; do not
parallelize Chromium to meet a deadline on a shared runner.
Leave at least 61 seconds after a batch completes before starting the next.
With one navigating preflight per audit, each audit batch expects 36 API calls;
the click paths expect 18. Count readiness and other documents separately.

Cap each batch at 48 observed API calls and refuse any 429 or unexplained extra
traffic. Do not spoof visitors, raise quotas, reset the API/limiter, retry 429s or
silently audit a refusal page. Prove the counting observer has a positive control.
Startup checks also consume quota: finish them before the first 61-second quiet
interval. Real counts, not the arithmetic alone, own the passing evidence.

The proof has one 12-minute wall-clock deadline after setup/build, starting
before the first quiet interval and ending after report validation. All four
61-second quiet intervals consume it: 244 seconds, leaving at most 476 seconds
for browser paths, all preflights, 27 audits and validation. Audit/preflight
pairs must therefore average less than 17.6 seconds even before deducting the
browser paths and validation; no measured feasibility is claimed yet.

Navigation and audit timeouts are upper bounds, not reserved time allocations:
each navigation gets at most 15 seconds and each audit at most 60 seconds,
both shortened to the remaining proof deadline. Exhaustion aborts the active
operation, refuses unstarted work and fails the run; partial evidence cannot
pass. In particular, 27 audits taking 60 seconds each cannot pass this deadline.
Owned cleanup has a separate maximum of 30 seconds. The 20-minute CI job ceiling
includes setup/build, proof, cleanup and artifact handling; record each duration
and reserve cleanup/artifact time within that ceiling.

The first full serial run is a feasibility gate before required-check activation.
If the complete run cannot meet these limits, record a failed result and obtain
a revised decision for changed time ceilings or execution topology. Do not
silently run parallel audits, reduce the 27-run census or weaken checks to fit.

Keep reports and sanitized receipts in GitHub workflow artifacts only:
all 27 JSON/HTML pairs, expected/run census, metrics verdicts, version/config/
commit manifest, browser screenshots, API status/count totals and cleanup result.
Upload bounded failure evidence on failure. Never archive private environments,
raw process logs, browser profiles, certificates or keys, and never upload to a
public Lighthouse service.

### Trust and required-check activation

Use the shared lifecycle's ownership and verified TLS contract. Human local use
retains explicit manual hosts/CA/leaf setup. CI uses disposable certificates,
certificate-specific browser SPKI trust, resolver mappings and independent Node
CA/hostname verification with wrong-key/untrusted-certificate controls.
Global certificate bypass or system hosts/trust modifications are forbidden.

The job is named `public demo + lighthouse`, runs on the normal PR/push triggers
without the deferred condition, and fails if any proof or cleanup is incomplete.
After its first real successful PR run, add that exact GitHub Actions context as
the seventh required check. Read back protection and preserve all six existing
contexts, strict freshness and every unrelated setting. Acceptance authorizes
only this bounded activation after the successful run; it does not claim activation.

## Consequences

### Positive

- Browser navigation, product identity and budgets become repeatable merge gates.
- Error pages, missing metrics and empty/stale evidence cannot produce green CI.
- Seed-derived expectations preserve the same-code/different-data claim.

### Negative

- Managed Chromium and Lighthouse increase dependency and CI setup costs.
- Real-window pacing adds deliberate waiting; browser/lab maintenance remains work.
- The normalized gzip collector and audit-page admission need their own controls.

### Neutral

- Performance becomes Active only for implemented hard checks, in the enforcing PR.
- Broader performance baselines and telemetry remain Phase 11.
- Local demo lifecycle details remain G45 phase decisions, rather than a new runtime.

## Implementation Notes

Acceptance precedes runtime/dependency/CI changes. Reconcile Testing 06's current
“Phase 02d … gates on a human opening them, not on a Playwright run” statement
when the narrow P7 browser job ships; preserve Phase 06's broader suite ownership.
Make the public-route size unit explicit in Standards 15 as decimal KB
(`< 200000` bytes), alongside its normalized-gzip window, same-/cross-origin
scope, exclusions and potentially stricter interpretation.

Register the canonical rules below in Standards 21 on acceptance as
**Kind: behavioural**, owned by the planned
Node entrypoint `frontend/apps/web/scripts/verify-public-demo.mjs` and the
`public demo + lighthouse` job; these are not C# architecture-test methods.
Mark them Implemented only with named, executed enforcing tests. The phase
package owns four implementation steps and two fresh independent review rounds
per step. Update Standards 06/15/16, the
standards index, CI, CONTRIBUTING and test/setup skills alongside enforcement.
Acceptance registers proof obligations, not passing tests or delivered enforcement.

## Architecture Tests

- `Public_Demo_Uses_Seeded_Production_Pages`: real link navigation and page/role/
  theme/locale observations, with wrong-page and error-page controls.
- `Public_Lighthouse_Reports_Enforce_Budgets`: exact census and hard/advisory
  validation, with missing/stale/error/budget offenders and clean reports.
- `Public_Initial_JavaScript_Gzip_Budget_Is_Measured`: real source inventory and
  byte accounting, with omitted/empty/malformed sources and over-budget controls.
- `Public_Demo_Preserves_Admission_And_Cleanup`: real quotas, Keycloak-stopped
  reads, TLS refusal and owned lifecycle, with observer and cleanup controls.

These are registered behavioral obligations for that Node/CI proof owner, not
existing scripts or C# test methods.

## Amendments

### Amendment 1 — P02d-7 enforcement delivery (2026-10-11)

The four-step implementation and both independent review rounds per step are
complete. All four behavioral rules above are Implemented with named executable
owners. The shared demo lifecycle supplies the actual linked nine-page browser
proof and 27 serial cold Lighthouse audits, bounded report/source admission,
hard gzip/CLS/automated-accessibility checks, real quota accounting and confirmed
owned cleanup. Per-URL timing medians remain advisory.

The first real [PR run](https://github.com/HodeTech/LearnStack/actions/runs/38106505383)
passes all seven jobs. Its complete sanitized artifacts and the subsequent
authorized seventh-context activation/full protection read-back are verified in
the [CI and exit closeout](../roadmap/phase-02d-delivery-log.md#p02d-7-ci-and-phase-exit-closeout-2026-10-11).
PR #28 remains subject to maintainer review and merge. This is delivery navigation;
the acceptance metadata, decision and feasibility obligations remain unchanged.
Existing P6 human/VoiceOver evidence and Phase 06/11 scope retain their limits.

### Amendment 2 — Acceptance and reviewed-head navigation (2026-10-11)

The original no-implementation sentence describes acceptance-time state.
Amendment 1 describes later delivery and the first real run used for activation.
The [reviewed-head verification](../roadmap/phase-02d-delivery-log.md#reviewed-implementation-head-verification-2026-10-11)
separately records source `d47ef17` and its downloaded successful CI artifacts.
Subsequent heads remain subject to all seven required checks. This clarification
changes no decision, tool version, budget or original acceptance metadata.

## References

- [P02d-7 package](../roadmap/phase-02d-delivery-log.md#p02d-7-decision-package-2026-10-10).
- [Performance Standards](../standards/15-performance.md).
- [Testing Standards](../standards/06-testing.md#end-to-end-tests).
- [Accessibility Standards](../standards/16-accessibility.md#tooling).
- [Lighthouse 13.5.0 package and Node requirement](https://github.com/GoogleChrome/lighthouse/blob/v13.5.0/package.json).
- [Lighthouse programmatic use](https://github.com/GoogleChrome/lighthouse/blob/v13.5.0/docs/readme.md#using-programmatically).
- [Lighthouse mobile profile selection](https://github.com/GoogleChrome/lighthouse/blob/v13.5.0/core/config/constants.js).
- [Pinned slow-4G upstream constants](https://github.com/ChromeDevTools/devtools-frontend/blob/v1.0.1684555/front_end/models/trace/lantern/simulation/Constants.ts#L12-L18), matching Lighthouse's trace-engine 0.0.65 dependency.
- [Lighthouse default audit set](https://github.com/GoogleChrome/lighthouse/blob/v13.5.0/core/config/default-config.js).
- [Lighthouse audit/artifact filtering](https://github.com/GoogleChrome/lighthouse/blob/v13.5.0/core/config/filters.js).
- [Lighthouse Script source collection](https://github.com/GoogleChrome/lighthouse/blob/v13.5.0/core/gather/gatherers/scripts.js).
- [Lighthouse resource-summary transfer semantics](https://github.com/GoogleChrome/lighthouse/blob/v13.5.0/core/audits/resource-summary.js).
- [Lighthouse TBT](https://developer.chrome.com/docs/lighthouse/performance/lighthouse-total-blocking-time).
- [Lighthouse accessibility scoring](https://developer.chrome.com/docs/lighthouse/accessibility/scoring).
- [Playwright library](https://playwright.dev/docs/library).
- [Playwright 1.64.0 package/license](https://github.com/microsoft/playwright/blob/v1.64.0/packages/playwright/package.json).
- [Playwright 1.64.0 browser manifest](https://github.com/microsoft/playwright/blob/v1.64.0/packages/playwright-core/browsers.json).
- [Playwright browser installation](https://playwright.dev/docs/browsers).
- [LHCI 0.15.1 dependency](https://github.com/GoogleChrome/lighthouse-ci/blob/v0.15.1/packages/cli/package.json).
