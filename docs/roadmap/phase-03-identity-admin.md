# Phase 03: Identity Domain, Authorization, and Admin Foundation

> **Status (2026-10-28).** P03-0 and P03-1 are complete. P03-1 is merged through
> [PR #38](https://github.com/HodeTech/LearnStack/pull/38); its six implementation
> steps, review corrections and ADR-0075 correction are closed. The
> [merge closeout](phase-03-delivery-log.md#p03-1-merge-and-closeout-2026-10-27)
> distinguishes implementation-source A/B qualification from the final PR head's
> successful protected checks and records the identical squash-merge file tree.
> Phase 03 remains in progress; P03-2 Steps 1–5 and both review rounds per step
> are complete. Step 6 qualifies the final source, inventories and CI evidence. Live membership
> and permission enforcement belong to P03-2; new-user HTTP/BFF activation stays
> in P03-3. This remains the critical-path phase after
> [Phase 02b](phase-02b-events-auth.md). The
> [decision register](#the-decision-register) owns gate state; dated records live
> in the [delivery log](phase-03-delivery-log.md).
>
> | Packet | Title | State |
> |---|---|---|
> | P03-0 | Entry preparation | Complete — [reconciliation](phase-03-delivery-log.md#entry-preparation-2026-10-25) |
> | P03-1 | Identity authority and membership foundation | Complete and merged — [closeout](phase-03-delivery-log.md#p03-1-merge-and-closeout-2026-10-27) |
> | P03-2 | Live membership and authorization | In progress — [Step 6 source freeze](phase-03-delivery-log.md#p03-2-resolution-correction-review-round-2-and-source-freeze-2026-10-28); focused resolution correction, complete discovery and both fresh review rounds pass; complete final-source hosted A/B remains required |
> | P03-3 | Protected Studio and membership administration | Not started |
> | P03-4 | Invitations and bounded delivery | Not started |
> | P03-5 | Custom fields and privacy classification | Not started |
> | P03-6 | Provider event ingress and audit mirror | Not started |
> | P03-7 | Back-channel session revocation | Not started |
> | P03-8 | Tenant federation | Not started |
> | P03-9 | Platform authority and operational recovery | Not started |
> | P03-10 | DSAR and global account closure | Not started |
> | P03-11 | Exit enforcement | Not started |

## Goal

Build LearnStack's identity domain (global users, contextual memberships, roles,
permissions and invitations) on top of
the Keycloak OIDC integration delivered in [Phase 02b](phase-02b-events-auth.md), and
ship the first admin experience.

Authentication itself (password storage, MFA, token issuance, password reset, account
recovery) is owned by Keycloak — see
[ADR-0004](../decisions/0004-authentication-strategy.md) and
[Identity and Authentication](../architecture/13-identity-and-auth.md). The **audit
trail** is owned by `LearnStack.Modules.Audit` — Identity does **not** own an audit
table; it emits MUST-class audit intents that the central pipeline makes durable
([ADR-0033](../decisions/0033-audit-durability-model.md), which supersedes ADR-0016).
The audit module and its capture pipeline were wired in
[Phase 02a Packet 9](phase-02a-kernel-tenancy.md). This phase delivers the
LearnStack-side identity domain on top of those primitives.

The [ownership boundary](#tenant-data-ownership-dsar-boundary-and-pii-classification)
keeps global identity separate from tenant-authored data. This phase makes that
boundary executable before a tenant admin can write a person's attributes.

**P02b-5 handoff — 2026-10-19.**
[ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md) delivers only
the closed tenant API bearer boundary and nonsensitive AccountSite read. This
phase consumes P02b-6 realm issuance and P02b-7 sessions; it does not first wire
those flows. It owns durable subject/actor mapping, real membership, permissions
and permission-sensitive routes. Bootstrap seed mapper emission remains P02b-6;
this phase extends that integration to the live identity/membership lifecycle.

**P02b-6 SDK-boundary handoff — 2026-10-21.** The
[accepted package](phase-02b-delivery-log.md#p02b-6-decision-package-2026-10-20)
uses development Admin REST tooling without a runtime provider SDK. Phase 03
owns the first runtime identity-provider adapter and the
[`Provider_SDK_Types_NotImportedOutsideInfrastructure`](../standards/21-architecture-tests-catalogue.md#awaiting-backfill)
backfill when that adapter introduces an actual SDK. Re-verify the subject
before the first writer; direct HTTP alone does not qualify SDK coverage.
The rule remains Awaiting backfill; the SDK exception rule remains enforced.

Before replacing deny-all membership, qualify affirmative row-7 organization
selection and row-14 claim-only resolution through real HTTP controls. Decide
header-mismatch evidence ownership after a successful claim-only platform-host
resolution: a confirmed claim/membership is not an independently defended tenant
host. Do not silently reuse the host-tenant audit allowance or row-14 membership
refusal signal for that distinct case. AccountSite retains its independent
matching HostScope ceiling. This decision belongs to the first live-membership
packet, before that currently unreachable path becomes exposed.

## Scope

### Packets and decision gates

Each packet is independently reviewable and mergeable. Its decision pass
re-verifies the current code, corpus and pinned provider before accepting its
coupled gates, per [Decision Timing](README.md#decision-timing). A migration,
provider attribute writer or dormant adapter is a first writer too; route
activation is not the only decision boundary.

| Packet | Result | Dependencies | Gates before the first affected writer |
|---|---|---|---|
| P03-0 | Reconciled plan, packet boundaries, decision register and proof ownership | Completed 02a/02d/02b | Documentation preparation only; no new runtime authority |
| P03-1 | Durable identity storage/common creation authority, existing-binding admission, membership/profile/role foundation and fixed seed adoption; no new HTTP | P03-0 | G1, G2 contract, G3, G4 storage/issuance implications, G5 schema/bootstrap, G19; resolve any invitation/closure invariant that changes these first tables under G8/G16 |
| P03-2 | Live membership, permission registry/behavior, runtime default-deny gate, scoped administrative directory read and audit enrichment | P03-1 | G2 execution, G5 enforcement, G6 policy ordering, G7; complete denied controls before protected activation |
| P03-3 | Bounded authenticated web operations, new-user bootstrap/no-session entry, tenant/org switching, member/role screens, organization endpoints and defined member settings | P03-2; production onboarding also waits for P03-9 | G1 activation, G4 transition, G6 transport/UI, G5 delegation; retain existing AccountSite ceiling |
| P03-4 | Secure new/existing invitee acceptance, revocation/resend and usable bounded delivery | P03-1–3 | G8; G1 supplies bootstrap; move delivery/provider gates earlier if this mechanism requires them |
| P03-5 | Customization-owned field definitions, revision-aware values, definition editor and membership form | P03-2–3 | G9; G19 covers diagnostic/PII baseline before any earlier personal-data writer |
| P03-6 | Authenticated durable provider ingress and attributed, deduplicated Audit mirror | P03-1–2 | G10; G13 before any privileged/global intake the chosen mechanism requires |
| P03-7 | Registered, authenticated back-channel logout with bounded provider/session indexes | P03-3 | G11; provider event mirroring is a distinct contract, not a prerequisite by filename |
| P03-8 | Usable tenant SAML/OIDC configuration with safe linking and recovery | P03-3 | G12; G1/G4/G5 still bound issued identity and authority |
| P03-9 | Admitted Platform gate, production first-admin/issuer-rebind authority, killswitch, fenced recovery and dashboard | P03-2 | G13, G14; privileged execution remains closed until this contract ships |
| P03-10 | Subject-scoped DSAR, usable export, tenant erasure and separately authorized global closure/redaction | P03-3, P03-5, P03-9 | G15, G16; G10 only if this workflow consumes provider ingress; integrate all applicable delivered stores |
| P03-11 | Complete source-bound evidence, gateway scaffold correction and analyzer escalation | All implementation packets | G17, G18; no pending mandatory deliverable or unqualified production subject |

The table is a dependency plan, not a total execution order. Federation does not
gate platform recovery; back-channel revocation does not require the Audit mirror.
P03-1 decides shared schema implications without implementing later workflows.
The maintainer places new-user HTTP bootstrap and BFF entry in P03-3. G1/G6
requires provider registration policy, durable creation admission, explicit
session/no-session completion and exact transport outcomes before activation.
P03-1 qualifies the common storage/audit owner and fixed seed adoption, while
existing-session API admission remains route-independent. It opens no new route.
Do not create Invitation, custom-field or DSAR tables there merely for convenience.
If a chosen mechanism needs a later packet's capability, close that gate and
deliver the minimum prerequisite before its first consumer; never introduce a
forward dependency or silently broaden an earlier packet's authority.

#### The decision register

Contract answers use an ADR or bounded dated disclosure where an existing
Accepted record is affected. A changed decision requires its own superseding
record; an existing interface or historical example is not acceptance. Detail
lands in its owning standard/module spec and the packet's dated decision package.
Do not reserve amendment numbers in advance. Split gates close only for the
named part, and the register links each explicit acceptance and vehicle.

| Gate | Question | Existing boundary / required answer | First consumer | Vehicle | Status |
|---|---|---|---|---|---|
| G1 | Who establishes issuer + opaque `sub` → stable `UserId` and proves actor association before an accepted login? | Preserve seeded/provider IDs, reject substitution/email merging, name trusted pre-membership admission, first User-writer audit attribution, concurrency and cross-system reconciliation; self-registration alone grants no membership/session; new-user HTTP activation also requires provider registration and durable creation budgets | P03-1 storage/seed; P03-3 activation | Identity authority ADR; bounded ADR-0064/0070 disclosures if affected; Identity spec | Accepted foundation; P03-3 activation Open |
| G2 | How do membership and permission readers reach data before resolved tenant SQL exists, and how does authorized `app.scope` reach EF/RLS? | Decide exact principal, grants, candidate-scope read and transaction/setter ownership before schema; implement step-5/step-6 handoff later; rows 6/9 still need live authorization; qualify rows 7/10/14 and successful claim-only header-mismatch evidence separately from host-tenant budgets | P03-1 contract; P03-2 execution | ADR-0036/0040 authority; Security/Database standards and closed-set tests | Accepted — 2026-10-27, [ADR-0076/0077 and package](phase-03-delivery-log.md#p03-2-joint-acceptance-and-step-1-2026-10-27); execution proof pending |
| G3 | What are membership/profile/role-binding boundaries, nullable natural keys, lifecycle and global-user projections? | Decide active uniqueness, tenant/org grant composition, profile granularity, archive/reinvite and last-admin/org invariants, same-module composite FKs and Tenancy lifecycle contracts; no cross-module Organization FK/join | P03-1 | Identity module spec; ADR for aggregate/ownership changes; database class declaration | Accepted foundation and [ADR-0074 timing delta](../decisions/0074-membership-admission-under-ordered-locks.md); foundation and per-act lifecycle proofs implemented/reviewed in Step 4; archive/closure transition activation remains P03-3/10 |
| G4 | How are active tenant/org claims issued per session, discovered and switched safely? | Current mappers use user-global attributes and ordinary BFF refresh preserves scope; decide storage implications first, then token reissue, callbacks/hosts, no-membership entry and switch/logout/refresh races | P03-1 implications; P03-3 transition | Provider/session ADR and applicable ADR-0070 disclosure; auth architecture | Accepted storage boundary; P03-3 transition Open |
| G5 | What identifies a scoped permission, who may delegate it, and when do revocations take effect? | Reconcile disabled-default versus Tenant Admin seed expansion; decide registry shape, closed-role defaults, bootstrap/recovery, grant ceilings, last-admin concurrency, finite SystemActor permissions, anonymous Portal Public and API/UI freshness | P03-1 schema/bootstrap; P03-2 enforcement; P03-3 UI | ADR-0017/0064-derived permission contract; Standard 19 and module matrices | Accepted — 2026-10-27, [P03-2 enforcement contract](phase-03-delivery-log.md#p03-2-joint-acceptance-and-step-1-2026-10-27); implementation pending; P03-3 UI/delegation surfaces remain gated |
| G6 | Which protected endpoint/application policies and native web/SDK operations are admitted? | Decide denial precedence/resource lookup first; then exact routes/methods, Origin/CSRF, input/deadline/no-store bounds, ETag/conflict, actor/scope-bound idempotent replay and cache cleanup; no generic proxy or Server Action implied | P03-2 policy ordering; P03-3 transport | API/BFF contract; ADR-0064/0070/0071 disclosures if affected; Standards 04/07 | Accepted — 2026-10-27, [policy/directory slice](phase-03-delivery-log.md#p03-2-joint-acceptance-and-step-1-2026-10-27); P03-3 native/mutation/UI transport remains Open |
| G7 | Who audits HTTP/application/resource denials and supplies trusted actor/request context? | One evidence owner per refusal, MUST sensitive reads, membership/role/platform/operator/route provenance, trusted IP and bounded user-agent; preserve refusal on audit failure; no generic ADR-0065 budget | P03-2 | ADR-0033/0044; Audit spec, enrichment port and matrices | Accepted — 2026-10-27, [ADR-0077 / P03-2 evidence](phase-03-delivery-log.md#p03-2-joint-acceptance-and-step-1-2026-10-27); runtime proof pending; privileged population remains G13 |
| G8 | What makes invitation acceptance and delivery authoritative and recoverable? | Verified current provider email, hashed single-use token, GET inspection only, explicit mutating act, current delegation, accept/revoke/expiry/resend races, aggregate atomicity and bounded delivery independent of Phase 08a | P03-1 only if shared aggregate shape changes; P03-4 writer | Invitation contract; ADR-0042 successor if an exception changes; phase package and delivery handoff | Open |
| G9 | How do field definitions and values preserve schema/classification history? | Decide revision pins/active uniqueness, scalar-field compatibility with the existing admitted object profile, nested data, required/default/retire/reclassify/key-reuse behavior, consent and supported localized forms; no raw JSONB bypass | P03-5 before its schema | Customization/privacy contract; ADR-0043 disclosure or successor if needed; module specs | Open |
| G10 | What authenticated durable provider ingress is selected, and how are global events attributed? | Name producer, acknowledgement, stable provider-event dedup, ordering, tenantless/multi-membership routing, privacy and retention; enqueue through outbox, never direct business publication; no invented tenant or privileged app consumer | P03-6, or an earlier mechanism needing ingress | Provider-event ADR; ADR-0046/0057 boundaries, exact grants and Audit subscription | Open |
| G11 | Which back-channel token profile, route and session targets are supported? | Separate OIDC purpose validation, signature/issuer/audience/time/events/no-nonce, replay retention, provider/session indexing and reachability; preserve late-work/accounting fences and unrelated/new session owners | P03-7 | ADR-0070 bounded extension; native route/provider configuration package | Open |
| G12 | How is realm-level tenant federation confined and safely activated? | Provider ownership, write-only credentials, bounded egress, linking/verified-email trust, MFA assurance, test/activate/rollback and last-login recovery; email/broker claims alone grant no membership | P03-8 | Identity-provider contract under ADR-0004/0064; permission/audit matrices | Open |
| G13 | How does the operator population authenticate a specific Platform act and retain human attribution? | Resolve ADR-0036's internal-chain rule and undecided platform URL space, tenant/Hub realm separation, no-Hub baseline, service versus human actors, MFA/reason, denied-entry evidence, production first-admin/provisioning and exceptional recovery, controlled issuer rebind and retained-connection lifetime; a new Hub crossing needs a coordinated ADR | P03-9 or first earlier privileged consumer | Operator authority ADR; bounded disclosures/supersession in affected Accepted records; Platform gate contract | Open |
| G14 | Which dashboard/replay/job-recovery transitions and grants are allowed? | Separate producer reset from targeted subscriber replay; exact permission, reason, stale fences, MUST result audit and runbook; immutable facts and uncertain external reservations survive; dated ADR-0015 disclosure before dashboard admission | P03-9 operational writer | ADR-0046/0047 recovery contract; ADR-0015 disclosure and bounded migrations | Open |
| G15 | Who may request which person's data in which tenant/org scope and for which recipient/purpose? | Separate self/admin/delegate authority, subject predicates and org boundaries; category is not disclosure authority; resolve third-party data, holds, eligibility and post-erasure status access | P03-10 request writer | Data Protection/Identity request contract and module matrices | Open |
| G16 | How do exports, closure and privileged redaction finish durably? | Decide artifact delivery/reauthorization/expiry, job/storage prerequisites, participant inventory/write fence, last-membership case and provider finalization; exact erasure grants/order/immutable references, user-reference locators and result-audit/inbox reconciliation | P03-1 only for shared closure-fence invariants; P03-10 workflow | Data Protection and ADR-0044/0046 privileged contract; migrations, module participation and phase package | Accepted shared fence; P03-10 workflow Open |
| G17 | Which real production subjects and product journeys prove complete phase exit? | All deliverables/criteria, inherited catalogue clauses, no skips/empty censuses, real provider/native/app-role paths, manual accessibility and complete source-bound CI; analyzer escalation stays at exit | P03-11; packets retain their own proof | Phase package, evidence index, catalogue and existing test owners | Open |
| G18 | Which gateway scaffold corrections ship without activating the gateway? | Apply Architecture 30 Rules 1–3: explicit version paths/priorities and separated credentials; retain disabled OIDC; reviewed per-public-read allowlist, lint and activation stay Phase 11 | P03-11 config writer | Phase package and Architecture 30; ADR-0035 trigger unchanged | Open |
| G19 | How is personal-data classification and diagnostic redaction established before the first identity writer? | Census current PII-bearing subjects and missing-annotation control; distinguish snapshot markers from logs/errors; fixed descriptors and value canaries, including arbitrary nested values; custom-field history remains G9 | P03-1 first personal-data writer; P03-5 extends it | Data Protection, ADR-0044/0057 boundaries and module PII/audit specs | Accepted; current/global/contextual model classification and actual Identity diagnostic sink controls implemented and reviewed; custom-field history remains G9 |

### Entry and activation boundaries

- P03-1 opens with G1/G2/G3/G19 and the schema-affecting parts of G4/G5.
  Resolve G8/G16 in that pass only where a different answer changes those tables.
- If bootstrap requires provider ingress, privileged access or a new global-audit
  path, the affected G10/G13/G7 part also closes before that first writer. Move its
  minimum implementation earlier; an unresolved tenant or future packet grants
  neither an audit exemption nor an implied bypass.
- A gate's question remains Open until explicit acceptance links its vehicle.
  Preparation does not replace the packet's decision pass or authorize code.
- Preserve the closed tenant bearer profile, AccountSite host ceiling, public
  anonymous reads, canonical pipeline, four-role model and existing setter set
  until the relevant bounded decision and enforcement ship together.
- New Identity/global tables declare their database class, principal and grants;
  calling a table global does not make an app-role directory or platform bypass.
- A packet adds its module specification, permission/audit matrix and catalogue
  subjects with its first implementation; no test-only subject establishes a
  production capability. Do not invent module-loader interfaces from sketches.

### Identity Model

The accepted [Identity module contract](../modules/identity/README.md#aggregate-and-storage-contract)
owns exact tables, keys, lifecycle and contained state:

- `User` is the permanent global application binding under
  [ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md), without an
  email/verification or unused global-profile mirror. P03-1 supplies fixed seed
  adoption and existing-binding admission; new-user HTTP/BFF entry remains P03-3.
- `Membership` is a contextual root with a stable ID, current null-safe natural
  uniqueness, a contained profile and assignments. Revoked memberships retire;
  rejoin creates a successor under
  [ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md).
- `MembershipProfile` holds contextual display name and locale preference. P03-3/G6
  decides self/admin DTOs; P03-5/G9 owns dynamic fields and revision history.
  No dormant contact/custom-field column or editable consent Boolean is admitted.
- `Role` is a tenant-wide template with Tenant or Organization capability scope;
  assignments derive exact organization authority from Membership. Platform
  authority remains P03-9/G13.
- Permission definitions and finite foundation grants belong to
  [Permission Standards](../standards/19-permissions.md#identity-foundation-contract).
- `Invitation` remains a P03-4/G8 membership offer; its first writer requires that
  packet's accepted contract.

There is no global `UserProfile` aggregate. Tenant-authored profile data belongs
inside Membership; later mirrors and workflows require their named gates.

> **Audit ownership.** The `AuditEntry` aggregate lives in the **Audit** module, not in
> Identity. Identity's commands flow through the shared `AuditLogBehavior`, and
> MUST-class rows are written on the same transaction as the business write per
> [ADR-0033](../decisions/0033-audit-durability-model.md). Cross-process identity
> signals (Keycloak webhooks) arrive as integration events. There is **no**
> Identity-owned audit table.

> **Organization ownership.** The `Organization` aggregate and its CRUD endpoints live
> in the **Tenancy** module, not in Identity, per [ADR-0017 Amendment 2
> (2026-08-10)](../decisions/0017-tenant-organization-hierarchy.md) — which resolves the
> "Identity / Tenancy" wording ADR-0017 shipped with. Identity owns Keycloak
> `organization_id` attribute mapping, the `Membership` extension for org-scoped role
> assignments, and JWT claim emission. `Membership` holds `OrganizationId` by value from
> `LearnStack.SharedKernel`: no navigation property, no join, no foreign key.
>
> **This phase still delivers those endpoints** — `Organization` create / read / update
> / archive at tenant-admin scope, under `tenancy.organization.*` ([Permission
> Standards](../standards/19-permissions.md)) — authored in
> `LearnStack.Modules.Tenancy.Application` against the Packet 6 aggregate. Only the
> owning module moved; the phase did not. This phase builds the organization switcher.
> Phase 06 builds organization list / detail / create UI on these endpoints.
> G3/G5 settle the org-admin own-org variant before exposing it; the tenant-wide
> read hatch does not widen organization UPDATE/DELETE authority.

### Tenant Data Ownership, DSAR Boundary, and PII Classification

The [Customization model](../architecture/32-tenant-customization-model.md#5-custom-fields-on-built-in-entities)
already places membership fields on `membership_profiles`, never global `users`.
The [Data Protection boundary](../architecture/23-data-protection.md#right-to-erasure-right-to-be-forgotten)
already separates tenant requests from global closure. This phase implements both.

The [Data Protection classification census](../architecture/23-data-protection.md#identity-foundation-and-classification-ownership)
enforces explicit declarations for every mapped EF scalar property, with a
planted neutral-name omission control. SQL-only, control and vendor stores retain
their separate inventories. Dynamic-value classification additionally needs
revision-aware definitions under G9; the census does not infer PII from stored
text or authorize disclosure to a recipient.

The [accepted P02d-2 G17 answer](phase-02d-delivery-log.md#p02d-2-accepted-answers)
and its writer already supply whole-value `[PiiSensitive]` on `TenantSetting.Value`.
This phase consumes that baseline for identity-backed access and DSAR; a public
theme allowlist never permits exporting arbitrary tenant settings. Marketplace
purpose/recipient decisions remain subject to the Proposed boundary below.

**Attribute ownership.** Each attribute has exactly one owner, and the owner determines
the table it lives in and who may write it.

| Attribute class | Owner | Storage | Writable by |
|---|---|---|---|
| Provider credentials, MFA factors, token issuance | Keycloak | Keycloak realm `learnstack` | Keycloak only |
| BFF token custody | Native web session owner | Process-local store under [ADR-0070](../decisions/0070-owned-tenant-bff-sessions.md) | The closed BFF owner only |
| Global application binding and admission lifecycle | Identity under [ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md) | `users` and the deployment authority under the [Database contract](../standards/05-database.md#identity-storage-contract) | Admitted creation owner; global closure remains P03-10/G16 |
| Canonical provider email/verification mirror, if selected | Provider source | No P03-1 mirror storage; P03-6/G10 or an earlier proven consumer decides fields/source/freshness | Its separately admitted mirror owner; never email-based identity merging |
| Roles and permissions inside a tenant | Tenant | Membership/role bindings under G3/G5 | Admin within the decided delegation ceiling and scope |
| Contextual display name and locale preference | Tenant / membership context | Contained `membership_profiles` under the [Identity contract](../modules/identity/README.md#lifecycle-profile-and-admission) | Closed seed owner; P03-3/G6 decides self/admin edits |
| Consent evidence | The person for the stated purpose | Append-only contract in [Data Protection](../architecture/23-data-protection.md#consent-management) | G3/G9 decide references and authorized capture; a profile edit cannot fabricate consent |
| Tenant-defined custom fields | Tenant | P03-5/G9 decides revision-aware Membership values; no foundation column | P03-5/G9 decides authorized writers |
| Learning behaviour (progress, attempts, attendance) | Tenant | per-module tenant-owned tables | The owning module |

Two rules make the table enforceable:

- **`users` carries no `custom_fields` column and no tenant-authored column of any
  kind.** The supported target is `Membership`, with values in
  `membership_profiles`. `User` is not a new-write target. G9 must explicitly
  identify any legacy label/migration input before accepting it; it never stores
  a value on `users`. Definition revisions preserve existing value interpretation.
- **`MembershipProfile` is `[TenantOwned]` and `[OrganizationScoped]`**, with an EF
  global query filter and a Row Level Security policy built from the canonical template
  in [Database Standards](../standards/05-database.md). It is an ordinary tenant table
  and gets the ordinary four layers of defense
  ([ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md)).

**DSAR boundary.** A data subject access request is scoped to a tenant unless the
person themselves asks for the account itself.

**Proposed marketplace impact (2026-10-01).** This institution-service contract does
not assign central order history or marketplace consent to a tenant membership.
[ADR-0049](../decisions/0049-institution-sites-and-course-marketplace.md) requires a
separate purpose-based privacy/export/erasure contract, coordinated with
[Data Protection](../architecture/23-data-protection.md#processor-agreements), before
marketplace identity/order/support writers. No such scope is approved in Phase 03
by this note.

| Request | Initiated by | Scope | Effect on other tenants |
|---|---|---|---|
| Tenant-scoped export | Authorized admin, or the person inside that tenant | The named subject's data within authorized tenant/org and recipient/purpose boundaries | None |
| Tenant-scoped erasure | Authorized admin, or the person inside that tenant | The subject's authorized memberships/profiles and behavior, with eligibility/retention constraints | None |
| Global account closure | The person through a separately admitted account request | Global identity/provider finalization and all membership participants under G16 | All of them, by design; execution requires separate platform authority |

- A tenant-scoped export is produced by running the export handlers **inside that
  tenant's `ITenantContext`, connected as `learnstack_app`**. RLS bounds tenant/org
  isolation; every module also applies the named subject and permitted disclosure
  predicates. A second person in the same tenant must not appear in the bundle.
  G15 separates self/admin/delegate requests and prevents an org admin from
  requesting another branch's data. A learner needs a self-service path without
  Studio administration rights, including a scoped status receipt after erasure.
- A tenant-scoped erasure removes the `Membership` and its `MembershipProfile`,
  anonymises that tenant's behaviour rows, and redacts the permitted human-facing
  audit fields while keeping the action record. Audit identifiers and causal
  metadata remain subject to the first-writer decision below. Tenant erasure
  preserves the [permanent global binding/tombstone](../architecture/23-data-protection.md#identity-foundation-and-classification-ownership).
- **Global account closure has no tenant-admin endpoint.** The person authorizes
  the request without receiving a Platform permission. G13/G16 define the
  privileged executor, stable participant inventory and write fence against new
  memberships, delayed mirrors and pending invitation acceptance. It fans out
  `UserAnonymisationRequestedV1` and reconciles Data Protection's completion
  workflow; request creation or event emission alone is not completion.
- The audited `EnterPlatformAdminScope(reason)` from
  [Phase 02a Packet 7](phase-02a-kernel-tenancy.md) is the only path that reads across
  tenants during either flow, and every use of it is MUST-class audited.

**Durable-copy prerequisite.** This phase still implements tenant-scoped DSAR
export/erasure, separate global closure, subject/actor mapping, request authority
and audit redaction. It consumes the canonical
[durable asynchronous store inventory](../architecture/23-data-protection.md#durable-asynchronous-store-inventory)
and its [first-writer prerequisites](../architecture/23-data-protection.md#erasure-authority-and-first-writer-prerequisites).
Before the first affected writer, settle a bounded decision for surviving
identifier/payload/audit references, exact principal and minimum grants,
immutable facts, dependency/deletion order, replay/deduplication floors and
durable completion. Ordinary app-role/SystemActor delivery or dispatcher
credentials authorize no privileged erasure. The existing inbox, delivery and
job grants provide no generic Platform purge; operator authority and erasure
do not prove external quiescence or reservation release.

[Phase 11](phase-11-production-hardening.md#data-operations) extends these
workflows across later modules and retained copies, with purge/retention,
backup/provider/diagnostic and operational reconciliation. That later work does
not defer this phase's DSAR or redaction implementation.

**PII classification on JSONB.** Every `TenantCustomFieldDef` row carries a required
`pii_category` drawn from the closed set in
[Data Protection § Personal Data Inventory](../architecture/23-data-protection.md) —
`PII-Identity`, `PII-Behaviour`, `PII-Sensitive`, or `None`. There is no default; the
Studio editor forces a choice at definition time, and the definition cannot be saved
without one.

- Export, redaction and retention use the value's applicable **definition revision**,
  subject and recipient/purpose contract. PII category alone grants no export
  authority. Retired definitions or reclassification cannot orphan existing values
  or make them safe to log. G9 decides nested values, key reuse and migrations.
- Log and error-tracking redaction is category-driven. `SensitiveTokenCatalog` from
  [Phase 02a Packet 3](phase-02a-kernel-tenancy.md) matches on property **names**, and
  the names inside `custom_fields` are authored by tenants at runtime, so it cannot
  reach them. Custom-field values are redacted by their declared category before a log
  event or an error-tracking payload is constructed.
- `Payment` is **not** an allowed category for a tenant-defined field. Payment data
  carries a seven-year retention obligation that a tenant-authored JSONB key cannot
  honour; a definition attempting it is rejected at save time with
  `Result.Fail(business_rule_violation, …)`.

Three checks make this mechanical, registered in
[Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md) under
its `Subject_Constraint` convention:

- `User_Aggregate_Has_No_TenantScoped_Columns` — architecture test over the `users`
  EF configuration.
- `Every_TenantCustomFieldDef_Declares_PiiCategory` — architecture test over the
  aggregate's invariants plus a migration scan for a `NOT NULL` `pii_category`.
- `Tenant_Scoped_Export_Contains_No_Foreign_Tenant_Rows` — integration test that runs
  an export for a person holding memberships in both seed tenants, as `learnstack_app`,
  and asserts the bundle is single-tenant.

### Tenant Custom Fields

`TenantCustomFieldDef` lands here rather than in
[Phase 02a Packet 8](phase-02a-kernel-tenancy.md), which now ships only
`TenantContentType` and `TenantLevelTaxonomy`. The aggregate's first consumer is the
membership admin screen in this phase, and its correctness depends entirely on the
ownership split above — shipping the table three packets before the rule that governs
what may be written into it invites exactly the leak this phase closes.

- Customization owns `TenantCustomFieldDef`; Identity consumes its application
  contract and owns membership values. G9 decides immutable definition revisions,
  target-qualified version identity, one active definition per concept and value
  pins before the first schema. Include required `pii_category`, field schema and
  `is_required` / `sort` metadata without an unversioned unique key blocking history.
- Allowed `target_entity` values **in this phase**: `Membership` only. The remaining
  targets light up with their owning phases —
  `Course` and `Lesson` in [Phase 05](phase-05-education-learning-content.md),
  `Enrollment` in [Phase 07](phase-07-enrollment-learner-portal.md),
  `LiveSession` in [Phase 08c](phase-08c-classroom.md). A definition naming a target
  whose phase has not shipped is rejected at save time rather than silently stored.
- Values are validated against the field's JSON Schema on every write, and rejected
  through `Result.Fail(validation_failed)` — never by throwing.
- The existing `IJsonSchemaValidator` admission requires a nonempty root
  `properties` object. G9 decides how field schemas use that profile or a separately
  approved bounded extension; scalar fields cannot bypass admission. It also
  decides required-field introduction, null/default/unknown-key behavior, schema
  retirement, consent for sensitive values and supported localized form types.
- Permission keys per [Permission Standards](../standards/19-permissions.md):
  `customization.custom_field.admin` (Tenant scope) to define fields;
  `identity.membership_profile.write` (Tenant / Organization scope) to set their
  values under the ADR-0073 profile permission contract; G9 still owns
  dynamic-value activation.
- Studio surface: a definition editor and a schema-driven form on the membership detail
  page. The visual schema builder is [Phase 06](phase-06-renderer-admin-studio.md).

### Authentication Integration

P02b-7 implements [ADR-0070](../decisions/0070-owned-tenant-bff-sessions.md).
Before immediate account-disable/sign-out-everywhere is promised, this phase
owns authenticated OIDC back-channel logout ingress, signature/audience/time/
replay validation and provider-session indexing into `BffSessionStore`.
It also owns authenticated provider event ingress/mirroring through the existing
identity-event/audit path. Earlier development provider storage is not a durable
LearnStack session ledger; local-only expiry/outage acts retain that limitation.
If the API needs a distinct indeterminate-authentication outcome or trusted
refusal witness, this phase owns that separate contract decision and its BFF
consumer proofs. Until then, preserve
[ADR-0071](../decisions/0071-uncertain-account-site-refusals.md)'s status table;
provider errors or challenge text cannot manufacture a trusted distinction.

Keycloak owns credentials, password reset, email verification, and MFA. This phase
wires LearnStack into that flow:

- Consume the existing strict API token validation and native BFF session owner
  from [Phase 02b](phase-02b-events-auth.md), including opaque HttpOnly cookies,
  on-demand refresh and local-first provider logout. Extend its membership and
  permission integration here rather than implementing session custody again.
- Post-login membership lookup: resolve memberships for the authenticated user and
  surface the active tenant via a host + claim cross-check.
- G1 establishes required actor claims before the accepted callback needs them;
  no first-login mirror relaxes the API profile. G4 decides discovery for zero or
  multiple memberships and session-specific scope issuance. Editing a shared
  provider user attribute is not evidence of independent A/B sessions: both must
  keep their own scope through real refresh. Switching preserves host-only cookie,
  callback/return allowlists and late-publication fences.
- Tenant-specific Keycloak federation surface in Admin Studio (configure SAML / OIDC
  IdP per tenant), delivered under G12 with a confined runtime adapter. Broker
  ownership, secret write-only handling, bounded egress, safe account linking,
  MFA trust, test/activate/rollback and last-login recovery are part of delivery.
- Durable issuer + `sub` to `UserId` mapping under G1, with retry/concurrency and
  provider/DB reconciliation. Existing seed IDs and credentials remain preserved.

LearnStack does **not** implement password hashing, password reset email rendering,
or provider brute-force protection; Keycloak owns those capabilities and token
issuance/rotation. The native BFF owns custody of the issued access, refresh and
ID tokens under ADR-0070; no application identity table stores them.

### Authorization

**Runtime gate before protected dispatch.** The API runtime classifier and
`AuthorizationBehavior` enforce the explicit HTTP/application policies selected
by [ADR-0076](../decisions/0076-live-scoped-authorization.md). Missing classifications
and insufficient authority refuse before dispatch; the endpoint census remains an
independent structural check. Explicit public reads remain anonymous. The
Status block above owns final-source qualification.

The gate's real HTTP controls must show that an unclassified protected endpoint and
an authenticated principal without the required permission cannot dispatch the
handler. Keep the endpoint census as an independent structural check, alongside
tenant membership, permission and resource-scope enforcement.

G2 settles pre-context app-role lookup and the authorization/transaction handoff:
the resolver asks membership before installing context, and authorization is step
5 before the ordinary step-6 transaction. Neither a normal unresolved repository
nor an unrecorded bypass/new GUC setter supplies that capability. A matching
host/token on rows 6/9 does not exempt a protected act from live membership checks.
G5 specifies the committed-revocation boundary, already-admitted work, delegation
ceilings, recovery/bootstrap and finite SystemActor permissions. Realm-role claims
and provider token refresh alone cannot establish application permission freshness.

- Role-based authorization.
- Permission-based authorization.
- Tenant-scoped permission checks (`Membership.Roles → Permissions`).
- Resource-scoped policies (for example, an instructor edits only their own courses).
- Admin and Studio route guards.
- API authorization policies.
- Every endpoint carries `[Authorize(Policy = …)]` or `[AllowAnonymous]` with its
  one-line reason, as
  [Permissions Standards § HTTP endpoints](../standards/19-permissions.md#http-endpoints)
  requires, and an architecture test registered in the catalogue fails the build for an
  endpoint that carries neither — the anonymous reads
  [Phase 02d](phase-02d-walking-skeleton.md) ships included.
- Permission keys for those public reads, granted to the `Portal Public` role of
  [Permissions Standards § Built-in Roles](../standards/19-permissions.md#built-in-roles).
  G5 supplies that anonymous capability model without requiring a stored membership
  or promoting `HostOnly` to protected authority.
- **Lights up the [Phase 02a Packet 3](phase-02a-kernel-tenancy.md)
  `AuthorizationBehavior` shell** — resolves each command's `[Authorize(Policy)]`,
  calls `IAuthorizationService.AuthorizeAsync` against the tenant +
  organization-scoped resource, and returns `Result.FailFor<TResponse>(forbidden)` on
  deny, per
  [ADR-0032 § Sub-decision 2](../decisions/0032-exception-handling-logging-and-observability.md).

- **Owns the `app.scope` carrier**, parked here by Phase 02a Packet 7. The
  cross-organization read hatch in the canonical Row Level Security policy is reachable
  only when a session sets `app.scope = 'tenant'`, and that flag derives from the actor's
  **role** plus a declared tenant-wide operation — never from a header, query parameter,
  cookie or body, and unreachable under `TenantContextOrigin.HostOnly`
  ([ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md)). `Membership` and
  `Role` land in this phase, so this is the earliest phase that can supply the derivation;
  [Phase 02b](phase-02b-events-auth.md)'s authenticated principal is the prerequisite, not
  the carrier. Placement is unchanged and already fixed:
  [Security Standards § Tenant Context](../standards/11-security.md).
  `Tenant_Scope_Widening_Is_Never_Set_From_Request_Input` becomes non-vacuous here.

- **The Platform-scope permission, and the writer it unlocks.** `EnterPlatformAdminScope`
  is gated by `IPlatformAdminGate`, registered since
  [Phase 02a Packet 9](phase-02a-kernel-tenancy.md) as `DenyAllPlatformAdminGate`, which
  refuses everyone — so every operation that needs the scope is unreachable, every
  killswitch toggle included. This phase replaces it with a gate that reads a
  Platform-scope permission, and ships with it the killswitch toggle command, its
  permission key and its incident runbook: the writer Packet 9 withheld on purpose
  ([ADR-0045 Amendment 1 § 4](../decisions/0045-entitlement-and-feature-flag-socket.md)).
  [Permissions](../standards/19-permissions.md) reserves no killswitch or entitlement key
  yet, so reserving them is part of the work. The toggle's row is already classified —
  `tenancy.killswitch.toggle`, `(off-path)` `(planned)`, MUST, in the
  [Tenancy matrix](../modules/tenancy/audit.md).

- **Audited job quarantine inspection and reconciliation.** ADR-0047 assigns this
  phase the operator permission, evidence review and recovery surface. P02b-4
  deliberately freezes quarantined job/attempt and recurrence/control facts for
  every current role. Before implementing recovery, settle its bounded decision
  and add a new migration for the exact trigger transitions and grants; preserve
  immutable recorded evidence. Capacity release requires positive completion,
  no-start or quiescence proof. Operator authority, elapsed time and a missing
  worker alone cannot release reservations or authorize another external start
  ([ADR-0047](../decisions/0047-transactional-background-job-runtime.md#external-profile-and-quiescence)).

Authorization is the third layer, not the first. A permission check that passes still
runs under the tenant's `ITenantContext` and under Row Level Security; a deny is a
better error message, not the isolation boundary.

### Protected web and API operations

The delivered authenticated caller accepts only `GET /api/v1/account/site`.
P03-3 extends native credential custody through exact generated SDK operations
under G6; the public caller stays anonymous. A generic proxy, browser bearer,
Server Action or tenant/org selector is not admitted by adding an admin screen.

- Decide endpoint/application/resource policy ordering before activation. HTTP
  denials occur before the MediatR audit frame; G7 assigns their evidence owner.
  Application policy metadata respects module/core dependency boundaries.
- Define route/method/input limits, verified host/source, Origin/CSRF, no-store,
  deadline/cancellation and closed error/recovery outcomes, preserving
  [ADR-0071](../decisions/0071-uncertain-account-site-refusals.md) unless explicitly
  changed by the packet's decision.
- Use cursor pagination/indexed member projections and ETag/If-Match for edits.
  Idempotent side effects retain authorization on replay; tenant-wide key storage
  must not let another actor replay sensitive responses or bypass revocation.
- Key protected client/server state by applicable actor, tenant, org and locale;
  fence late responses and clear stale protected state on switch/logout.
- Deliver empty/loading/error/conflict states, scope labels and deliberate
  unsaved-edit handling. Apply localization and manual keyboard/focus/form/error/
  reflow/contrast checks to new screens now; the full Playwright/axe expansion
  remains [Phase 06](phase-06-renderer-admin-studio.md).

### Operator recovery and dashboard admission

P03-9/G13 also owns the production first-admin and controlled issuer-rebind
authority defined by ADR-0072/0073. Before production onboarding,
qualify an admitted operator creating the finite Role templates in separate
transactions, then the first tenant-wide admin Membership with MUST audit.
Specify authorized retry after partial setup, wrong-tenant/operator denial,
unknown-COMMIT reconciliation and recovery without the development seed
capability. Tenancy provisioning remains its Tenant/default Organization pair.
An issuer rebind needs proven provider continuity and frozen admission, preserving
AuthorityId, actor IDs and tombstones; no production rename path exists before
this contract is accepted and delivered. Seed-backed P03-3 journeys may be
qualified earlier; they are not production onboarding proof.

Phase 02b supplies internal targeted subscriber replay and no operator route.
This phase owns the operator outbox/dead-letter/subscriber-replay and job-recovery
surface. Before its first writer, settle the URL space under ADR-0036, exact
Platform-scope permission, reason, fenced transition and MUST audit/runbook;
producer reset is not an implied power of targeted subscriber replay.

This phase also owns the application `/admin/hangfire*` mapping and its
Platform-scope permission. Append the dated ADR-0015 disclosure before replacing
its historical realm-role gate. Keycloak roles are not LearnStack authority.
Keep the route absent until the application gate and denied-route controls ship.
Phase 11 owns APISIX defense in depth and gateway activation.

The first Platform-admin consumer must decide denied-entry evidence and enforce
connection lifetime: a connection retained after scope completion must not permit
an autocommit bypass. The current deny-all gate exposes no privileged consumer;
an admitted scope's entry row is not evidence of a denied attempt.

### Gateway Configuration Corrections

G18 applies [API Gateway Rules 1–3](../architecture/30-api-gateway.md#4-route-table)
to the committed `infra/apisix/apisix.yaml` scaffold. The architecture example
already declares explicit priorities and removes the historical Hub-secret
reference. The scaffold has priorities and no such reference, but still uses the
version-wildcard spelling Rule 1 rejects. Its OIDC block remains disabled.

- Use explicit version paths and method sets, retain explicit priorities and keep
  the gateway independent of the Hub HMAC secret. The bearer-only target needs no
  substitute code-exchange client secret under Architecture Rule 3.
- Do not claim HMAC possession alone defeats the internal chain: compromising
  that secret permits forged body signatures; accepted mTLS and service JWT
  checks remain independent requirements under
  [Security Standards](../standards/11-security.md#hub-contract-surface).
- [Phase 11](phase-11-production-hardening.md) owns the reviewed route for every
  enumerated public read, anonymous-over-catch-all precedence, route lint and
  gateway activation. This phase corrects the scaffold without enabling OIDC or
  copying the architecture's full target table into a live configuration.

The APISIX adapter's owning phase and
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md) trigger are unchanged.

### Invitation Flow

G8 closes before the Invitation schema or acceptance writer. G1 supplies the
actor/bootstrap contract; a new invitee cannot already be assumed to hold the
membership they are accepting.

- An authorized admin offers email + role + organization within their current
  delegation ceiling. Bind a random single-use token to a stored one-way verifier;
  a prefilled email is not proof of the authenticated person's current verified
  provider email. G8 decides exact matching and email-change behavior.
- GET inspects an offer without accepting it. The admitted mutating act uses the
  chosen authenticated continuation and CSRF/transaction binding. Keep tokens out
  of diagnostic payloads and arbitrary return URLs.
- Define pending/accepted/revoked/expired/resend states, default 14-day expiry,
  existing-member behavior, current inviter grant/org lifecycle checks and
  accept/accept, accept/revoke and accept/expiry races.
- Membership/grant creation and invitation consumption need one authoritative
  outcome. G8 decides aggregate boundaries and atomicity/reconciliation under
  [ADR-0042](../decisions/0042-tenant-provisioning-cross-aggregate-transaction.md);
  the ambient transaction alone grants no new multi-root exception.
- Deliver a usable bounded invitation handoff in this phase. G8 decides its
  email/provider or explicit admin-handoff mechanism, delivery/retry/idempotency
  and truthful queued/sent/failed/resend UX. Do not wait implicitly for Phase 08a,
  display a fake sent result or put raw email/token data in identifier-only events.

An invitation grants authority in one tenant/org only. It does not let an admin
edit global `User` or provider attributes; G1's trusted mirror is separate.

### Admin Foundation (identity-management screens only)

This phase ships the identity-management surface in Admin Studio. CMS/media
screens belong to Phase 04 and catalog screens to Phase 05; Phase 06 owns full
renderer/Studio expansion and the
[screen ownership table](phase-06-renderer-admin-studio.md#admin-studio--screen-ownership).

- Login (delegates to Keycloak).
- Tenant switcher for people with memberships in multiple tenants, and the
  organization switcher. Platform-operator admission is separate under G13.
- Users list and detail, scoped to the acting tenant and organization.
- Roles and permissions management.
- Invitations.
- Custom field definitions and the schema-driven membership form.
- Membership profile/preferences under the decided self/admin field contract.
- Tenant-scoped data-request surface: request an export, request an erasure, view
  status. Global account closure is deliberately absent from this surface.
- Self-service data requests/status for a learner without admin permissions, plus
  the separate person-authorized global closure surface under G13/G15/G16.
- Identity-backed admission for existing Tenancy locale/branding commands is
  named explicitly in the packet package; no new route is implied by a permission
  registration. Full tenant-setting/branding editors remain Phase 06. Generic
  setting or provider-secret authoring is not implied by member preferences.

### Audit Coverage Wiring

The owning Identity, Tenancy and Customization commands declare the following
MUST-class audit operations through their module catalogues. The central pipeline
writes mutation evidence in the business transaction under
[ADR-0033](../decisions/0033-audit-durability-model.md). Identity never touches
`audit_log` directly or owns another module's command.

- Membership created / removed (per `(user_id, tenant_id, organization_id)`).
- Role assigned / revoked.
- Permission set changed.
- Invitation created / accepted / revoked.
- Tenant setting changed — Tenancy-owned.
- Custom field definition created / changed / deleted — Customization-owned;
  schema history records changes to the personal data the tenant can store.
- Tenant-scoped export requested / completed, tenant-scoped erasure requested /
  completed.
- Global account closure requested — recorded against every affected tenant.
- Platform-admin cross-tenant access (`actor.platformAdmin = true`, with the required
  `reason`).
- Hub-operator access to a tenant resource (`actor.hubOperator = true`).

Keycloak owns login/failure, reset, MFA and lock events in its own stream. G10
selects the authenticated listener/webhook or pull mechanism, stable provider
event identity, durable acknowledgement, ordering and attribution before an
ingress is written. Known multi-membership users, unknown users, tenantless facts
and post-erasure events need explicit routing/privacy outcomes; provider
authentication alone does not authorize a tenant supplied in its payload.

Identity enqueues the approved `learnstack.identity.user` facts through `IOutbox`.
The confined dispatcher publishes through `IEventBus`; the Audit subscription
uses the transport-owned shared inbox under
[ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md). A direct
business `PublishAsync`, raw provider payload copy or invented event tenant is not
admitted. Name any new global intake storage/principal/retention explicitly.
`InProcessEventBus` remains the working transport; Dapr stays demand-gated to
[Phase 11](phase-11-production-hardening.md). Provider development retention is
not the application's security-event retention.

G7 supplies the request-context port and enumerates its trusted population sites:
actor email, membership/roles, Platform/Hub flags, correlation/route, verified IP
and bounded untrusted user-agent. Core Application acquires no ASP.NET dependency.
Complete actual HTTP/application denial evidence and classify member lists/details
and export/downloads as applicable MUST `read-sensitive` acts. No failed write is
hidden by a duplicate HTTP/application audit or a generalized assertion budget.
Extend every affected module matrix, including consent and federation changes.

**The erasure's audit redaction owes a decision before its handler lands.** The handler
[Audit Subsystem § 10](../architecture/31-audit-subsystem.md#10-gdpr--pii-redaction)
sketches writes its `audit.redaction.apply` row on the platform transaction that does the
redaction, before that transaction commits — a MUST row never follows the commit it
describes. `IAuditStore.WritePlatformScopeAsync` is the write that takes a caller's
platform transaction, and its contract admits one caller, the scope's own entry row. This
phase amends [ADR-0044 § 10](../decisions/0044-audit-write-path.md) to admit the redaction
result as a second, bounded caller or to give it a method of its own, and settles the
inbox boundary with it: the inbox mark lives on another connection, so a redelivery must
find the result row already written rather than record a second one.

The same decision must account for immutable `actor_user_id`, entity/causation
identifiers and new `metadata.causal_actor_user_id` references. Current redaction
permits only `actor_email`, `ip_address`, `user_agent`, `before_state`,
`after_state` and `changes`; `metadata` is outside the column grant. Preserve
those boundaries until the bounded decision and any required migration ship.
Redacting the six columns alone does not establish complete anonymisation.

### DSAR fulfillment and global closure

G15/G16 close before request/workflow writers; shared identity schema implications
close in G3's first pass. The [canonical Data Protection inventory](../architecture/23-data-protection.md#durable-asynchronous-store-inventory)
and [Audit redaction boundary](../architecture/31-audit-subsystem.md#10-gdpr--pii-redaction)
remain their respective authorities.

- Define request identity, immutable subject/participant snapshot, idempotency,
  eligibility/holds, progress, partial failure, retry and durable completion.
  Removing membership must not strand authorized pending work or its status view.
- The export target includes a usable private downloadable artifact and expiry.
  G16 names the required Phase 03 storage/delivery slice or an explicitly approved
  replacement, with size/lifetime/reauthorization/revocation/cleanup. Full media
  remains Phase 04; full notification orchestration remains Phase 08a.
- Register required typed jobs/recurrences and execution profiles against
  ADR-0047. Name framework/system authority and retries; an ordinary system job
  cannot obtain privileged erasure rights. There is no implicit Phase 04 job
  prerequisite for a Phase 03 deliverable.
- Global closure fences new memberships/invite acceptance and delayed mirror
  recreation, settles the last-membership case, reconciles all participants and
  performs one final provider-account transition. Distinguish requesting person,
  human approver where required, service actor and privileged executor.
  Before making a User nonadmitting, P03-10 takes the canonical User fence, then
  all affected exclusive Tenant keys, and retains them through completion under
  [Standard 05](../standards/05-database.md#lock-snapshot-and-adoption-conditions).
  Final-admin recovery/termination still requires its separately accepted contract.
- Implement `IUserReferenceLocator` registration for applicable modules, exact
  privileged result-audit access, idempotent result identity and inbox/commit
  reconciliation. A crash after platform commit must not duplicate result audit.
- Preserve immutable transport/audit facts, replay floors, unresolved external
  reservations and minimum grants; extend the inventory for new Identity tables,
  artifacts and copied responses. Phase 11 owns retained-copy/purge extensions,
  not this phase's missing fulfillment. Status explains any retained references.

### Cross-cutting follow-up at phase exit

- **Escalate both analyzers from Warning to Error** — `LS0001`
  (`LearnStackException-DomainExceptionThrow`) and `LS0002`
  (`LearnStackException-SensitiveExceptionMessage`, introduced in
  [Phase 02b](phase-02b-events-auth.md)) — and remove both from `WarningsNotAsErrors` in
  `backend/Directory.Build.props` — the documented escalation point per
  [ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md).
  Reconcile each rule's scoped call sites and obtain a clean solution build
  before this exit gate. `LS0001` retains its Domain/Application scope;
  `LS0002` covers all production C# projects under `backend/src`. Both remain
  Warnings until this gate; neither belongs in `NoWarn`.
- **Inherited documentation CI trigger.** When a second active contributor gains
  write access, add XML-documentation target-existence and changed-prose
  88-column checks to CI under
  [Documentation Standards](../standards/13-documentation.md) and the existing
  [contributor trigger](../../.github/CONTRIBUTING.md#branch-protection-settings-on-main).
  Existing Markdown fragment lint does not establish either check. The trigger
  remains unchanged; no protection-setting change is authorized by this handoff.

## Deliverables

Every row is mandatory. Its packet owns implementation and the linked criterion
identifies observable proof; a later packet completes dependent end-to-end paths.

| ID | Delivered result | Packet | Completion criteria |
|---|---|---|---|
| D01 | Stable global `User` storage/common creation authority, existing-binding admission, scoped Membership/Profile and Role/Permission foundation, fixed seed adoption | P03-1 | C01–C03, C10 |
| D02 | Live membership/permission enforcement, runtime gate, `app.scope` carrier, bounded administrative directory GET and real denied controls | P03-2 | C03–C05, C19 |
| D03 | Bounded authenticated native/SDK operations, usable new-user bootstrap/no-session entry and member/role management screens | P03-3 | C01, C06–C07, C20 |
| D04 | Membership-aware tenant and organization switchers through newly validated scope | P03-3 | C07 |
| D05 | Tenancy-owned organization CRUD/archive endpoints, member/profile preferences and identity-backed admission for named existing Tenancy commands | P03-3 | C02, C06, C16, C20 |
| D06 | Secure tenant/org invitation lifecycle and usable bounded delivery/handoff | P03-4 | C01, C08, C20 |
| D07 | Customization-owned versioned field definitions, mandatory PII category, membership values, editor and schema-driven form | P03-5 | C09, C10, C20 |
| D08 | PII census and diagnostic/snapshot redaction, extended for arbitrary custom values | P03-1; P03-5 extension | C10 |
| D09 | Authenticated durable provider-event ingress, identifier-only outbox facts and deduplicated Audit mirror | P03-6 | C11, C19 |
| D10 | Authenticated OIDC back-channel logout, provider/session indexes and fenced local revocation | P03-7 | C12 |
| D11 | Confined tenant federation configuration, safe linking, test/activate/rollback and recovery | P03-8 | C13, C20 |
| D12 | Platform-scope identity/permission gate, production first-admin/issuer rebind, attributed bypass, killswitch writer and runbook | P03-9 | C14, C19 |
| D13 | Fenced operator recovery and application Hangfire dashboard admission, exact permissions/grants/audit/runbooks | P03-9 | C15, C19 |
| D14 | Self/admin tenant-scoped DSAR requests, usable export/erasure, separate person-authorized global closure and privileged redaction | P03-10 | C17, C18, C20 |
| D15 | Trusted audit request/actor enrichment, sensitive-read/denied coverage and user-reference locators | P03-2; P03-10 locator/redaction completion | C19 |
| D16 | Corrected inactive gateway scaffold, LS0001/LS0002 Error, complete catalogue and source-bound exit evidence | P03-11 | C21 |

## Completion Criteria

Every criterion needs a legitimate positive and a negative that isolates the
claimed control. Ordinary isolation uses real PostgreSQL as `learnstack_app`, with
one person in both seed tenants and both organizations of one tenant, plus another
person in the same tenant. Use real pinned Keycloak and native HTTPS for issuance/
session claims; controlled signed tokens separately isolate protocol refusals.
Test-only endpoints, fake grants or empty scans do not establish shipped behavior.

| ID | Observable proof | Packet evidence |
|---|---|---|
| C01 | A real nonseed invitee obtains required actor claims and completes the permitted flow; concurrent first login and provider/DB retries preserve one mapping. Same-email/different-sub and conflicting/reserved actor values refuse; the four seed identities/credentials survive. | P03-1 internal foundation; P03-3 HTTP/native entry; P03-4 invitation path |
| C02 | Tenant/org directory and profile reads expose only authorized members/fields, including a guessed foreign global UserId. NULL-org uniqueness, archive/revoke/reinvite and profile granularity follow G3 with database race controls. | P03-1–3, P03-5 |
| C03 | Org/self/invite grant escalation fails; legitimate delegation and bootstrap work. Concurrent last-admin changes preserve the decided invariant; registry upgrades grant only the accepted defaults. API revocation meets its declared boundary and stale UI cannot mutate; SystemActor has finite permissions. | P03-1–3 |
| C04 | Actual HTTP rejects an unclassified protected endpoint and an underprivileged valid principal before handler dispatch. Direct MediatR dispatch independently refuses; expected denials return `Result.Fail(forbidden)`. Explicit public reads remain anonymous. | P03-2 |
| C05 | Real rows 7/10/14 succeed only with active membership. Protected rows 6/9 also enforce revocation. Successful claim-only header mismatch has its separately decided evidence. The role-derived tenant read hatch affects EF/RLS consistently and cannot widen org UPDATE/DELETE or be set from request input. | P03-2 |
| C06 | Actual native/SDK member read/edit works with no browser bearer/raw profile. Wrong Origin/CSRF/provenance, forged scope, stale ETag and late old-session response refuse safely. Another actor or revoked actor cannot replay a sensitive idempotent response; paging/search and error states remain scoped. | P03-3 |
| C07 | One person's simultaneous A/B provider/BFF sessions retain independent scope across refresh; tenant/org switching works only through the accepted reissue transition. Held old refresh, wrong destination or revoked target cannot publish; zero-membership entry has usable recovery. | P03-3 |
| C08 | New/existing invitee acceptance checks current trusted verified email and delegation. Wrong/unverified/changed email, wrong org, revoked/expired offer and accept/revoke/expiry/resend races produce one reconciled outcome. GET cannot grant; delivery failure is visible/recoverable, without duplicate grants or unsupported external exactly-once claims. | P03-4 |
| C09 | Definition/value writes use actual admitted schemas and permitted targets; missing/invalid PII category and `Payment` refuse. Required/default/null, successor/retirement/reclassification/key reuse and sensitive-consent cases retain historical interpretation/exportability without orphaned values or raw write bypass. | P03-5 |
| C10 | The actual `users` model has no tenant-authored/custom-fields column. The PII census has a planted missing subject; sensitive value/token/link canaries remain absent from logs, validation errors, Sentry/local archives and audit snapshots, with positive redaction controls for nested custom values. | P03-1, P03-5 |
| C11 | Real provider ingress authenticates and durably acknowledges one stable provider event. Duplicate/reordered delivery and crash-after-accept reconcile once; unknown/no-/multi-membership subjects have the exact authorized routing/audit fan-out. No raw provider payload, direct business publication or invented tenant escapes the event contract. | P03-6 |
| C12 | Real signed back-channel logout and separate wrong-purpose/signature/issuer/audience/time/events/nonce/replay controls prove the selected profile. Shared/distinct SSO and later independent sessions have exact targets; held callbacks/refresh cannot republish. Replay/index retention and unsettled ownership stay bounded; actual provider-to-native reachability is shown. | P03-7 |
| C13 | Two tenant IdPs coexist in the real realm. One tenant cannot edit another broker/secret, use unauthorized egress or link a foreign admin via asserted email. Valid federation still issues the accepted profile; failed activation retains recovery and trusted MFA semantics. | P03-8 |
| C14 | A real operator with the specific Platform permission succeeds only on its admitted surface; tenant/wrong-purpose credentials fail before bypass acquisition. Human/service/executor/subject attribution, reason and correlation survive. A retained raw connection cannot execute autocommit after scope completion/abandonment. The chosen no-Hub baseline works without a Development bypass. Production first-admin setup/recovery and controlled issuer rebind have positive/denied, partial-commit and identity-continuity proof; seed authority is insufficient. | P03-9 |
| C15 | Dashboard/recovery remain absent until admitted. Positive and denied routes, stale fences, producer-versus-subscriber replay, unchanged sibling inboxes and job quiescence/reservations prove only the accepted transition/grants; MUST result audit and runbooks are usable. | P03-9 |
| C16 | Tenant-admin organization create/read/update/archive succeeds under Tenancy contracts; foreign tenant/org, inactive/archived org and default/last-org controls match G3/G5. No cross-module Organization FK/join or write-hatch shortcut exists. The identity-backed settings slice exposes only its named supported commands/fields. | P03-3 |
| C17 | A subject export excludes another person in the same tenant as well as foreign tenants/orgs and disallowed notes. Org-admin cannot request another branch; self-service works without admin rights. Eligibility/holds, purpose/recipient and post-erasure status access match G15. | P03-10 |
| C18 | Export reaches a usable authorized artifact with decided expiry/revocation/cleanup. Tenant erasure removes authorized memberships/profiles and transforms behavior per Data Protection while the other tenant's real login works. Global closure cannot be resurrected by pending invites/new memberships/delayed mirrors. Crash-after-platform-commit redelivery finds the existing result audit; immutable facts, replay floors and uncertain reservations survive with truthful retained-copy status. | P03-10 |
| C19 | MUST mutations/results commit with business state; forced audit failure rolls back success or refuses otherwise-successful sensitive disclosure. Real HTTP/application denials have exactly their decided evidence and preserve 403/404 on audit fault. Trusted actor/membership/role/source/platform/operator/route context cannot be forged. Applicable locators are registered; no Identity-owned audit table exists. | P03-2, P03-6, P03-9–10 |
| C20 | New screens demonstrate enabled locales, safe tenant themes, keyboard/focus/form/error/reflow/contrast, empty/loading/conflict and expiry/outage/429/503 recovery. Scope and destructive effect are clear; no internal token/secret/identifier debugging enters product copy. | Each UI packet |
| C21 | All applicable canonical catalogue obligations have actual subjects and dirty/clean controls, complete required CI populations and zero skips. LS0001/LS0002 are Error, absent from WarningsNotAsErrors, and the solution builds clean. Gateway scaffold uses explicit v1 paths/priorities, no Hub-secret dependency and disabled OIDC; full public-route allowlist/lint/activation remain Phase 11. | P03-11 |

#### Inherited architecture obligations

Use the canonical [Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md)
names. These rows assign proof, not implementation status; the catalogue remains
the status authority. Preserve all existing isolation, module, aggregate,
context-writer/setter, role and public HostScope fences as subjects expand.

| Obligation | Packet / proof |
|---|---|
| `User_Aggregate_Has_No_TenantScoped_Columns` | P03-1 / C10 |
| `Every_TenantCustomFieldDef_Declares_PiiCategory` | P03-5 / C09; behavioral lifecycle/privacy proof supplements NOT NULL |
| `Tenant_Scoped_Export_Contains_No_Foreign_Tenant_Rows` | P03-10 / C17, supplemented by same-tenant foreign-person/org cases |
| `Every_PII_Module_RegistersUserReferenceLocator` | P03-10 / C18–C19; applicable module census and planted missing registration |
| `Permission_Definitions_DeclareScope`, `Permission_Scope_Matches_Resource_Scope` | P03-2 / C03–C05 |
| `Permission_Keys_Match_Convention`, `Permission_Registry_Has_DeniedTest` | P03-2 / C03–C04; actual nonempty registration set |
| `MustClass_Audit_Writes_Share_The_Business_Transaction` | P03-2 / C19 completes the real authorization-denied clause |
| `Tenant_Scope_Widening_Is_Never_Set_From_Request_Input` | P03-2 / C05 replaces the vacuous carrier subject |
| `PlatformAdminScope_Entry_Requires_Platform_Permission` | P03-9 / C14 adds actual admitted and denied operators |
| `Provider_SDK_Types_NotImportedOutsideInfrastructure` | First actual runtime provider SDK, if introduced; direct HTTP alone does not close backfill |

Each packet records exact source/configuration/provider versions, actual result
population and sanitized receipts in its delivery log. No production p95, full
WCAG conformance or external exactly-once effect is inferred from local fixtures.

## Risks

- **Redaction mistaken for anonymisation.** Retained actor/entity/causation IDs
  and metadata survive six-column redaction. The bounded first-writer decision
  and durable-copy controls are exit requirements, not a later operations waiver.

- **Re-implementing what Keycloak owns** — password hashing, reset emails, token
  rotation. If a capability appears in the Keycloak vs LearnStack split table in
  [Identity and Authentication](../architecture/13-identity-and-auth.md), it stays in
  Keycloak.
- **The ownership split erodes one column at a time.** The pressure is always local and
  always reasonable: a global "phone number" is convenient, and the first tenant to ask
  for it will not be thinking about the second. `User_Aggregate_Has_No_TenantScoped_Columns`
  is the mechanical answer; a reviewer asking "which tenant authored this value?" is the
  human one.
- **A tenant-scoped erasure quietly becoming global.** The two flows share vocabulary,
  handler names and UI copy, and the destructive one is easier to implement. Keeping
  global closure off the tenant-admin surface entirely — no endpoint, no permission key
  — is cheaper than reviewing every future change to the shared path.
- **`pii_category` degenerating into a required field nobody thinks about.** If every
  definition ends up `None`, the classification is decorative and the export is wrong.
  The Studio editor asks the question in the field's own words, and the seed data sets
  a non-trivial category so the shape is exercised.
- **Designing the identity model around the first tenant.** Stable Membership roots,
  null-safe natural uniqueness and tenant-wide Role templates with scoped capabilities
  must serve every tenant shape, not only the language-school
  showcase in [Phase 10](phase-10-english-learning-mvp.md). Both
  [Phase 02a Packet 7](phase-02a-kernel-tenancy.md) seed tenants get memberships in
  this phase.
- **Keeping roles simple and postponing permissions until it hurts.** The permission
  key set is small here, but the scope dimension is not optional — retrofitting scope
  onto flat permissions touches every check.
- **Relying on frontend checks for admin authorization.** The API enforces; the UI
  mirrors. A hidden button is a usability decision, never a security one.
- **Scaffold correction mistaken for gateway activation.** Phase 11 consumes
  explicit version paths and separated credentials, then qualifies the reviewed
  public allowlist, precedence and OIDC/TLS/provenance. Copying a target example
  into the scaffold does not establish any of those production controls.

## Phase Exit Decision

[Phase 04](phase-04-cms-media-pages.md) begins when all of the following hold:

- Every D01–D16 deliverable and C01–C21 completion criterion has source-bound
  evidence in the phase exit index, linking actual packet records and relevant
  accepted vehicles. No mandatory invitation, federation, logout, operator,
  organization, settings, audit-enrichment or DSAR path is omitted by summarizing
  the phase as "RBAC complete". Every applicable first-writer gate is Accepted
  for the part implemented; conditional SDK coverage retains its honest subject.
- A person holding memberships in both seed tenants has their tenant-authored
  attributes stored per tenant, invisible across tenants, and provably so by an
  integration test running as `learnstack_app`.
- A tenant-scoped erasure in one tenant leaves that person's access to the other tenant
  intact, and no tenant-admin path can reach the global account.
- The bounded DSAR/redaction decision and applicable durable-copy controls above
  are implemented; surviving immutable references are explicitly reconciled.
- Every `TenantCustomFieldDef` carries a PII category, and export, redaction and
  retention are driven by the definitions rather than by the JSONB blob.
- `AuthorizationBehavior` and the runtime default-deny gate are real, with
  independently proved HTTP/application denials. Every protected endpoint,
  sensitive reads included, has its explicit policy and assigned audit evidence.
- `LS0001` and `LS0002` are Errors, both removed from `WarningsNotAsErrors`, and the
  solution builds clean.
- The corrected APISIX scaffold is committed with explicit version paths and
  priorities, no Hub-secret dependency and disabled OIDC. Reviewed public-route
  allowlisting, lint and gateway activation remain
  [Phase 11](phase-11-production-hardening.md) work.
- The identity, DSAR and custom-field architecture tests named above are registered in
  [Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md) and
  green in CI.
