# Phase 03: Identity Domain, Authorization, and Admin Foundation

## Goal

Build LearnStack's identity domain (users, memberships with triple-key
`(user_id, tenant_id, organization_id)`, roles, permissions, invitations) on top of
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

This phase also settles a question the corpus has been carrying unanswered: **which
identity data belongs to a tenant and which belongs to the person**. Every earlier
phase could avoid it because no tenant admin could write a person's attributes yet.
From this phase forward one can, and the answer has to exist before the first custom
field is stored.

## Scope

### Identity Model

- `User` — global identity mirrored from Keycloak (`sub`, canonical email, global
  display name). One row per human across the whole platform.
- `Membership` — per-tenant **and per-organization** relationship; triple key
  `(user_id, tenant_id, organization_id)` per
  [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md). Carries roles. A user
  can hold memberships in multiple tenants and in multiple organizations within one
  tenant.
- `MembershipProfile` — the tenant-owned profile record attached to a `Membership`:
  per-tenant display name, tenant-visible contact fields, locale preference,
  per-tenant consent flags, and the tenant-defined `custom_fields` JSONB column.
- `Role` — Platform / Tenant / Organization-scoped per the scope catalogue in
  [Permission Standards](../standards/19-permissions.md).
- `Permission` — fine-grained capability inside a role with an explicit scope
  (Platform / Tenant / Organization).
- `Invitation` — pending membership offer, bound to email + tenant + organization.

The aggregate previously named `UserProfile` is split by the ownership rules below: the
attributes a person owns stay on `User`, and everything a tenant authors moves to
`MembershipProfile`. There is no single profile aggregate straddling both.

> **Audit ownership.** The `AuditEntry` aggregate lives in the **Audit** module, not in
> Identity. Identity's commands flow through the shared `AuditLogBehavior`, and
> MUST-class rows are written on the same transaction as the business write per
> [ADR-0033](../decisions/0033-audit-durability-model.md). Cross-process identity
> signals (Keycloak webhooks) arrive as integration events. There is **no**
> Identity-owned audit table.

> **Organization ownership.** The `Organization` aggregate and its CRUD endpoints live in
> the **Tenancy** module, not in Identity, per
> [ADR-0017 Amendment 2](../decisions/0017-tenant-organization-hierarchy.md) — which
> resolves the "Identity / Tenancy" wording ADR-0017 shipped with. Identity owns Keycloak
> `organization_id` attribute mapping, the `Membership` extension for org-scoped role
> assignments, and JWT claim emission. `Membership` holds `OrganizationId` by value from
> `LearnStack.SharedKernel`: no navigation property, no join, no foreign key.

### Tenant Data Ownership, DSAR Boundary, and PII Classification

The corpus currently conflates the global `User` aggregate with tenant-owned profile
data, and three concrete defects follow from it:

- **Custom fields on `User` leak across tenants.**
  [Tenant Customization Model § Custom fields on built-in entities](../architecture/32-tenant-customization-model.md)
  adds `custom_fields jsonb` to the `users` table and lets a `TenantCustomFieldDef` with
  `target_entity = "User"` write into it. `users` is a global table with no
  `tenant_id`, no query filter and no Row Level Security policy. A learner enrolled in
  a language school and a yoga studio would carry both tenants' fields in one blob that
  both tenants read.
- **A tenant-scoped data request is not separated from global account deletion.**
  [Data Protection](../architecture/23-data-protection.md) describes one erasure
  workflow that ends in "invalidate Keycloak user". Initiated by a tenant admin, that
  workflow destroys an identity that other tenants are actively using. One tenant must
  not be able to close a person's account with another.
- **JSONB columns carry no PII metadata.** Data Protection requires every aggregate to
  declare a PII category and claims a CI check over columns named like `email` or
  `phone`. A `custom_fields` blob is one column whose keys are authored by tenants at
  runtime; column-level classification cannot see inside it. Retention, export and
  redaction are therefore undefined for exactly the data most likely to be personal.

This phase resolves all three.

**Attribute ownership.** Each attribute has exactly one owner, and the owner determines
the table it lives in and who may write it.

