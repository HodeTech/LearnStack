# ADR-0096: P03-5 Administration Contract Baseline

## Status

Proposed

**Date:** 2026-11-10
**Deciders:** @cemil (approval pending)

This proposal changes no executable compatibility policy. Acceptance requires the
maintainer to confirm that no external deployment or integration consumes the
prior affected administration contract. That absence is not established by
repository or CI inspection. If an old external consumer exists or usage is
unknown, this proposal does not apply;
a separately approved versioned contract design is required.

On acceptance, this narrowly supersedes
[ADR-0024](0024-api-versioning-policy.md)'s new-major requirement for exactly the
reviewed pre-MVP transition below. ADR-0024 remains Accepted everywhere else.
[ADR-0095](0095-revision-pinned-membership-custom-fields.md)'s field, consent,
authority and explicit v3-adoption decisions remain unchanged.

## Decision Drivers

- P03-5's approved permission pair and v3 marker expand closed administration
  responses. An older SDK also rejects the new tenth capability property.
- The pinned OpenAPI gate correctly refuses the new grant branch and response
  bounds. A passing served-snapshot comparison does not prove compatibility.
- Concealing an actual grant or reporting an old release marker misrepresents
  state. A partial grant projection is unsafe beside complete grant replacement.
- MVP delivery should not introduce a second major without a real coexistence
  requirement; an unreleased baseline change must still be explicit and bounded.
- Approval must not become a reusable switch that permits future breaking changes.

## Considered Options

1. **One reviewed pre-MVP baseline transition** (selected proposal). Require the
   maintainer's no-old-external-consumer confirmation, retain raw incompatibility
   evidence
   and admit only the exact old/new snapshot bytes. Old SDK compatibility is lost.
2. **Versioned administration with coexistence** (required if that confirmation
   cannot be given). Design truthful legacy Role representation, v3-capable
   readers and safe complete grant replacement together; adding a v2 route alone
   does not settle shared v3 state. This adds a separate implementation packet.
3. **Delay v3 activation** (not selected). Keep shipped v1 behavior and leave
   P03-5's definition administration incomplete until its transport decision.
4. **Disable the gate, remove bounds or merely mark enums open** (rejected).
   None makes an already shipped closed SDK accept the new response.

## Decision

After the required maintainer confirmation, LearnStack admits one exact pre-MVP
P03-5 administration baseline transition under `/api/v1`. The API, generated SDK,
native layer and web application update together before the instance is exposed.
The compatibility gate retains the raw breaking result and records an explicit
ADR-bound approval for these exact snapshot bytes; every other comparison follows
ADR-0024 unchanged. This is an approved incompatibility, not a non-breaking change.

## Context

### Verified conflict

