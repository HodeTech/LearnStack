# ADR-0108: Privileged Audit Redaction

## Status

Accepted — 2026-11-22, after the maintainer reviewed the drafts, four independent reviews of them and the corrections those reviews led to. The dated record is the [Phase 03 acceptance record](../roadmap/phase-03-delivery-log.md#p03-10-decision-package-and-acceptance-2026-11-22). Acceptance settles the redaction
seam, the locator contract and the surviving-reference treatment that
[Phase 03](../roadmap/phase-03-identity-admin.md#the-decision-register) and
[ADR-0044](0044-audit-write-path.md) § 10 owe before the first redaction writer, and with
[ADR-0106](0106-subject-data-requests-authority-and-status.md) and
[ADR-0107](0107-data-request-fulfillment-and-account-closure.md) is the P03-10 decision
package. It authorizes the code it describes and is not proof that anything is
implemented.

**Date:** 2026-11-22
**Deciders:** @cemil

## Decision Drivers

- `audit_log` allows two mutations by one role: `learnstack_platform` may UPDATE six
  columns (`actor_email`, `ip_address`, `user_agent`, `before_state`, `after_state`,
  `changes`) and DELETE rows for the retention purge, and a trigger rejects every other
  change. The facade rule below keeps the executor from using the DELETE. No redaction code, no
  `IUserReferenceLocator` and no `audit.redaction.apply` operation exists.
- The platform scope facade refuses any SQL that names `audit_log`, the handle exposes no
  connection, and a scope's result row is its last write.
  [ADR-0101](0101-platform-operator-authority.md) admits human operators only and
  refuses job and external frames, so a subscription consumer cannot be the executor.
- [ADR-0044](0044-audit-write-path.md) Amendments 17 and 18 already added witness-bound
  result and intent writes, so the "one caller of `WritePlatformScopeAsync`" question
  Phase 03 raised is answered without a new store method.
- `actor_user_id`, entity and causation identifiers, `correlation_id` and all of
  `metadata` (which carries `causal_actor_user_id`, authorization Membership and Role
  references and `identity_subject_key`) are outside the grant and the trigger. Redacting
  six columns is not anonymisation.
- The corpus disagrees on what a locator does: Audit Subsystem § 10 has module locators
  redact payload references, while Data Protection and Audit's own spec say locate and
  report only.

## Considered Options

1. **An operator platform-scope act that calls one Audit-owned executor with fixed
   statements, driven by module-declared locators, and reports what survives** (chosen).
2. **Widen the grant and the trigger to `metadata` and `actor_user_id`** (rejected). Both
   are the attributable history that audit exists to keep, and the subject key is a
   deliberate permanent tombstone; widening them would end the append-only guarantee for
   the columns that make rows trustworthy.
3. **A new `IAuditStore` method for the redaction result** (rejected). The existing
   witness-bound result write carries it, and a sixteenth method breaks the pinned
   count for no gain.
4. **Let the facade accept SQL that names `audit_log`** (rejected). It would remove the
   rule that keeps every other platform act from touching the log.
5. **A subscription consumer on a privileged delivery** (rejected). ADR-0046 says the
   ordinary executor refuses privileged subscriptions and ADR-0101 refuses a machine
   executor.

## Decision

1. **Executor.** The privileged half of an erasure request, and the redaction inside a
   closure's finalize ([ADR-0107](0107-data-request-fulfillment-and-account-closure.md)
   § 7), is an operator act in the platform scope. The standalone act is the registry
   platform operation `complete_erasure` of ADR-0107 § 8, whose audit slug is
   `audit.redaction.apply` (module `audit`), off the MediatR path, MUST and
   `security-event`, under the non-assignable Platform key `platform.data_request.write`,
   which settles the key Audit's permission spec names "pending". ADR-0101's key table
   gains it by dated amendment, and the Audit matrix drops the `(planned)` marker.
2. **Seam.** An Audit-owned executor with fixed, parameterized statements lives in
   `LearnStack.Infrastructure.Audit` behind a SharedKernel port `IAuditRedactor`, which the
   platform scope resolves by dependency injection, so `LearnStack.Infrastructure` gains no
   reference to the Audit infrastructure project. The scope handle exposes one new method,
   `RedactAuditAsync(plan)`. The method is bound to the admitted operation: it refuses unless the scope's admission names `audit.redaction.apply` or the
   closure Finalize operation, so no other platform-scope service can call it. The facade
   rule that statements cannot name `audit_log` stays unchanged and tested. The executor's
   file is the one sanctioned redaction site, added by exact path to the append-only
   architecture test's allow-list. Modules never write `audit_log`.
3. **Result row.** The result is the existing witness-bound platform-scope result write.
   `IAuditStore` keeps its fifteen writes and `WritePlatformScopeAsync` keeps its single
   entry-row caller. The standalone act concerns one tenant, so its row names that real
   tenant; the closure Finalize concerns several and writes one platform-wide row under the
   sentinel tenant (ADR-0044 Amendment 17), whose report lists the tenant identifiers
   (not personal data) and the per-tenant counts, while each tenant's own fan-out request
   carries its tenant's MUST evidence. Rows carry `actor_user_id` NULL, the closed reason
   slug, the correlation id and the operator attribution, and hold a closed report in
   `after_state`: counts of rows redacted per column and counts of retained references per
   kind, never a person identifier.
4. **Plan and selection.** The plan carries the tenant, the request's scope group, the
   request's origin, the subject `UserId`, the request's frozen Membership identifiers and,
   for a closure, the subject key. A row's `organization_id` is the announcement's
   organization of the transaction that wrote it
   ([ADR-0044](0044-audit-write-path.md) § 9), not the entity's, so the selection depends on
   who asked. An erasure the person requested, and every participant tenant of a closure,
   selects across the whole tenant. An erasure an administrator requested selects only rows
   whose `organization_id` is the group's (a tenant-wide group selects rows with no
   organization, an organization group only that organization's), so an organization
   administrator never alters another branch's records. In either case a row is selected
   when `actor_user_id` equals the subject, or `entity_id` equals the subject or a
   Membership of the snapshot, or a locator-declared reference in `before_state`,
   `after_state` or `changes` holds one of those identifiers. A closure's Finalize also runs
   the selection over the platform sentinel tenant, by `metadata.identity_subject_key`, for
   the subject's global creation and failed attempts. Rows the selection cannot reach are
   reported as `unscoped_audit_rows`.
5. **What is redacted.** `ip_address`, `user_agent` and `actor_email` become NULL.
   String values at declared pointers in the three JSON columns become
   `SensitiveTokenCatalog.RedactedValue`, the one sentinel the capture already writes. The
   statements are idempotent, touch only the six granted columns and refuse any other
   change at the trigger as today.
6. **What survives and is reported.** `actor_user_id`, `entity_id`, causation and
   correlation identifiers, `module`, `operation`, outcome, timestamps and all of
   `metadata` are retained. Each request reports the retained kinds and counts under the
   closed vocabulary of ADR-0107 § 6, together with the retention target of
   [Audit Coverage](../standards/18-audit-coverage.md#retention) for the row's class,
   which no code enforces yet (Phase 11 owns purge). The tombstone and the subject key
   are never called anonymous. ADR-0038's statement that an erased actor is "an orphan
   surrogate key with no path back to a natural person" is false once the subject key is
   kept, and is corrected by amendment (as is the same sentence in Phase 02a) to say that the
   retained identifier is pseudonymous and stays linkable through the subject key.
7. **Locators.** Each applicable module registers an `IUserReferenceLocator` that
   declares, for its entities, the properties that hold a person or Membership reference
   and the retained-linkage kinds it knows, as `(entityType, propertyName, referenceKind)`.
   The paths of `changes` items are instance-qualified (`/{EntityType}/{EntityId}/...`,
   [CapturedEntityChange](../../backend/src/LearnStack.SharedKernel/Audit/CapturedEntityChange.cs)),
   so the executor matches a declaration by entity type and property name in `before_state`
   and `after_state` (entity property maps) and by entity type and path suffix in `changes`,
   and resolves the entity identifier from the row itself. The locator performs no SQL and
   holds no privilege; the Audit executor applies the declarations. A census over the
   existing personal-data classification requires every mapped property of person-reference
   type (`UserId`, `MembershipId`) in an audited entity to be declared or listed as an
   explicit retained reference, and each module's canary test embeds a reference at every
   declared pointer, runs the redaction and asserts it is gone, so an omitted declaration
   fails the build. This
   reconciles Audit Subsystem § 10 (the module knows where its references are) with Data
   Protection (the module never reaches `audit_log`).
8. **Idempotency and crash behaviour.** For `complete_erasure`, the redaction, the
   request's status change and the result row commit in one platform transaction, so no
   state exists in which one is recorded without the others. A closure's Finalize uses the
   intent checkpoint of ADR-0044 Amendment 18: the first transaction redacts and writes the
   intent row that carries the redaction counts, the provider call follows, and the second
   transaction writes the Closed transition and the result row, so the redaction's MUST
   evidence is the intent row in the transaction that did it. A re-run after the commit
   finds the request `completed` and returns its recorded result with no second row; a
   re-run after a rollback repeats statements that are idempotent. The closed report is
   bounded: counts per kind and column, aggregated into totals beyond 200 groups so it stays
   under the 32 KiB state limit. No inbox is involved, because the act is an HTTP operator
   act and not a delivery.
9. **Grants.** No grant on `audit_log` and no change to the append-only trigger function
   is made. The platform role's request-table privileges are those of ADR-0107 § 8.

## Context

- **Why not anonymise the retained columns.** `actor_user_id` is what lets a reviewer
  answer who did something; replacing it makes the log unauditable, and the subject key is
  kept deliberately to prevent recreation. The honest contract is redaction of what is
  not needed to attribute an action, plus a truthful report of what remains.
- **Why one executor file.** The facade rule and the single sanctioned site make
  "who can redact" a one-line census, which is the property the append-only test needs.
- **What would change our minds.** A retention policy that allows removing the attributable
  columns, or a Phase 11 purge that deletes rows past their target, would change what
  survives; neither is assumed.

## Consequences

### Positive

- The redaction has one executor, one result row and one idempotency story, with no new
  store method and no new grant on `audit_log`.
- Modules declare where their references live without gaining any audit access.

### Negative

- A person's identifier survives in audit and in `metadata` after erasure, and the status
  says so.
- Each erasure waits for an operator act for the redaction.

### Neutral

- The retention targets are reported, not enforced, until Phase 11.

## Implementation Notes

- Step 2 adds the executor, the facade method, the operation, the locators and their
  census, the runbook and the sanctioned-site entry, and updates the tests that pin them
  (`AuditEntry_Is_AppendOnly`, the platform-scope convention census and a test that no
  other operation can call `RedactAuditAsync`). `IAuditStore_Exposes_No_Update_Method` and
  the audit schema grant tests do not change, because no store method or grant is added.
  The comment in `AuditEntry_Is_AppendOnly` and the catalogue text that name the
  redaction handler, the purge job and a module locator as three sanctioned sites are
  corrected in the same commit, since the one executor replaces them.
- Criterion C18's "redelivery finds the existing result audit" is met by the one platform
  transaction of § 8, not by a delivery inbox, and the phase text is corrected to say so.
- Corrections owed to [Audit Subsystem § 10](../architecture/31-audit-subsystem.md),
  [Data Protection](../architecture/23-data-protection.md), the Audit module spec and the
  catalogue ride the acceptance commit.

## References

- [ADR-0033](0033-audit-durability-model.md),
  [ADR-0038](0038-cross-cutting-port-and-event-contracts.md),
  [ADR-0044](0044-audit-write-path.md),
  [ADR-0046](0046-durable-event-delivery-and-recovery.md),
  [ADR-0101](0101-platform-operator-authority.md),
  [ADR-0107](0107-data-request-fulfillment-and-account-closure.md)
- [Audit Subsystem](../architecture/31-audit-subsystem.md),
  [Audit Coverage](../standards/18-audit-coverage.md)