| Attribute class | Owner | Storage | Writable by |
|---|---|---|---|
| Credentials, MFA factors, token material | Keycloak | Keycloak realm `learnstack` | Keycloak only |
| `sub`, canonical email, email-verified flag | Keycloak | mirrored onto `users` | The mirror handler only |
| Global display name, avatar, preferred UI locale, global account status | The person | `users` | The person; platform admin under an audited `EnterPlatformAdminScope(reason)` |
| Roles and permissions inside a tenant | Tenant | `memberships` | Tenant / organization admin of that tenant |
| Per-tenant display name, contact fields, consent flags, locale preference | Tenant | `membership_profiles` | Tenant / organization admin of that tenant, and the person within that tenant |
| Tenant-defined custom fields | Tenant | `membership_profiles.custom_fields` | Tenant / organization admin of that tenant |
| Learning behaviour (progress, attempts, attendance) | Tenant | per-module tenant-owned tables | The owning module |

Two rules make the table enforceable:

- **`users` carries no `custom_fields` column and no tenant-authored column of any
  kind.** A `TenantCustomFieldDef` whose `target_entity` is `User` resolves to
  `membership_profiles`, not to `users`. The field is defined once per tenant and
  stored once per membership.
- **`MembershipProfile` is `[TenantOwned]` and `[OrganizationScoped]`**, with an EF
  global query filter and a Row Level Security policy built from the canonical template
  in [Database Standards](../standards/05-database.md). It is an ordinary tenant table
  and gets the ordinary four layers of defense
  ([ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md)).

**DSAR boundary.** A data subject access request is scoped to a tenant unless the
person themselves asks for the account itself.

| Request | Initiated by | Scope | Effect on other tenants |
|---|---|---|---|
| Tenant-scoped export | Tenant admin, or the person acting inside that tenant | Rows where `tenant_id = <tenant>` | None |
| Tenant-scoped erasure | Tenant admin, or the person acting inside that tenant | The membership, its profile, and that tenant's behaviour rows | None |
| Global account closure | The person, through the platform account surface | The `users` row, the Keycloak user, and a fan-out to every membership | All of them, by design |

- A tenant-scoped export is produced by running the export handlers **inside that
  tenant's `ITenantContext`, connected as `learnstack_app`**. Row Level Security is the
  mechanism that bounds the bundle, not a `WHERE tenant_id = …` clause a developer can
  forget in one of a dozen modules.
- A tenant-scoped erasure removes the `Membership` and its `MembershipProfile`,
  anonymises that tenant's behaviour rows, and anonymises the actor field on that
  tenant's audit entries while keeping the action record
  ([Data Protection § Right to Erasure](../architecture/23-data-protection.md)). The
  `users` row survives for as long as any membership remains anywhere.
- **Global account closure has no tenant-admin endpoint.** It is a platform-scoped
  operation. It fans out `UserAnonymisationRequestedV1` to every tenant the person
  belongs to and reconciles completion per Data Protection's 30-day workflow.
- The audited `EnterPlatformAdminScope(reason)` from
  [Phase 02a Packet 7](phase-02a-kernel-tenancy.md) is the only path that reads across
  tenants during either flow, and every use of it is MUST-class audited.

**PII classification on JSONB.** Every `TenantCustomFieldDef` row carries a required
`pii_category` drawn from the closed set in
[Data Protection § Personal Data Inventory](../architecture/23-data-protection.md) —
`PII-Identity`, `PII-Behaviour`, `PII-Sensitive`, or `None`. There is no default; the
Studio editor forces a choice at definition time, and the definition cannot be saved
without one.

- Export, redaction and retention walk the **definitions**, not the blob. Without the
  per-key classification an export either dumps the whole column — leaking values that
  should have been redacted — or drops it entirely, which fails the portability right.
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

- `tenant_custom_field_defs` with `UNIQUE (tenant_id, target_entity, key)`, the
  required `pii_category` column, the field's JSON Schema, and `is_required` / `sort`
  presentation metadata.
- Allowed `target_entity` values **in this phase**: `Membership` only. The remaining
  targets light up with their owning phases —
  `Course` and `Lesson` in [Phase 05](phase-05-education-learning-content.md),
  `Enrollment` in [Phase 07](phase-07-enrollment-learner-portal.md),
  `LiveSession` in [Phase 08c](phase-08c-classroom.md). A definition naming a target
  whose phase has not shipped is rejected at save time rather than silently stored.
- Values are validated against the field's JSON Schema on every write, and rejected
  through `Result.Fail(validation_failed)` — never by throwing.
- Permission keys per [Permission Standards](../standards/19-permissions.md):
  `customization.custom_field.admin` (Tenant scope) to define fields;
  `identity.membership.write` (Tenant / Organization scope) to set their values.
