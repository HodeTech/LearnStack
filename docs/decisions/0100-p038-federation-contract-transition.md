# ADR-0100: P03-8 Federation Contract Transition

## Status

Accepted — 2026-11-18, after the maintainer reviewed the draft and four independent
reviews of it, and after the corrections those reviews led to.

**Date:** 2026-11-18
**Deciders:** @cemil

The maintainer approves the corrected decision and confirms that the application is in
development and not actively used, so no backward compatibility is owed: there is no old
consumer outside the coordinated API/SDK/native/web bundle requiring coexistence for
this transition. The dated acceptance record is the
[Phase 03 maintainer disposition](../roadmap/phase-03-delivery-log.md#adr-0100-maintainer-review-disposition-and-acceptance-2026-11-18).
CI cannot establish this usage fact.

This is one reviewed incompatibility under its own Accepted ADR, as
[ADR-0096](0096-p035-administration-contract-baseline.md)'s was; neither is a general
pre-MVP exemption. The basis recorded here applies only while the application has no
consumer outside the coordinated bundle. It ends at the first of a consumer outside that
bundle, a pilot or production deployment, or a 1.0 release. From then on
[ADR-0024](0024-api-versioning-policy.md)'s versioning and coexistence rules govern
every contract change, this mechanism admits no further carrier, and the maintainer
records the change in the Phase 03 log.

This narrowly supersedes ADR-0024's new-major requirement only for the exact transition
below. It also replaces three statements of ADR-0096's gate rules, listed under
[What this replaces in ADR-0096](#what-this-replaces-in-adr-0096); ADR-0096's pair,
tables and approval are unchanged. ADR-0024 and ADR-0096 stay Accepted. Acceptance does
not resolve gate G20, establish runtime behavior or qualification, or authorize a merge.

## Decision Drivers

- Registry release v4 adds one assignable pair, `Tenant:identity.federation.admin`. The
  administration grant union is closed and every grant-array bound tracks the registry
  ([Permission Standards](../standards/19-permissions.md)), so the pinned OpenAPI gate
  refuses the new branch and the raised bounds, as it did for v3.
- The native self read, `GET /api/v1/identity/self`, carries the host tenant's
  `federationAlias` inside the closed self union
  ([ADR-0099](0099-confined-tenant-federation.md) and its Amendment 6). The gate refuses
  that in its normalized channel; the property, not its `required` marker, causes it.
- ADR-0096's approval is spent. The remote `main` snapshot is byte-identical to its
  reviewed contract, and its carrier cannot be repointed to another pair.
- The maintainer confirms at acceptance (see Status) that no consumer outside the
  coordinated bundle requires a second major.
- Later phases declare further permission keys in their module permission matrices
  (Education, Enrollment, Classroom, Billing, Audit, Platform), so the same refusal will
  recur. This record admits only the P03-8 pair and leaves that recurrence to an owned
  decision below.

## Considered Options

1. **One reviewed P03-8 transition, one carrier per ADR** (selected). Record the
   maintainer's usage confirmation, preserve the incompatibility evidence and admit only
   the exact snapshots, findings and comparison identities. The previous SDK cannot
   parse the revised contract.
2. **Make the P03-8 contract non-breaking** (not selected). Measured below: the five raw
   findings are the registry's growth itself and cannot be removed while the grant union
   stays closed. Hiding the new grant misrepresents state, which ADR-0096 refuses.
   Making `federationAlias` optional leaves the self finding unchanged.
3. **Redesign the grant contract to open keys now** (not selected for this packet). It
   still needs one approved transition (row 7 below) and gives up the SDK's fail-closed
   exact-key parsing. It belongs to the standing-rule decision below.
4. **A standing registry-growth class rule** (not selected here). It would avoid a
   decision per key but changes the gate's semantics and the exception's character. It
   is the owned decision below, not part of this packet.
5. **Disable the gate, remove bounds or merely open enums** (rejected). Removing the
   gate's refusal abandons the one control that makes an incompatibility visible and
   reviewed. Removing bounds or opening enums are redesigns that still need their own
   approved transition (rows 7 and 9 below).

## Decision

LearnStack admits one exact pre-MVP P03-8 federation contract transition under
`/api/v1`. The API, generated SDK, native layer and web application update together
before exposure. The gate retains raw incompatibility and records an explicit ADR-bound
approval only for the snapshot bytes, complete finding sets and comparison identities
below. Every other comparison follows ADR-0024. A passing approved transition is not a
non-breaking result or proof of runtime behavior.

The carrier rule generalizes: each approved transition has its own carrier, bound to the
Accepted ADR it names. A later distinct transition still needs a new ADR and a new
carrier ([ADR-0096](0096-p035-administration-contract-baseline.md) already said so); no
earlier record is repointed for it. Stating the exception as one per Accepted ADR is an
edit to [API Standards § Versioning](../standards/04-api-design.md), made under this
decision.

## Context

### Verified conflict and complete change inventory

The comparison is remote `main` at `0c7394c7` (the merge of PR #44, whose snapshot is
ADR-0096's reviewed contract; a local `main` may lag it) against the P03-8 head. The
head adds eleven `/api/v1/identity/federation` operations, the anonymous
`/api/v1/public/sign-in` GET and HEAD pair, their schemas, registry release v4 with its
tenth assignable pair, the eleventh capability flag `canManageFederation` and
`federationAlias` on both self states. Additions that the pinned tool does not refuse
are not findings.

The pinned comparison refuses these, in three channels:

- **Raw tool, five.** On `GET /api/v1/identity/permissions`: `items` and
  `release.tenantAdminDelta` `maxItems` rise from 9 to 10, and a `oneOf` branch is added
  to `release.tenantAdminDelta.items`. On `GET /api/v1/identity/roles/{id}`: `grants`
  `maxItems` rises from 9 to 10, and a `oneOf` branch is added to `grants.items`.
- **Normalized tool, six.** The five above plus `response-body-one-of-added` on
  `GET /api/v1/identity/self`. The raw tool does not report it: the normalized channel
  inlines references and the tool then sees both inline `oneOf` alternatives as added.
  The incompatibility behind it is the previous SDK's exact-key self parser rejecting
  `federationAlias`.
- **Companion policy, 54.** The new branch sorts into the grant union at index 1, so
  the eight branches at indices 1 to 8 shift, and the sequence's crossing from Tenant to
  Organization branches at index 7 changes a scope too. The positional comparison counts
  nine closed-enum removals and nine additions per occurrence of the union: two response
  occurrences (18 and 18) and one request occurrence (9 and 9).

The previous SDK accepts nine known pairs, adoption versions 1 to 3, exactly ten
capability keys and the self states without `federationAlias`. The revised SDK and BFF
accept ten pairs, version 4, eleven keys and the extra self property. The affected
existing surfaces are:

- GET `/api/v1/identity/permissions`: the selected release view now includes v4.
- GET `/api/v1/identity/roles/{id}`: actual grants stay visible; none is hidden to
  satisfy an older parser.
- PUT `/api/v1/identity/roles/{id}/grants` and its body: the request union is the same
  closed union.
- GET `/api/v1/identity/me/capabilities`: `OwnCapabilitiesDto` is a closed object that
  grew from ten to eleven required flags, so the previous exact-key parser fails on
  every read. The gate reports no finding for it, because added properties are not
  findings.
- GET `/api/v1/identity/self`: the revised parser accepts `federationAlias`.

### Process finding

ADR-0099, Accepted on 2026-11-13, already described registry v4, the eleventh capability
and the self field, but named neither ADR-0024 nor ADR-0096. The break was foreseeable
then and surfaced only at the first hosted run. A decision that adds an assignable
permission key, or a field in a closed contract union, now says in its own
Implementation Notes whether it needs a transition record.

### Measured, not assumed

Each row compares the two named documents with the unchanged comparison and the pinned
tool version, and every edit is to the served document, not to code. "Prior" is the
`main` snapshot below and "reviewed" the P03-8 snapshot. Variants:
"optional" removes `federationAlias` from `required` in both self states; "absent"
removes the property; "appended" moves the federation branch to the end of the grant
union; "a further key" adds a synthetic eleventh pair and sets the four grant-array
bounds to 11 (registry `items`, `tenantAdminDelta`, Role `grants`, `RoleGrantsInput`);
"fixed at 64" sets those four bounds to 64 in both documents; "open keys" replaces the
grant union by an object with `scope` in {Tenant, Organization} and a `key` string of up
to 128 characters.

| Row | Compared (base to head) | Raw | Normalized | Companion |
|---|---|---|---|---|
| 1 | Prior to the reviewed head | 5 | 6 | 54 |
| 2 | Prior to the head with `federationAlias` optional | 5 | 6 | 54 |
| 3 | Prior to the head with `federationAlias` absent | 5 | 5 | 54 |
| 4 | Prior to the head with the branch appended | 5 | 6 | 0 |
| 5 | Row 4's head to itself plus a further key | 5 | 5 | 0 |
| 6 | Row 4's head with bounds at 64 to itself plus a further key | 2 | 2 | 0 |
| 7 | Prior to the head redesigned to open keys, bounds at 64 | 7 | 8 | 36 |
| 8 | Row 7's head to itself with a further key | 0 | 0 | 0 |
| 9 | Open keys at bound 10 to open keys at bound 11 | 3 | 3 | 0 |

Required markers do not matter (rows 1 to 3). The raw five follow any new assignable
key (row 5), and fixed ceilings leave two of them (row 6). Appending removes the
positional 54 but not the rest (row 4). Row 8 is true by construction: an open-key
contract holds no per-key bytes, so a further key changes none. Row 9 shows that this
holds only while the bounds are fixed ceilings and not the registry size. An open-key
contract needs its own approved transition first (row 7).

### Approved snapshot bytes

SHA-256 covers complete committed `backend/openapi/v1.json` bytes, including
serialization, read from verified Git commits rather than caller or report digests. The
commit column is documentation; the gate checks only its grammar, not the commit.

| Snapshot | Commit | SHA-256 |
|---|---|---|
| Prior contract | `0c7394c7` | `a32f7813870d729cc17ae6c72fa6a5b291ae5823ba77c6c627fe1e7e0c018b51` |
| Reviewed P03-8 contract | `393e9e8e` | `d81576c0f982344ea344b5aa48b22e06e723976f203628193d345478c53f5f9d` |

The reviewed commit is the last to change the snapshot. Any later change to its bytes
is a new contract and obtains no approval from this record.

### Approved complete findings

Canonicalization is ADR-0096's: strict JSON (duplicate keys and nonfinite numbers
refuse), sorted keys, ASCII escapes, compact separators, no duplicate objects, outer
array sorted by canonical string, nested order preserved, no field dropped. An
additional field or finding, a removal or a changed value refuses; only outer finding
order and object-key order are immaterial. The raw and normalized sets differ by the
self finding, so their hashes differ.

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
file comparisons remain available without approval. A local build of the same tool
version reproduced all three finding hashes and ADR-0096's three; the hosted pinned run
is the authority.

### Gate behavior and carrier

ADR-0096's gate rules hold, with this one generalization.

- Approval exists only in `scripts/openapi-ci.py`'s verified Git-snapshot path. Direct
  `openapi-policy.py` file mode, fixture mode and the first-baseline path never consume
  it. Each wrapper invocation requires a fresh output directory.
- A carrier is `backend/openapi/policy/transition-NNNN.json`, named for the Accepted
  `docs/decisions/NNNN-*.md` it restates. Its schema is ADR-0096's: exactly
  `schemaVersion`, `adr`, `baseSha256`, `headSha256`, `findings` and `inputs`. A carrier
  is immutable history. A future gate change that needs a different record shape adds a
  `schemaVersion` and keeps accepting earlier versions; it never invalidates an existing
  carrier.
- Every committed carrier is checked on every run for its shape, its file name (the ADR
  number the record names) and a unique (base, head) pair. A malformed carrier, an
  unrecognized `transition-*.json` name or a repeated pair refuses all approval and
  fails the whole OpenAPI job, including for another carrier's pair.
- Only the carrier whose pair equals the actual Git snapshot bytes is bound to its
  decision, exactly one `docs/decisions/NNNN-*.md` that is Accepted and whose tables
  equal the carrier, and can approve. With no match the comparison keeps its unapproved
  result. A retained decision is history: a later change to an old ADR's status or text
  does not affect comparisons of other pairs.
- A pair is approved by one carrier. If a later comparator or tool change invalidates a
  carrier's identity, a second carrier cannot approve the same pair; that needs a gate
  change under its own decision.
- For the matching carrier the execution identity, the three complete finding sets, the
  refusal summary and the freshly run 60 policy controls apply exactly as in ADR-0096.
- Success identifies an approved incompatibility, not raw compatibility.

### What this replaces in ADR-0096

ADR-0096 § Gate behavior and carrier says, and this record replaces:

1. "The sole carrier is `backend/openapi/policy/transition-0096.json`": each approved
   transition has its own carrier; ADR-0096's stays as history.
2. "and additional `transition-*.json` files refuse approval": a further carrier is
   admitted; a malformed or unrecognized one, or a repeated pair, still refuses.
3. "Read the carrier and ADR-0096 from the verified head commit. Require exactly
   Accepted status and bind every carrier digest/count to the corresponding table in
   this Accepted ADR": the gate reads the matching carrier and the decision that carrier
   names, so ADR-0096 is read and bound only when its own pair is compared.

ADR-0096 gets a dated Amendment that points here. Its body is unchanged.

### Runtime and coordinated delivery boundary

Snapshot approval covers contract bytes and comparison findings only. The same snapshot
can accompany incorrect handler status, authorization, registry selection, capabilities
or replacement behavior, and neither this gate nor served-snapshot comparison
establishes those or SDK implementation identity.

The final frozen source must separately pass actual API/SDK checks for the default and
selected v2, v3 and v4 registries, actual v4 Role detail, the eleventh capability, the
native self `federationAlias` and complete grant replacement with federation authority
removal or refusal, plus the hosted A/B evidence. The delivery records name the tests;
they include `BoundedAdministrationHttpTests` (the v4 adoption over HTTP),
`FederationHttpTests`, `IdentitySelfEntryHttpTests` and the SDK
`administration-server.test.ts`. This evidence is required before merge, not before this
acceptance, and no other runtime semantic obtains an exemption here.

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
owns it, together with whether review of the carrier, decision and gate files becomes
mechanical, for example with an owners rule over
`backend/openapi/policy/transition-*.json`, `scripts/openapi-*.py` and
`docs/decisions/`. The maintainer decides before the first change after P03-8 that adds
an assignable permission key or otherwise alters the grant unions' bytes; P03-9 and
P03-10 are the earliest packets that may. The candidates measured here are a transition
record per release as this one, a standing registry-growth rule enforced by the gate,
and an open-key grant contract with fixed ceilings. Appending new branches (registry
order) removes the positional companion findings under any of them. The OpenAPI document
builder hardcodes the union order and the bounds and reads no registry release data, so
that needs its own contract. The gate also admits only non-empty finding rows today, so
a transition whose companion findings are gone needs a gate change.

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
- A second approved transition no longer needs an edit to the gate script. A pull
  request that adds an Accepted ADR, its carrier and the snapshot passes the gate, and
  the gate reads the decision's status from the head commit as ADR-0096's did. Review of
  the ADR and carrier files is therefore the control; G20 owns making it mechanical.
- A malformed carrier fails the whole OpenAPI job, and a pair already approved cannot be
  approved again by a second carrier.

### Neutral

- No permission, grant, DB role, definer, context writer, migration or runtime change is
  added by this decision. Acceptance does not replace actual product, human or
  same-source hosted qualification.

## Implementation Notes

Delivered with this acceptance:

1. The generalized admission helper and its controls (`bac20429`, `0b607611`,
   `79433ea6`): each carrier approves only its own pair; a malformed sibling or a
   repeated pair refuses; a sibling's decision binds only when its own pair is compared;
   file name against ADR, unrecognized, non-ASCII and non-JSON names, reviewed-contract
   rows (lettered phase ids included), table sets, count grammar, chained pairs, the
   wrapper's red exits, head-only reads and the command-line exit code are controlled,
   and every real carrier is bound to its real decision on each run of the controls. It
   was implemented and reviewed before this acceptance, unpushed; ADR-0096 required the
   opposite order, and the Phase 03 log discloses the difference.
2. The carrier `backend/openapi/policy/transition-0100.json`.
3. An ADR-0024 bounded-supersession Amendment and an ADR-0096 Amendment for the
   generalized carrier rule; their bodies and Accepted statuses remain.
4. API 04, Testing 06, Permission 19, the served-contract catalogue entry, the policy
   README and contributor guidance describe two exact exceptions and the carrier rule.
5. The Phase 03 log's dated acceptance record, the Status reconciliation, gate G20, and
   the correction that the OpenAPI self-response transition is this ADR's, not
   ADR-0096's.

Before merge, on the frozen source: the independent review rounds, a hosted pinned run
that reports `ADR-0100 approved incompatibility`, the runtime evidence above and the
hosted A/B. No merge is authorized by this record.

## Architecture Tests

The Git-snapshot and command-line controls in `scripts/test-openapi-ci.py` cover this. A
record beside an unapproved snapshot, an invalid sibling carrier, an unexpected finding
or a failed comparison never creates success.
`PublicSurface_Contract_Matches_Served_OpenApi` and the actual response and authority
parsers are preserved.

## References

- [ADR-0024](0024-api-versioning-policy.md), [ADR-0040](0040-ambient-unit-of-work.md)
  (Amendments 22 and 23, the host-alias reader),
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
