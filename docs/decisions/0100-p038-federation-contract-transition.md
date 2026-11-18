# ADR-0100: P03-8 Federation Contract Transition

## Status

Proposed — awaiting maintainer review. On acceptance this paragraph records the
maintainer's approval and development-only/no-active-use confirmation, as
[ADR-0096](0096-p035-administration-contract-baseline.md) does for its transition.

**Date:** 2026-11-18
**Deciders:** @cemil

This narrowly supersedes [ADR-0024](0024-api-versioning-policy.md)'s new-major
requirement only for the exact transition below, as ADR-0096 did for its own. It also
replaces one sentence of ADR-0096's gate rules: that `transition-0096.json` is the sole
carrier and that any additional `transition-*.json` refuses approval. ADR-0096's pair,
tables and approval are unchanged. ADR-0024 and ADR-0096 stay Accepted.

## Decision Drivers

- Registry release v4 adds one assignable pair, `Tenant:identity.federation.admin`.
  The administration grant union is closed and every grant-array bound tracks the
  registry ([Permission Standards](../standards/19-permissions.md)), so the pinned
  OpenAPI gate refuses the new branch and the raised bounds, as it did for v3.
- The native self read carries the host tenant's `federationAlias`
  ([ADR-0040](0040-ambient-unit-of-work.md) Amendments 22 and 23) inside the closed
  self union. The gate refuses that in its normalized channel; the property, not its
  `required` marker, causes it.
- ADR-0096's approval is spent. The `main` snapshot is byte-identical to its reviewed
  contract, and its carrier cannot be repointed to another pair.
- No active old consumer requires a second major for this development baseline.
- Each further assignable permission key repeats the same refusals. The permission
  documentation lists 64 distinct keys and ten pairs are assignable now. This record
  admits only the P03-8 pair and leaves that recurrence to an owned decision below.

## Considered Options

1. **One reviewed P03-8 transition, one carrier per ADR** (selected). Record the
   maintainer's usage confirmation, preserve the incompatibility evidence and admit only
   the exact snapshots, findings and comparison identities. The old SDK cannot parse
   the revised contract.
2. **Make the P03-8 contract non-breaking** (not selected). Measured below: the five
   raw findings are the registry's growth itself and cannot be removed while the grant
   union stays closed. Hiding the new grant misrepresents state, which ADR-0096 refuses.
   Making `federationAlias` optional leaves the self finding unchanged.
3. **Redesign the grant contract to open keys now** (not selected for this packet). It
   still needs one approved transition (measured below) and gives up the SDK's
   fail-closed exact-key parsing. It belongs to the standing-rule decision below.
4. **A standing registry-growth class rule** (not selected here). It would avoid a
   decision per key but reverses ADR-0096's "not a general exemption" stance and changes
   the gate's semantics. It is the owned decision below, not part of this packet.
5. **Disable the gate, remove bounds or merely open enums** (rejected). None makes an
   already shipped closed SDK accept the new response.

## Decision

LearnStack admits one exact pre-MVP P03-8 federation contract transition under
`/api/v1`. The API, generated SDK, native layer and web application update together
before exposure. The gate retains raw incompatibility and records an explicit ADR-bound
approval only for the snapshot bytes, complete finding sets and comparison identities
below. Every other comparison follows ADR-0024. A passing approved transition is not a
non-breaking result or proof of runtime behavior.

The carrier rule generalizes: each approved transition has its own carrier, bound to
the Accepted ADR it names. A later distinct transition still needs a new ADR and a new
carrier; no earlier record is repointed for it.

## Context

### Verified conflict and complete change inventory