- Studio surface: a definition editor and a schema-driven form on the membership detail
  page. The visual schema builder is [Phase 06](phase-06-renderer-admin-studio.md).

### Authentication Integration

Keycloak owns credentials, password reset, email verification, and MFA. This phase
wires LearnStack into that flow:

- OIDC token validation against Keycloak's JWKS
  ([Phase 02b](phase-02b-events-auth.md) configured the middleware).
- BFF session handling for the Next.js Studio: HTTP-only cookies, silent refresh,
  end-session at Keycloak on logout.
- Post-login membership lookup: resolve memberships for the authenticated user and
  surface the active tenant via a host + claim cross-check.
- Tenant-specific Keycloak federation surface in Admin Studio (configure SAML / OIDC
  IdP per tenant) — UI delivered here, runtime in Keycloak.
- Mapping of Keycloak `sub` to LearnStack `UserId`; idempotent on first login.

LearnStack does **not** implement password hashing, password reset email rendering,
refresh token storage, or brute-force protection — those are Keycloak responsibilities.

### Authorization

- Role-based authorization.
- Permission-based authorization.
- Tenant-scoped permission checks (`Membership.Roles → Permissions`).
- Resource-scoped policies (for example, an instructor edits only their own courses).
- Admin and Studio route guards.
- API authorization policies.
- **Lights up the [Phase 02a Packet 3](phase-02a-kernel-tenancy.md)
  `AuthorizationBehavior` shell** — resolves each command's `[Authorize(Policy)]`,
  calls `IAuthorizationService.AuthorizeAsync` against the tenant +
  organization-scoped resource, and returns `Result.FailFor<TResponse>(forbidden)` on
  deny, per
  [ADR-0032 § Sub-decision 2](../decisions/0032-exception-handling-logging-and-observability.md).

Authorization is the third layer, not the first. A permission check that passes still
runs under the tenant's `ITenantContext` and under Row Level Security; a deny is a
better error message, not the isolation boundary.

### Gateway Configuration Corrections

The APISIX gateway **adapter** is demand-gated to
[Phase 11](phase-11-production-hardening.md) per
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md); until then the equivalent
concerns are handled by ASP.NET middleware. The published route table in
[API Gateway](../architecture/30-api-gateway.md) is nevertheless corrected **here**,
because that file is what an operator will paste into a live gateway the first time one
is needed, and both defects below are silent when read and severe when deployed.

**Route priority.** Every route declares an explicit integer `priority`, and every
anonymous route whose path is matched by the authenticated catch-all declares a
strictly higher value than it.

- The published table describes priority bands in comments (`# priority 1`,
  `# priority 99`, `# priority 100`) but no route carries a `priority` field. APISIX
  defaults an omitted priority to `0`, so every band ties.
- `GET /api/v*/localization/*` and the authenticated catch-all `/api/v*/**` both match
  `GET /api/v1/localization/en`. When the catch-all wins the tie, `openid-connect` with
  `bearer_only: true` returns 401 to a request that has no token and is not supposed to
  need one.
- That request is the first call the public renderer makes. Anonymous rendering from
  [Phase 02d](phase-02d-walking-skeleton.md) and the public CMS reads from
  [Phase 04](phase-04-cms-media-pages.md) sit behind exactly this route.

**Route 100's `client_secret_ref` is deleted.** The line currently reads
`client_secret_ref: vault://learnstack/hub/internal-api-hmac-key`, binding the
gateway's Keycloak OIDC client secret to the Hub internal-API HMAC signing key. It is
wrong twice:

- **Functionally** — the `learnstack-gateway` client's secret in the `learnstack` realm
  is not the Hub signing key, so the OIDC handshake cannot succeed with that value.
- **Structurally** — it collapses two trust domains into one secret. The HMAC key signs
  `/api/internal/*` request bodies in the Hub auth chain
  ([ADR-0034](../decisions/0034-hub-contract-surface-invariant.md)). Distributing it
  into edge-proxy configuration — and, on failure, into edge-proxy error logs — hands
  anyone who can read either the ability to forge a signed
  `PUT /api/internal/tenants/{id}/entitlements` or
  `PUT /api/internal/tenants/{id}/host-mappings` for **any** tenant: granting themselves
  any plan, or pointing a tenant's hostname somewhere else. The repository's secret-scan
  check does not catch it, because the committed value is a Vault path rather than a
  secret.