The [first P03-5 hosted attempt](https://github.com/HodeTech/LearnStack/actions/runs/42769169256)
compares main `570c4cca` with development `94de9e4e`. The OpenAPI job completes
with five raw errors: permission registry `items` and `release.tenantAdminDelta`
bounds rise from eight to nine; Role detail `grants` rises from eight to nine;
the two grant response unions gain a branch. Indexed union comparison adds
companion enum noise, but reordering alone cannot remove the real incompatibility.
The original archive's API size, SHA-256 and ZIP CRC are verified independently.

The previous SDK accepts only eight known pairs and adoption versions one/two.
Its capability parser requires exactly nine keys. Current P03-5 responses therefore
cannot be made compatible by changing schema text. The registry's default/source-1
response already retains the v2/eight-item view; source two selects v3. Role detail
still reports actual state, and capabilities gain the tenth flag on every read.

### Exact approved transition

The SHA-256 values cover the actual committed `backend/openapi/v1.json` bytes,
including serialization. They are computed from verified Git snapshots, never
accepted from a report or caller-supplied digest.

| Snapshot | SHA-256 |
|---|---|
| Prior contract | `80b319161acf2893b33b4d23731faf720df55828e6af67bd17e57307ae33ed56` |
| Reviewed P03-5 contract | `a32f7813870d729cc17ae6c72fa6a5b291ae5823ba77c6c627fe1e7e0c018b51` |

The affected existing read surfaces are:

- GET `/api/v1/identity/permissions`: the explicit source-version-two path can
  describe v3/nine pairs; source one/default behavior remains truthful.
- GET `/api/v1/identity/roles/{id}`: actual grants and the actual v3 adoption marker
  remain visible. No grant is hidden to make a legacy parser succeed.
- GET `/api/v1/identity/me/capabilities`: the approved tenth advisory Boolean is
  present. Older exact-key parsers reject it even though adding a response field
  is normally classified as additive.

The complete target snapshot also contains ADR-0095's reviewed new operations.
This approval covers those exact bytes, not an extensible list of permitted
paths, permission keys, enum additions, bounds or registry releases. No other
request, response, status, authorization or mutation semantic is exempted.

### Gate behavior

- Both pinned raw-tool and companion-policy comparisons must complete normally
  and produce valid classified findings before approval is considered. Their
  original findings remain retained. Only a completed incompatibility result is
  approvable: tool/version/fixture/execution/I/O errors, malformed or missing
  reports and incomplete evidence remain fatal even for the exact approved pair.
  An approval cannot turn a failing fixture into a pass.
- A single checked-in transition record names ADR-0096 and the two exact hashes.
  Only an Accepted ADR-0096 can activate it. Missing, malformed, duplicate or
  additional records refuse; no environment, CLI flag or output receipt grants
  approval. The implementation owns the fixed record schema and file path.
- Admission requires computed base and head hashes to match in that direction.
  Any changed byte, reverse transition or different base/head remains subject to
  the normal breaking policy. Adding another change never inherits this approval.
- The result reports both the raw incompatibility and the consumed approval.
  A green job means an explicitly reviewed transition, not raw compatibility.
- The immutable record may remain as history. It is not consumed via mutable
  deployment state and permits no later distinct contract transition.

### Consumer and deployment boundary

The maintainer confirms that there is no external consumer of the prior affected
administration contract; CI cannot infer this from an empty repository search or
the development version `0.0.0`. Operators
update the API and its bundled SDK/native/web consumers together. Mixed old/new
consumers are unsupported for this one transition and must not be exposed during
the update. No zero-downtime rollout or external-client compatibility is claimed.

If an external old client exists, this decision is inapplicable. A new versioned
design must explicitly settle truthful legacy reads and complete-replacement
safety; suppressing a grant, forging a marker or silently changing PUT semantics
is not an alternative implementation of this proposal.

## Consequences

### Positive

- The already approved P03-5 behavior can finish without an unnecessary parallel
  API major when the maintainer confirms there are no old external consumers.
- Compatibility failures stay visible and auditable; all unknown future changes
  continue to fail the existing gate.

### Negative

- Old SDK consumers cannot use the revised administration contract. An incorrect
  no-old-external-consumer confirmation would cause real client failures.
- The release requires coordinated API/SDK/native/web replacement. A mixed-version
  rollout can refuse otherwise legitimate reads.
- Future closed-set permission/release growth still follows ADR-0024; this is not
  an open permission metadata protocol or an automatic future exception.

### Neutral

- No permission, automatic grant, DB role, definer, context writer, migration or
  runtime/test/CI window is added by this decision.
- Full same-source hosted A/B and original-archive reconciliation, actual product
  tests and the separately recorded human accessibility evidence remain required.

## Implementation Notes

1. Before executable policy changes, obtain explicit approval including the
   no-old-external-consumer confirmation. Move this record/index to Accepted and
   append
   dated navigation Amendments to ADR-0024 and ADR-0095 without rewriting them.
2. Add the single fixed-schema transition carrier under `backend/openapi/policy/`.
   Extend `scripts/openapi-ci.py` and its existing policy integration only after
   normal comparisons. Bind approval to verified snapshot bytes and Accepted
   ADR-0096, not fixture mode or an arbitrary user-selected manifest.
3. Preserve existing raw/normalized specs, tool version, findings and Git SHA
   receipts. Add a distinct approval field to the result and keep the CLI fixture
   entry point unable to consume transition approval.
4. Reconcile [API Standards](../standards/04-api-design.md),
   [Testing Standards](../standards/06-testing.md), the existing
   [served contract rule](../standards/21-architecture-tests-catalogue.md#publicsurface_contract_matches_served_openapi),
   contributor guidance, Phase 03 Status and the append-only delivery record.
5. Test exact approval, normal unchanged/additive comparisons and refusals for
   changed/reversed hashes, unknown/missing/unaccepted ADR, duplicate/extra/malformed
   records and declared-versus-computed digest substitution. Existing request,
   closed-enum, response-removal, recursion and sibling-break fixtures still fail
   as specified. On the exact approved pair, tool/version/fixture failures,
   malformed/missing reports and incomplete comparisons must also fail. Do not
   promote catalogue proof status on acceptance alone.
6. Complete local checks and two independent implementation reviews; push the
   frozen source once, execute complete A then whole-run B and verify original
   ZIP/API/case-union evidence. The PR names this approved incompatibility and
   coordinated consumer update. No merge is authorized by this record.

## Architecture Tests

Extend the existing CLI/served-contract controls, not the runtime authorization
surface. A planted unapproved contract must still fail even beside a valid
transition carrier; approval metadata cannot override actual input bytes.
Missing/empty inventories and a Proposed status fail closed. Preserve
`PublicSurface_Contract_Matches_Served_OpenApi` and the actual SDK/native response
parsers, including rejection of malformed and unknown authority inputs.

## References

- [ADR-0024](0024-api-versioning-policy.md),
  [ADR-0095](0095-revision-pinned-membership-custom-fields.md).
- [API Standards](../standards/04-api-design.md),
  [Testing Standards](../standards/06-testing.md).
- [OpenAPI CI](../../scripts/openapi-ci.py),
  [compatibility policy](../../scripts/openapi-policy.py),
  [SDK administration policy](../../frontend/packages/sdk/src/administration-policy.ts).
- [Phase 03 Status](../roadmap/phase-03-identity-admin.md).