The comparison is `main` at `0c7394c7` (the merge of PR #44, whose snapshot is
ADR-0096's reviewed contract) against the P03-8 head. The head adds eleven
`/api/v1/identity/federation` operations, the anonymous `/api/v1/public/sign-in`
GET and HEAD pair, their schemas, registry release v4 with its tenth assignable pair,
the eleventh capability flag `canManageFederation` and `federationAlias` on both self
states. Additions that the pinned tool does not refuse are not findings.

The pinned comparison refuses these, in three channels:

- **Raw tool, five.** On `GET /api/v1/identity/permissions`: `items` and
  `release.tenantAdminDelta` `maxItems` rise from 9 to 10, and a `oneOf` branch is added
  to `release.tenantAdminDelta.items`. On `GET /api/v1/identity/roles/{id}`: `grants`
  `maxItems` rises from 9 to 10, and a `oneOf` branch is added to `grants.items`.
- **Normalized tool, six.** The five above plus `response-body-one-of-added` on
  `GET /api/v1/identity/self`, caused by `federationAlias` inside both self states.
- **Companion policy, 54.** The new branch sorts into the grant union at index 1, so
  the nine branches after it shift. The positional comparison counts each shift as a
  closed-enum removal and addition: 18 response additions and 18 removals, and 9 request
  additions and 9 removals.

The previous SDK accepts nine known pairs, adoption versions 1 to 3 and exactly ten
capability keys. The revised SDK and BFF accept ten pairs, version 4, eleven keys and
the extra self property. The affected existing surfaces are:

- GET `/api/v1/identity/permissions`: the selected release view now includes v4.
- GET `/api/v1/identity/roles/{id}`: actual grants stay visible; none is hidden to
  satisfy an older parser.
- PUT `/api/v1/identity/roles/{id}/grants` and its body: the request union is the same
  closed union.
- GET `/api/v1/identity/self`: the revised parser accepts `federationAlias`.

### Measured, not assumed

Each row edits the reviewed head's served document and runs the unchanged comparison
with the pinned tool version. The first row is the head approved below.

| Variant | Raw | Normalized | Companion |
|---|---|---|---|
| The reviewed head | 5 | 6 | 54 |
| `federationAlias` optional instead of required | 5 | 6 | 54 |
| `federationAlias` absent from both self states | 5 | 5 | 54 |
| New grant branch appended instead of inserted | 5 | 6 | 0 |
| A further assignable key added, closed union, appended | 5 | 5 | 0 |
| The same, with every grant-array bound fixed at 64 | 2 | 2 | 0 |
| Redesigned to open grant keys, from the prior contract | 7 | 8 | 36 |
| Open grant keys, then a further key | 0 | 0 | 0 |

Required markers do not matter. The raw five follow any new assignable key. Appending
removes the positional 54 but not the rest. An open-key contract makes later growth
invisible only after its own approved transition.

### Approved snapshot bytes

SHA-256 covers complete committed `backend/openapi/v1.json` bytes, including
serialization, read from verified Git commits rather than caller or report digests.

| Snapshot | Commit | SHA-256 |
|---|---|---|
| Prior contract | `0c7394c7` | `a32f7813870d729cc17ae6c72fa6a5b291ae5823ba77c6c627fe1e7e0c018b51` |
| Reviewed P03-8 contract | `393e9e8e` | `d81576c0f982344ea344b5aa48b22e06e723976f203628193d345478c53f5f9d` |

The reviewed commit is the last to change the snapshot. Any later change to its bytes
is a new contract and obtains no approval from this record.

### Approved complete findings

Canonicalization is ADR-0096's: sorted keys, ASCII escapes, compact separators, no
duplicate objects, outer array sorted by canonical string, nested order preserved, no
field dropped. An additional field or finding, a removal or a changed value refuses;
only outer finding order and object-key order are immaterial.

| Report | Count | SHA-256 |
|---|---|---|
| Raw tool | 5 | `b0230933312e69a9585f4fd5d98565f429be37ab59613cacae41842c56ab9bb2` |
| Normalized tool | 6 | `a1f1cac89d3c3d2617c35484b84ba6c01a54f7f159cfa0c22790543a077bddc7` |
| Companion policy | 54 | `10949b11b6d56715551c91f2cbac2e70450abc2bffe44612d7aae559598d8a11` |

The actual unchanged comparator and inputs are pinned as ADR-0096 pins them. These are
drift controls under repository review, not protection against an actor authorized to
rewrite the gate and its decision together.

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
A self-reported version or installation receipt is insufficient. Ordinary workstation
file comparisons remain available without approval.

### Gate behavior and carrier

ADR-0096's gate rules hold, with this one generalization.

- Approval exists only in `scripts/openapi-ci.py`'s verified Git-snapshot path. Direct
  `openapi-policy.py` file mode, fixture mode and the first-baseline path never consume
  it. Each invocation requires a fresh output directory.
- A carrier is `backend/openapi/policy/transition-NNNN.json`, named for the Accepted
  `docs/decisions/NNNN-*.md` it restates. Its schema is ADR-0096's: exactly
  `schemaVersion`, `adr`, `baseSha256`, `headSha256`, `findings` and `inputs`.
- Every committed carrier is validated on every run against the tables of the Accepted
  decision it names. An invalid carrier, a missing, ambiguous or non-Accepted decision,
  an unrecognized carrier file name or a repeated (base, head) pair refuses all
  approval, including for another carrier's pair.
- Only the carrier whose pair equals the actual Git snapshot bytes can approve. With no
  match the comparison keeps its unapproved result. A carrier whose pair no longer
  occurs is history and approves nothing else.
- For that carrier the execution identity, the three complete finding sets, the refusal
  summary and the freshly run 60 policy controls apply exactly as in ADR-0096.
- Success identifies an approved incompatibility, not raw compatibility.

### Runtime and coordinated delivery boundary

Snapshot approval covers contract bytes and comparison findings only. The same snapshot
can accompany incorrect handler status, authorization, registry selection, capabilities
or replacement behavior, and neither this gate nor served-snapshot comparison
establishes those or SDK implementation identity.

The final frozen source must separately pass actual API/SDK checks for the default and
selected v2, v3 and v4 registries, actual v4 Role detail, the eleventh capability, the
native self `federationAlias` and complete grant replacement with federation authority
removal or refusal, plus the hosted A/B evidence. Delivery records name the tests.
No other runtime semantic obtains an exemption here.

The maintainer's development-only/no-active-use confirmation is recorded at acceptance,
not inferred from repository searches or version `0.0.0`. The boundary includes
separate internal and operator scripts outside the coordinated bundle. Update
API, SDK, native and web together before exposure. Revert a code deployment only as a
coordinated bundle compatible with the installed data; retained data and schema use
reviewed forward repair. Do not partially revert consumers or advertise zero-downtime
rollout. An unexpected old-parser failure on the changed read surfaces reopens this
decision before exposure continues.

### Open decision: how the closed grant contract grows

Every further assignable permission key will be refused again by the pinned gate
(measured above). This record does not decide how that is handled. Phase 03 gate G20
owns it. The maintainer decides before the first change after P03-8 that adds an
assignable permission key or otherwise alters the grant unions' bytes; P03-9 and P03-10
are the earliest packets that may. The candidates measured here are a transition
record per release as this one, a standing registry-growth rule enforced by the gate,
and an open-key grant contract. Appending new branches (registry order) removes the
positional companion findings under any of them; the API layer reads no Identity release
data today, so that needs its own contract.

## Consequences

### Positive

- P03-8's registry v4, federation administration and self `federationAlias` can finish
  without an unused parallel API major.
- The gate no longer needs editing around each reviewed transition, and every carrier
  is checked on every run.
- Original incompatibilities remain visible and every unexpected finding refuses.

### Negative

- An old consumer cannot use the revised contract. An incorrect usage confirmation or
  mixed deployment can cause legitimate reads to fail.
- Approval binds exact bytes; any later change to the served snapshot needs its own
  decision until the open decision above is resolved.
- Each further assignable key costs a decision until then.

### Neutral

- No permission, grant, DB role, definer, context writer, migration or runtime change is
  added by this decision. Acceptance does not replace actual product, human or
  same-source hosted qualification.

## Implementation Notes

1. Record the maintainer's approval and usage confirmation in the append-only Phase 03
   log with the review disposition; reconcile Status and the decision register (G20).
   Append an ADR-0024 bounded-supersession Amendment and an ADR-0096 Amendment for the
   generalized carrier rule; their bodies and Accepted statuses remain.
2. Generalize the confined admission helper and its controls (delivered in `bac20429`):
   each carrier approves only its own pair; any invalid sibling refuses; duplicate
   pairs, file name against ADR, unrecognized names and reviewed-contract rows are
   controlled.
3. Add the carrier `transition-0100.json` and run the pinned comparison in CI. A local
   reproduction of ADR-0096's three finding hashes validated the method for the hashes
   above; the hosted pinned run is the authority.
4. Reconcile API 04, Testing 06, Permission 19, the served-contract catalogue entry and
   the policy README. Add no rule or status promotion.
5. Complete the independent review rounds, the hosted A/B and the runtime evidence above
   on the frozen source before merge. No merge is authorized by this record.

## Architecture Tests

Extend the existing Git-snapshot and CLI controls in `scripts/test-openapi-ci.py`. A
record beside an unapproved snapshot, an invalid sibling carrier, an unexpected finding
or a failed comparison never creates success. Preserve
`PublicSurface_Contract_Matches_Served_OpenApi` and the actual response and authority
parsers.

## References

- [ADR-0024](0024-api-versioning-policy.md),
  [ADR-0040](0040-ambient-unit-of-work.md),
  [ADR-0096](0096-p035-administration-contract-baseline.md),
  [ADR-0099](0099-confined-tenant-federation.md).
- [API Standards](../standards/04-api-design.md),
  [Testing Standards](../standards/06-testing.md),
  [Permission Standards](../standards/19-permissions.md),
  [served-contract rule](../standards/21-architecture-tests-catalogue.md#publicsurface_contract_matches_served_openapi).
- [OpenAPI CI](../../scripts/openapi-ci.py),
  [admission helper](../../scripts/openapi-transition.py),
  [policy/comparator](../../scripts/openapi-policy.py),
  [snapshot controls](../../scripts/test-openapi-ci.py),
  [policy README](../../backend/openapi/policy/README.md).
- [Phase 03 Status](../roadmap/phase-03-identity-admin.md).
