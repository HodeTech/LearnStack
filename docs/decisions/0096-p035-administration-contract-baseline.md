# ADR-0096: P03-5 Administration Contract Baseline

## Status

Accepted

**Date:** 2026-11-10
**Deciders:** @cemil

The maintainer approves the corrected decision and confirms that the application
is development-only and not actively used. There is no old consumer outside the
coordinated API/SDK/native/web bundle requiring coexistence for this transition.
The dated acceptance record is the
[Phase 03 maintainer disposition](../roadmap/phase-03-delivery-log.md#adr-0096-maintainer-review-disposition-and-acceptance-2026-11-10).
CI cannot establish this usage fact. This is one reviewed incompatibility, not a
general pre-MVP exemption. Acceptance precedes executable gate implementation and
implies neither runtime qualification nor successful hosted A/B.

This narrowly supersedes [ADR-0024](0024-api-versioning-policy.md)'s new-major
requirement only for the exact reviewed transition below. ADR-0024 stays Accepted
elsewhere; its body remains historical and is not reduced to a redirect.
[ADR-0095](0095-revision-pinned-membership-custom-fields.md)'s field, consent,
authority and explicit v3-adoption decisions remain unchanged.

## Decision Drivers

- P03-5's approved pair and v3 marker expand closed administration responses.
  The older SDK also rejects the tenth capability property.
- The pinned OpenAPI gate correctly refuses the new grant branch and bounds.
  Served-snapshot equality does not prove compatibility or runtime behavior.
- Concealing an actual grant or forging an old marker misrepresents state beside
  complete grant replacement.
- No active old consumer requires a second major for this development baseline.
- Changing a carrier, report, comparator or tool cannot reuse this approval for
  new findings or distinct contract bytes.

## Considered Options

1. **One reviewed pre-MVP baseline transition** (selected). Record the maintainer's
   usage confirmation, preserve incompatibility evidence and admit only the exact
   snapshots, findings and comparison identities. Old SDK compatibility is lost.
2. **Versioned administration with coexistence** (not selected). Required if an
   old consumer outside the coordinated bundle exists or usage is unknown. This
   must settle truthful legacy Role reads and safe complete grant replacement;
   adding a v2 route alone does not resolve shared v3 state.
3. **Delay v3 activation** (not selected). This leaves approved P03-5 administration
   incomplete without a demonstrated coexistence need.
4. **Disable the gate, remove bounds or merely open enums** (rejected). None makes
   an already shipped closed SDK accept the new response.

## Decision

LearnStack admits one exact pre-MVP P03-5 administration baseline transition under
`/api/v1`. The API, generated SDK, native layer and web application update together
before exposure. The gate retains raw incompatibility and records an explicit
ADR-bound approval only for the snapshot bytes, full finding sets and comparison
identities below. Every other comparison follows ADR-0024. A passing approved
transition is not a non-breaking result or proof of runtime behavior.

## Context

### Verified conflict and complete change inventory

The [first hosted attempt](https://github.com/HodeTech/LearnStack/actions/runs/42769169256)
compares main `570c4cca` with development `94de9e4e`. Five raw findings describe
permission registry `items` and `release.tenantAdminDelta` bounds rising from
8 to 9, Role detail `grants` rising from 8 to 9, and the shared grant union gaining
a branch in two response positions. The companion adds 54 indexed enum findings.
Reordering cannot remove the real incompatibility. The
[retained archive record](../roadmap/phase-03-delivery-log.md#p03-5-hosted-incompatibility-and-proposed-administration-baseline-2026-11-10)
owns API size/SHA-256 and ZIP CRC verification.

The previous SDK accepts eight known pairs, adoption versions 1/2 and exactly nine
capability keys. The revised SDK accepts the reviewed v3/nine-pair graph and tenth
flag. The affected existing surfaces are:

- GET `/api/v1/identity/permissions`: absent/default `sourceReleaseVersion=1`
  returns the truthful v2/eight-item view; `sourceReleaseVersion=2` selects v3/nine.
- GET `/api/v1/identity/roles/{id}`: actual grants and adoption markers remain
  visible. No grant is hidden to satisfy an older parser.
- GET `/api/v1/identity/me/capabilities`: every read includes the tenth advisory
  Boolean. Older exact-key parsers reject it.
- PUT `/api/v1/identity/roles/{id}/grants`: `RoleGrantsInput.grants` also expands
  from 8 to 9 and accepts the new pair. This request loosening and the optional
  registry selector pass under normal ADR-0024 rules, not this exception. An old
  client cannot safely read the new graph before constructing a complete
  replacement; mixed consumers are therefore unsupported.

The target also includes ADR-0095's new operations. Approval is not an extensible
list of paths, bounds, permissions or releases.

### Approved snapshot bytes

SHA-256 covers complete committed `backend/openapi/v1.json` bytes, including
serialization, read from verified Git commits rather than caller/report digests.

| Snapshot | Commit | SHA-256 |
|---|---|---|
| Prior contract | `570c4cca` | `80b319161acf2893b33b4d23731faf720df55828e6af67bd17e57307ae33ed56` |
| Reviewed P03-5 contract | `94de9e4e` | `a32f7813870d729cc17ae6c72fa6a5b291ae5823ba77c6c627fe1e7e0c018b51` |

### Approved complete findings

Each report is strict JSON: duplicate object keys and nonfinite numbers refuse.
Canonicalize every complete finding object with sorted keys, ASCII JSON escapes,
compact `,`/`:` separators and no trailing newline. Reject duplicate objects;
sort the outer array by those canonical ASCII strings, preserve every nested
array's order, then serialize the sorted array in the same form. SHA-256 covers
those bytes. No field, including text, level, location or fingerprint, is dropped.
An additional field or finding, a removal or a changed value refuses; only outer
finding order and JSON object-key order are immaterial.

| Report | Count | SHA-256 |
|---|---|---|
| Raw tool | 5 | `0f182c772e6393871a0d61aefafd495eb26f797d1baccb99af86a63c1665afd8` |
| Normalized tool | 5 | `0f182c772e6393871a0d61aefafd495eb26f797d1baccb99af86a63c1665afd8` |
| Companion policy | 54 | `30995cc1910a22269e1db3d4eb6093e6d19a772958c41e37dfc3d69c9d799b1a` |

The actual unchanged comparator and inputs are pinned as well. Reproduced
findings from a changed policy implementation/configuration do not inherit
approval. These are drift controls under repository review, not protection against
an actor authorized to rewrite the gate and its decision together.

| Comparison input | SHA-256 |
|---|---|
| `scripts/openapi-policy.py` | `409651c82d5af4bcf3e88bc2ae93ebe0fbd1b7d161f25ee24c0a774fc7ec8bb9` |
| `backend/openapi/policy/oasdiff.json` | `ca3d163bab055381827226140568f3bef7eaac187cebd76878e0b63e9e442356` |
| `backend/openapi/policy/severity.txt` | `59c9b14bf09cbf0278c08c950472cc217a35518bf67dc54d26acec80d7ef4f4f` |
| `backend/openapi/policy/fixtures/base.json` | `be0413c7fbc306fc6008928e8571d30168116425eeb48fd6113afaba84ac2ae0` |
| `backend/openapi/policy/fixtures/cases.json` | `68810bfb29f42ff2a4dfa8628bd8db9a2b6a0c1866adc9efb6c097dd2037e21c` |
| Linux AMD64 oasdiff 1.33.0 executable | `fa65aae43c867e9f79da9cb121d3c1a113637883afe014c0680fd5b50696f416` |

The executable identity derives from the retained CI installation of the approved
archive SHA-256 `43a4e328e2d13ba1552d760aa68d2485c75c5621f309f6ff64ae895188345247`.
A self-reported version or installation receipt is insufficient. Approval uses
that exact executable; ordinary workstation file comparisons remain available
without approval. Comparator flags are covered by the unchanged script identity.

### Gate behavior and carrier

- Approval exists only in `scripts/openapi-ci.py`'s verified Git-snapshot path.
  Direct `openapi-policy.py --base/--head` file mode, fixture mode and the
  first-baseline/bootstrap path never consume it.
- Each wrapper invocation requires a fresh output directory, so no report or
  control result can survive from a failed earlier invocation. Both raw and
  normalized comparisons complete normally with valid findings and matching
  refusal summaries. Tool/version/execution/I/O errors, missing or
  malformed reports and incomplete evidence remain fatal, even for matching
  snapshot hashes. All three complete finding sets must match the table above;
  an extra raw, normalized or companion violation refuses.
- Before returning approved success, the CI wrapper freshly runs the existing
  60 policy controls with the same verified tool/comparator and validates their
  complete successful result. The existing required-job fixture step remains;
  neither its failure nor a stale/missing fixture receipt can become approval.
- The sole carrier is `backend/openapi/policy/transition-0096.json`, one object
  with exactly `schemaVersion`, `adr`, `baseSha256`, `headSha256`, `findings` and
  `inputs`. Version is integer 1; ADR is `0096`; findings contain exactly
  `raw`, `normalized`, `companion`, each with `count` and `sha256`. Inputs map
  exactly the six comparison-input labels above to their SHA-256 values.
  Duplicate keys, extra/missing fields, arrays, malformed hashes and additional
  `transition-*.json` files refuse approval. No caller-selected manifest,
  environment variable or output receipt authorizes it.
- Read the carrier and ADR-0096 from the verified head commit. Require exactly
  Accepted status and bind every carrier digest/count to the corresponding
  table in this Accepted ADR. Repointing the carrier alone to another valid pair
  fails. Proposed, Superseded, Deprecated, unknown or missing status refuses.
  ADR-0041 governs visible changes to this Accepted decision.
- Compute snapshot hashes from actual Git bytes. Compute the tool hash from the
  executed regular file and comparator/input hashes from actual execution bytes;
  the latter must also match the verified head. A report's digest claim is never
  the authority. Changed or reversed snapshots cannot inherit approval.
- Preserve original reports and emit a distinct approval result beside them.
  Success identifies an approved incompatibility, not raw compatibility.
  The carrier is retained as history and approval is idempotent only for this
  exact pair and finding/identity combination. A later distinct transition
  requires a new ADR and carrier; this record cannot be repointed for it.

### Runtime and coordinated delivery boundary

Snapshot approval covers contract bytes and comparison findings only. The same
snapshot can accompany incorrect handler status, authorization, registry selection,
capabilities or replacement behavior. Neither this gate nor served-snapshot
comparison establishes those behaviors or SDK implementation identity.

The final frozen source must separately pass actual API/SDK checks for default
and selected v2/v3 registries, actual v3 Role detail, the tenth capability and
complete grant replacement with authority removal/refusal. A shape-preserving
incorrect-response control must fail those checks while leaving the contract
snapshot unchanged. These source-bound observations and complete hosted A/B are
required PR evidence; no other runtime semantic obtains an exemption here.

The maintainer's development-only/no-active-use confirmation is recorded at
acceptance, not inferred from repository searches or version `0.0.0`. The boundary
includes separate internal/operator scripts outside the coordinated bundle.
Update API/SDK/native/web together before exposure. Revert a code deployment only
as a coordinated bundle compatible with the installed data; retained data/schema
use reviewed forward repair under ADR-0095. Do not partially revert consumers or
advertise zero-downtime rollout. Unexpected old-parser failures on the three read
surfaces reopen this decision before exposure continues.

## Consequences

### Positive

- Approved P03-5 behavior can finish without an unused parallel API major.
- Original incompatibilities remain visible and every unexpected finding refuses.

### Negative

- An old consumer cannot use the revised contract. An incorrect usage confirmation
  or mixed deployment can cause legitimate reads to fail.
- Comparator, tool or reviewed-finding drift requires renewed decision evidence;
  a pinned snapshot alone cannot carry future approval.

### Neutral

- No permission, grant, DB role, definer, context writer, migration, runtime/test/CI
  duration or time-boxed bypass is added by this decision.
- Acceptance does not replace actual product, human or same-source hosted A/B
  qualification and original-archive reconciliation.

## Implementation Notes

1. Record the maintainer's approval/usage confirmation and this review disposition
   in the append-only Phase 03 log; reconcile index/Status. Append an ADR-0024
   bounded-supersession Amendment and an ADR-0095 navigation Amendment explaining
   the separate transport decision. Their bodies and Accepted statuses remain.
2. Add the fixed carrier and a confined admission helper used only by
   `openapi-ci.py` after normal comparisons. Keep `openapi-policy.py` unchanged;
   its CLI file/fixture modes cannot consume approval.
3. Retain Git SHAs, original/normalized specs, all findings and tool identity.
   Run/validate fresh controls before emitting a distinct approval result.
4. Reconcile API 04, Testing 06, the existing served-contract catalogue rule,
   policy README and contributor guidance; add no rule/status promotion.
5. Cover order-only canonicalization; added/changed/removed/duplicate findings in
   all three channels; record repointing; Accepted-ADR/table binding; every other
   lifecycle; unknown/extra/malformed carriers; declared/computed substitutions;
   changed tool/config/comparator/fixtures; bootstrap/file/fixture exclusion; and
   completed-pair tool/report/fixture failures. Normal additive/breaking controls
   retain their existing outcomes. Prove the runtime boundary separately.
6. Complete local checks and two independent implementation review rounds. Push
   the frozen source, run complete A then whole-run B and verify original archives.
   The ready PR identifies this incompatibility and coordinated consumer update.
   No merge is authorized.

## Architecture Tests

Extend existing Git-snapshot/CLI and actual API/SDK controls. A record beside an
unapproved snapshot, an unexpected finding or a failed comparison never creates
success. Preserve `PublicSurface_Contract_Matches_Served_OpenApi` and actual
response/authority parsers. Negative runtime controls demonstrate the snapshot
boundary; they are not claims that hash admission verifies runtime semantics.

## References

- [ADR-0024](0024-api-versioning-policy.md),
  [ADR-0095](0095-revision-pinned-membership-custom-fields.md).
- [API Standards](../standards/04-api-design.md),
  [Testing Standards](../standards/06-testing.md),
  [served-contract rule](../standards/21-architecture-tests-catalogue.md#publicsurface_contract_matches_served_openapi).
- [OpenAPI CI](../../scripts/openapi-ci.py),
  [policy/comparator](../../scripts/openapi-policy.py),
  [snapshot controls](../../scripts/test-openapi-ci.py),
  [policy README](../../backend/openapi/policy/README.md).
- [SDK administration policy](../../frontend/packages/sdk/src/administration-policy.ts),
  [Phase 03 Status](../roadmap/phase-03-identity-admin.md).