The correction: route 100 references the gateway's own OIDC client secret at its own
path, and the Hub HMAC key appears in no gateway configuration at any priority. It is
held only by the `IEntitlementProvider` / `IUsageReporter` / `IHubTenantSync` adapters
ADR-0034 names as the sole Hub-facing callers.

### Invitation Flow

- Tenant admin invites a user (email + role + organization).
- Invitation token bound to the email; a mismatched signup is rejected.
- A new invitee is redirected to Keycloak signup with a prefilled email; an existing
  user is redirected to Keycloak login.
- After callback, LearnStack creates the `Membership` and marks the invitation
  accepted.
- Invitation expiry (default 14 days), accept and revoke endpoints.

An invitation creates a membership in one tenant. It never creates, modifies or deletes
the global `User` row beyond the first-login mirror.

### Admin Foundation (identity-management screens only)

This phase ships the identity-management surface in Admin Studio. CMS, page-builder and
catalog screens are owned by [Phase 06](phase-06-renderer-admin-studio.md).

- Login (delegates to Keycloak).
- Tenant switcher (for multi-tenant operators) and organization switcher.
- Users list and detail, scoped to the acting tenant and organization.
- Roles and permissions management.
- Invitations.
- Custom field definitions and the schema-driven membership form.
- Tenant member settings basics.
- Tenant-scoped data-request surface: request an export, request an erasure, view
  status. Global account closure is deliberately absent from this surface.

### Audit Coverage Wiring

Identity emits the following MUST-class audit operations through the central pipeline;
`AuditLogBehavior` enrols the row in the same transaction as the business write per
[ADR-0033](../decisions/0033-audit-durability-model.md). Identity itself never touches
`audit_log` directly.

- Membership created / removed (per `(user_id, tenant_id, organization_id)`).
- Role assigned / revoked.
- Permission set changed.
- Invitation created / accepted / revoked.
- Tenant setting changed.
- Custom field definition created / changed / deleted — a schema change alters what
  personal data the tenant stores, so the definition history is itself compliance
  evidence.
- Tenant-scoped export requested / completed, tenant-scoped erasure requested /
  completed.
- Global account closure requested — recorded against every affected tenant.
- Platform-admin cross-tenant access (`actor.platformAdmin = true`, with the required
  `reason`).
- Hub-operator access to a tenant resource (`actor.hubOperator = true`).

Keycloak owns its own audit stream for login success and failure, password reset, MFA
enrolment, and account lock. The Identity module **subscribes** to Keycloak webhooks
and re-publishes the relevant events as `learnstack.identity.user` integration events
through `IEventBus`; the Audit module consumes them through `IInboxGuard`-protected
handlers. `IEventBus` resolves to `InProcessEventBus` from
[Phase 02a Packet 5](phase-02a-kernel-tenancy.md); the Dapr-backed transport is
demand-gated to [Phase 11](phase-11-production-hardening.md) per
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md), and the handler contract
is identical either way. See
[Audit Coverage Standards](../standards/18-audit-coverage.md) for the MUST / SHOULD /
MAY matrix.

### Cross-cutting follow-up at phase exit

- **Escalate both analyzers from Warning to Error** — `LS0001`
  (`LearnStackException-DomainExceptionThrow`) and `LS0002` (the
  secrets-in-exception-message rule introduced in
  [Phase 02b](phase-02b-events-auth.md)) — and remove both from `WarningsNotAsErrors` in
  `backend/Directory.Build.props` — the documented escalation point per
  [ADR-0032 § Sub-decision 4 + Amendment 1](../decisions/0032-exception-handling-logging-and-observability.md).
  Until this gate the analyzer fires as a Warning so that a legitimate
  aggregate-invariant throw does not break CI. By phase exit the identity domain is the
  first substantial body of domain code, and the rule has been exercised against real
  aggregates rather than against an empty solution.

## Deliverables

- Identity domain — `User` (global), `Membership` (triple-keyed), `MembershipProfile`
  (tenant-owned, organization-scoped), `Role` and `Permission` with explicit scope,
  `Invitation` — on top of Keycloak.
- Written attribute-ownership table, enforced by `User_Aggregate_Has_No_TenantScoped_Columns`.
- Tenant-scoped DSAR export and erasure, running under `ITenantContext` as
  `learnstack_app`; global account closure as a separate platform-scoped operation.
- `TenantCustomFieldDef` with a mandatory `pii_category`, the `Membership` target, the
  definition editor, and the schema-driven membership form.
