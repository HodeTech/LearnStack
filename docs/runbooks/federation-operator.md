# Federation operator: orphaned and broken brokers

How an operator finds the identity-provider brokers a tenant's federation left
orphaned, unreadable or nonconforming, and removes or disables one. Decided in
[ADR-0099 Amendment 10](../decisions/0099-confined-tenant-federation.md) and
implemented as Amendment 11. There is **no dashboard** and **no way to list or remove
a realm user**: this is an API and this document.

## What exists

| Route | Does | Permission |
|---|---|---|
| `GET /api/platform/v1/federation/brokers` | Reports one page of brokers that need an operator (a **report**) | `platform.federation.read` |
| `POST /api/platform/v1/federation/brokers/{alias}/remove` | Deletes an orphaned or unreadable owned broker at the provider (a **remove**) | `platform.federation.admin` |
| `POST /api/platform/v1/federation/brokers/{alias}/disable` | Sets `enabled=false` on a nonconforming or unreadable owned broker, at the provider only (a **disable**) | `platform.federation.admin` |

Authentication, the token profile and how a grant is given are in [Platform operator
access](operator-access.md). The served operator document, `platform-v1`, is the
contract for these routes, and it is the only client there is.

All three run on `ConnectionStrings:PlatformAdmin` and on the federation admin client
(`Federation:ClientSecret`; see the [adapter
specification](../modules/identity/providers.md#tenant-federation-adapter)). With no
client secret configured every call answers `503` at once and nothing is sent to the
provider. The report needs the Identity migration
`20261120160000_grant_platform_federation_projection` applied; without it the first
report fails with a permission error in the API's log.

## Which brokers the platform may touch

A broker is the platform's only when **both** hold:

- its alias is `lsf-` and 32 lowercase hexadecimal characters, the form LearnStack
  derives from a connection identifier, and
- its owner marker parses strictly to a tenant and a connection whose derived alias is
  that alias.

Everything else in the realm (an unmarked instance, a marker naming another connection,
an alias that is not LearnStack's) is **counted and never named**: the report's
`ignored` says how many, and no act accepts such an alias.

A broker the platform may touch is reported in one or two classes (`orphan` combines
with either of the others):

| Class | Meaning |
|---|---|
| `orphan` | No live connection row names it: the row is absent (`rowState` `absent`) or a tombstone (`deleted`) |
| `unreadable_owned` | The marker proves it LearnStack's and its representation no longer reads as an admitted OIDC or SAML broker |
| `nonconforming` | It reads, and differs from the fixed profile or carries a mapper ([Fixed broker profile](../modules/identity/providers.md#fixed-broker-profile)) |

A broker that conforms and has a live row is not reported.

## What is not here

- **Listing or removing realm users.** No LearnStack credential may enumerate or remove
  them ([ADR-0099 Decision Q1](../decisions/0099-confined-tenant-federation.md)). The
  account review below is yours, with your own realm authority.
- **Removing a broker that reads while a live row names it.** Disable it here if it
  must stop now, and let the tenant's own *Delete* retire the connection with its
  broker; a remove is refused (`errors.connection`) while a live row names a broker
  that reads. A broker that does *not* read is the exception, below.
- **Repairing a broker.** There is no rewrite from the fixed profile on this surface.
  The tenant's next readback corrects its own broker; an unreadable one is removed
  here, because the tenant's own Delete treats it as foreign and cannot.
- **A broker whose tenant is archived or deleted with a live row.** That is
  [P03-10](../roadmap/phase-03-identity-admin.md)'s.
- **A provider-side fence.** A tenant's create that lands after a sweep produces a new
  orphan, which the next report finds.

## When to look

- A tenant reports a sign-in option that does nothing, or a provider that was removed
  in the tenant's own screen and still offers a button.
- A broker in the provider's console carries a LearnStack alias (`lsf-` and 32 hex
  characters) that no connection in the tenant's own screen explains.
- After a failed tenant create or delete whose intent row has no completion row
  (see [Check it worked](#check-it-worked)).
- As a periodic sweep: the report is read-only and cheap, so run it page by page.

## Report

Each page is one entered platform scope, so each page writes one
`platform.admin_scope.enter` audit row with the reason `federation_report`, your
attribution and your ticket. It does not say which page you read. Pass the ticket as
`?ticket=`: 1 to 64 letters, digits or `.` `_` `:` `/` `#` `-`, and percent-encode `#`
as `%23`.

```sh
curl -s -H "Authorization: Bearer $TOKEN" \
  "$API/api/platform/v1/federation/brokers?limit=50&ticket=CHG-123"
```

- `limit` is 1 to 100 (default 20; a larger value is clamped, and zero or less is
  refused with `errors.limit`). Only `limit`, `cursor` and `ticket` are accepted; any
  other parameter is refused with `errors.query` and nothing is read.
- A page is one slice of the provider's own listing, `scanned` instances long, and the
  answer holds `items` (the brokers on that slice that need attention), `scanned`,
  `ignored`, `mappersUnread` and a `pageInfo` whose `nextCursor` you send back as
  `cursor`. **An empty `items` with a `nextCursor` is not the end**: the slice held
  nothing to report. Read until `hasNext` is false. A cursor belongs to this route.
- Each item carries `alias`, `tenantId`, `connectionId`, `classes`, `rowState`,
  `protocol`, `enabled` and `mappers`. `mappers` is the count read on a live broker that
  reads and conforms, where only a mapper can make it drift; `null` means it was not
  asked (an orphan, an unreadable or a drifted broker already has its class).
- **`mappersUnread` is not clean.** An extra mapper is drift only a mapper read shows,
  so the page reads the mappers of its healthy-looking live brokers one call at a time
  within ten seconds. `mappersUnread` counts those it could not read (the provider
  failed, its circuit was open or the time ran out): they are unknown, not cleared, and
  no item names them. When it is not zero, read the page again, with a smaller `limit`
  if it keeps running out of time.
- The realm is read **first** and the connection rows second, because a tenant writes
  its row before it creates its broker. A tenant that creates a broker while your page
  is being read cannot be taken for an orphan. A broker whose class is `orphan` on one
  page can still gain a row before you act; the remove re-reads the row.
- A report the provider cannot answer is `503`, never an empty realm.

## Choose: remove or disable

| The broker is | Do |
|---|---|
| `orphan`, readable or not | **Remove** it |
| `unreadable_owned`, with or without a live row | **Remove** it: the tenant's own Delete cannot reach a broker that does not read, so nothing else can. The tenant's row stays as it was and its next readback finds the broker absent, after which the tenant's own Delete completes. Disable it instead only if the loss of its federated links must wait |
| `nonconforming` with a live row | **Disable** it if it is a risk now; the tenant's next readback corrects it, and until then the sign-in option fails closed. The tenant's own Delete can remove it, because it reads; a remove here is refused |
| healthy with a live row | Neither: a disable is refused with `not_applicable` |

A **remove** also deletes the broker's federated links, which is why [an account
review](#account-review-after-a-remove) is owed. A **disable** changes only `enabled`
at the provider and the tenant's own row is left to its next readback. Neither writes
the tenant's connection row.

## Act

A ticket is optional and is checked, and **recorded only on the platform scope's entry
row** (a row of the platform sentinel, like the report's): the intent and completion
rows are readable by the tenant, so they never carry one. The body is `{}` or
`{"ticket":"CHG-123"}`.

```sh
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  "$API/api/platform/v1/federation/brokers/$ALIAS/remove" -d '{"ticket":"CHG-123"}'
```

Use `.../disable` the same way.

Each act proves the owner marker at the provider and reads the connection row (a live
row refuses a remove at once and writes no tenant row). It then writes an **intent**
row; a remove reads the row again, as the last check before the delete. It changes the
provider, reads it back, and writes a **completion** row on a bounded budget of its own,
so a client that leaves while the provider works does not lose the record.

| Answer | Meaning |
|---|---|
| `200`, `outcome: applied` | The provider's change was made and confirmed. `accountReviewOwed` is `true`: a remove deleted the broker's federated links, and a disable closed it as a sign-in option, so people who signed in only through it need another way in |
| `200`, `outcome: already_applied` | The broker was already absent (remove) or already disabled (disable). Nothing was sent, and when it was so at the first read **no tenant row was written**; if it changed between that read and the provider call, the intent and completion rows exist and the completion says `already_applied` |
| `404` | A disable named an alias no instance has |
| `409`, `errors.owner` = `lockey_not_owned` | The instance's marker does not name a tenant and a connection of LearnStack's, or its row names another tenant. Nothing was touched |
| `409`, `errors.connection` = `lockey_live_connection` | A live connection row names a broker that reads, so a remove is refused. Disable it if it must stop now, and the tenant deletes the connection |
| `409`, `errors.class` = `lockey_not_applicable` | A disable found a healthy owned broker with a live row |
| `409`, `errors.provider` = `lockey_provider_refused` | The provider definitively refused the change. Nothing changed |
| `400` | The alias is not a derived one, or the body or the ticket is malformed. A refusal at the gate carries no per-field errors |
| `401`, `403`, `413`, `415`, `429` | As in [operator recovery](operator-recovery.md#act): the credential, the grant (`platform.federation.admin` for an act), the body size or type, or the request budget |
| `503`, `audit_unavailable` | The intent row could not be written or committed, or the completion of a change that did not happen could not be: nothing changed, though the intent row may stand alone. Retry after the delay; error line 7105 is in the API's log |
| `503`, `dependency_unavailable` | The provider or the database could not be reached before any change was sent. Nothing changed. Retry after the delay |
| `503`, `dependency_unavailable` with `errors.outcome` = `lockey_outcome_unknown` | A change was sent and its answer was lost, the request was aborted while it ran, or the completion that records it could not be written. It **may have landed**: see [An unknown outcome](#an-unknown-outcome) |
| `500` | An unexpected failure or a deployment defect (a refused credential, a missing grant): the API's log names it |

A client that never received an answer (a timeout, a gateway error) is in the same
position as an unknown outcome.

## An unknown outcome

There is no provider-side fence, so reconcile by reading, in this order:

1. Read the report again, or read the one alias. A remove that landed leaves no
   instance with that alias; a disable that landed shows `enabled` false.
2. If it did not land, repeating the act is safe: it re-proves the marker and the row.
3. To tell your act from another operator's, read the tenant's audit rows by the
   `correlationId` of the failed response, as in [Check it worked](#check-it-worked).

A repeat of an act that had landed answers `already_applied` with `accountReviewOwed`
false, and writes no row; that alone cannot say whose act it was, and it does not say a
review is owed. **After an unknown remove that shows the broker gone, treat the account
review as owed.** The record is the `remove_attempt` row, with no completion row or
with an `uncertain` one.

## Account review after a remove

The provider deletes **the broker's federated links** with the broker and keeps the
accounts. A person who signed in only through that provider still has an account, and
has lost the way into it. LearnStack cannot list or repair these accounts, and
Keycloak keeps link events for seven days only, so the review is a realm
administrator's, in the provider's own console under their own authority:

1. If the removal is not urgent, **note the linked users before you remove**: the
   console lists a user's identity-provider links.
2. After the remove, review the accounts that depended on it: whether each has another
   credential or sign-in route, and whether its sessions must be ended.
3. Tell the tenant's owner. No owner notification channel exists before
   [Phase 08a](../roadmap/phase-08a-assessment-notifications.md), so this is by hand.

An `accountReviewOwed` of `true` in the answer, and the same member in the completion
row's `after_state`, are the reminder. Nothing in LearnStack closes the review.

## Check it worked

1. Read the report again: the broker is gone (a remove) or shows `enabled` false (a
   disable).
2. Read the audit trail. The rows are in the **tenant's** trail: they name the tenant
   the owner marker names, even when no tenant row exists, with no actor, your
   attribution in `metadata.operator` and **no ticket**. There is no audit API: read
   them as the application role announcing the tenant, by the response's
   `correlationId`, with the query and the digest recipe of [Tenant
   onboarding](tenant-onboarding.md#reading-the-record).
   - `platform.federation.remove_attempt` or `platform.federation.disable_attempt`,
     outcome `indeterminate`, the **intent**, committed before the provider was
     touched. Its `before_state` holds the broker's `classes` and the connection
     row's `rowState`.
   - `platform.federation.remove` or `platform.federation.disable`, outcome `success`,
     the **completion**, whose `after_state` holds the closed `outcome` (`applied`,
     `already_applied`, `refused`, `not_owned`, `unavailable` or `uncertain`) and
     `accountReviewOwed`.
3. An **intent row with no completion row** is a completion that was lost: the provider
   may have been changed. The next report resolves it, as step 1 does.

A refusal before the intent (an absent broker, an unowned alias, a live row seen at
the first read, an unavailable provider) writes **no tenant row**: the scope's entry
row, `platform.admin_scope.enter` with the reason `federation_remove` or
`federation_disable`, records that an act was attempted and nothing is known to name a
tenant. A live row found only at the re-read after the intent writes both tenant rows,
and the completion says `refused` with the cause `live_connection`.

## A 503 that does not clear

Find the line in the API's host log by the response's `correlationId` (warning 7110;
it carries the operation and whether the outcome is unknown):

- **The provider.** The federation admin client's token or an admin call failed. The
  adapter never logs provider text, so the line names no cause: read the provider's own
  server log, and the counter below (`outcome` `unavailable`). The adapter's circuit may
  be open after repeated failures; it closes by itself. A `Federation:ClientSecret` that
  is not configured, or one the realm refuses, never clears until it is fixed.
- **The database.** A transient fault of `ConnectionStrings:PlatformAdmin` (it cannot
  connect, a timeout) is a `503`, and the line's exception holds the cause. A refused
  credential or a missing grant is not transient: it is a `500` and the log names it.

## Signals

`learnstack_platform_operator_federation_total{operation,outcome}` counts every call
that reached the owner: a refused parameter, alias or body (a `400`) and a refused
credential are not counted, and an audit failure is counted by the audit-failure
counter. `operation` is `platform.federation.report`, `platform.federation.remove` or
`platform.federation.disable`; `outcome` is `ok`, `invalid_cursor`,
`applied`, `already_applied`, `not_found`, `not_owned`, `live_connection`,
`not_applicable`, `refused`, `unavailable` or `uncertain`. A rising `uncertain` means
completions are being lost or the provider is failing mid-change; a rising
`not_owned` is an operator naming aliases that are not LearnStack's. The admission,
refusal and audit-failure counters are in [Platform operator
access](operator-access.md#signals). Each report page logs the scope-entry line 7001, as
a killswitch act does.

## Limits

- **Pages are slices of the realm.** A realm with many instances takes many pages;
  there is no filter by tenant, class or alias. A page whose instances are unusually
  large (over 1 MiB together) is refused as unavailable: lower `limit`.
- **The mapper reads are sequential.** Each costs a token and a call, so a page of many
  healthy brokers can run out of its ten seconds; `mappersUnread` says so.
- **Disable is a read and a write.** The tenant's own write can interleave with it, and
  the tenant's next readback detects the drift
  ([ADR-0099 Amendment 11](../decisions/0099-confined-tenant-federation.md)).
- **A disable sends back what the provider returned.** An unreadable broker cannot be
  rebuilt from the fixed profile, so the operator disable is the one provider write that
  does not build its body from the allow-list. It changes `enabled` and nothing else,
  and keeps the stored secret.

## Not recoverable here

- A broker with no marker, or a marker that is not LearnStack's: the realm
  administrator's, in the provider's console.
- A federated link that a remove deleted.
- A realm user, a session, a credential or an owner notice.
- A dashboard.