- Category-driven redaction of custom-field values in logs and error-tracking payloads.
- `AuthorizationBehavior` as a real implementation, replacing the Phase 02a shell.
- Admin login, tenant switcher and organization switcher via OIDC PKCE.
- Tenant- and organization-aware user management screens.
- Invitation flow end to end, tenant and organization bound.
- Identity events wired through the central audit pipeline; Keycloak webhook →
  `IEventBus` → Audit consumer working end to end.
- Corrected APISIX route table: explicit priorities on every route, and no Hub HMAC
  reference anywhere in gateway configuration.
- `LS0001` and `LS0002` escalated to Error.

## Completion Criteria

- A tenant admin sees only users from their tenant; an organization admin sees only
  users from their organization.
- No attribute a tenant authored about a person is visible to any other tenant. An
  integration test creates one person with memberships in both
  [Phase 02a Packet 7](phase-02a-kernel-tenancy.md) seed tenants, sets a custom field
  in each, and asserts as `learnstack_app` that neither tenant can read the other's
  value.
- `users` has no `custom_fields` column and no tenant-authored column;
  `User_Aggregate_Has_No_TenantScoped_Columns` is green.
- A tenant-scoped erasure removes that tenant's membership, profile and behaviour rows
  and leaves the person's membership in the other tenant fully functional, including
  the ability to log in.
- No tenant-admin endpoint can delete or anonymise the global `User` row or the
  Keycloak user.
- Every `TenantCustomFieldDef` row carries a `pii_category`; a definition with category
  `Payment` is rejected; the export bundle contains exactly the fields the categories
  permit.
- A custom-field value classified `PII-Identity` does not appear in any log line or
  error-tracking payload.
- Role and permission changes are enforced by both API and UI; a permission-scope
  rejection (Tenant vs Organization) returns the correct Problem Details code.
- Unauthorized users cannot reach admin endpoints, and a denied command returns
  `Result.Fail(forbidden)` rather than throwing.
- Invitation flow is covered by integration tests, including email mismatch, revoked,
  expired, and cross-organization cases.
- MUST-class identity audit entries commit in the same transaction as the write they
  describe, with before / after snapshots where applicable; no Identity-owned `audit_*`
  table exists.
- Cross-tenant operations from a platform-admin role are audited with the required
  `reason`; Hub-operator actions are audited with `actor.hubOperator = true`.
- The committed APISIX route table declares a priority on every route, the anonymous
  localization route outranks the authenticated catch-all, and
  `internal-api-hmac-key` appears nowhere in it.

## Risks

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
- **Designing the identity model around the first tenant.** Triple-key membership and
  organization-scoped roles must serve every tenant shape, not only the language-school
  showcase in [Phase 10](phase-10-english-learning-mvp.md). Both
  [Phase 02a Packet 7](phase-02a-kernel-tenancy.md) seed tenants get memberships in
  this phase.
- **Keeping roles simple and postponing permissions until it hurts.** The permission
  key set is small here, but the scope dimension is not optional — retrofitting scope
  onto flat permissions touches every check.
- **Relying on frontend checks for admin authorization.** The API enforces; the UI
  mirrors. A hidden button is a usability decision, never a security one.
- **Correcting the gateway config and then never deploying it.** The corrected table is
  configuration for a component that does not run until
  [Phase 11](phase-11-production-hardening.md). Phase 11 must adopt the corrected file
  rather than re-deriving one from the architecture doc's older revision.

## Phase Exit Decision

[Phase 04](phase-04-cms-media-pages.md) begins when all of the following hold:

- A person holding memberships in both seed tenants has their tenant-authored
  attributes stored per tenant, invisible across tenants, and provably so by an
  integration test running as `learnstack_app`.
- A tenant-scoped erasure in one tenant leaves that person's access to the other tenant
  intact, and no tenant-admin path can reach the global account.
- Every `TenantCustomFieldDef` carries a PII category, and export, redaction and
  retention are driven by the definitions rather than by the JSONB blob.
- `AuthorizationBehavior` is a real implementation and every write endpoint carries an
  explicit policy.
- `LS0001` and `LS0002` are Errors, both removed from `WarningsNotAsErrors`, and the
  solution builds clean.
- The corrected APISIX route table is committed with explicit priorities and no Hub
  HMAC reference. The gateway adapter itself remains
  [Phase 11](phase-11-production-hardening.md) work.
- The identity, DSAR and custom-field architecture tests named above are registered in
  [Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md) and
  green in CI.
