# Account removal client: the confined deletion credential

How an operator provisions, verifies, rotates and, after a leak, contains the one
credential that can delete a person's identity-provider account: the realm client the
closing step of a global account closure uses. Decided in
[ADR-0107 § 7](../decisions/0107-data-request-fulfillment-and-account-closure.md). The
closure's own steps and their operator routes are in
[Data erasure](data-erasure.md) and the closure runbook; this document is about the
credential. There is **no route of its own**: the credential is used by one caller, the
closure's Finalize, through one port method.

## What exists

| Item | Value |
|---|---|
| Port | `IIdentityProviderAccountRemoval`, one method: delete the account whose provider subject is given |
| Default | `RefusingIdentityProviderAccountRemoval`: every call answers refused, nothing is sent |
| Adapter | `KeycloakAccountRemoval`, selected when `AccountRemoval:ClientSecret` is configured |
| Realm client | `learnstack-account-removal` in the `learnstack` realm, role `realm-management/manage-users` only |
| Secret | `KEYCLOAK_ACCOUNT_REMOVAL_SECRET` in the realm's private source; the API reads the same value as `AccountRemoval__ClientSecret` |
| Calls | One token request, then `DELETE users/{id}` for a canonical lowercase UUID; nothing else |
| Resilience | `Resilience:accountRemoval`, fixed: no retry, 5-second call ceiling, breaker on, one call at a time |

The adapter refuses a malformed subject and a missing or unusable secret before any
request leaves the process, and a token wider than the role before the deletion is
sent. It never logs or audits the subject or the secret. With no secret configured every
closure stops at its last step and nothing reaches the provider.

## What the credential can do

**`manage-users` is not deletion-only.** It is a realm-wide role in the one realm every
tenant shares. A leaked secret lets its holder, in every tenant's realm:

- **create** users and **update** them, including the profile attributes the realm
  declares admin-editable (the identity attributes among them);
- **reset credentials**, and so sign in as anyone who has no second factor;
- **delete** any user, and so any person's account.

The integration proof `AccountRemovalProviderTests` shows the create, update and
credential reset against the pinned provider rather than asserting them. Whether the
role also lists users was not exercised; assume it can. It does **not** reach
identity-provider brokers, clients, realm settings or authentication flows: the same
proof shows each answers `403`.

The adapter confines what LearnStack sends: one deletion of one validated identifier.
It cannot confine what the credential could do if it leaves LearnStack. ADR-0107 § 7
accepts that, with three mitigations: the secret store, rotation by this runbook and
the provider's own admin-event stream. The committed realm does not enable that stream,
and its retention is
[Phase 11](../roadmap/phase-11-production-hardening.md)'s; until then the review in
[Leak response](#leak-response) rests on the provider's own logs.

**Fine-grained admin permissions are not relied on.** The pinned Keycloak image
(`26.7.5`, the digest in `infra/compose/dev.yml`) lists `admin-fine-grained-authz` at
versions `v1` and `v2` (read from `kc.sh start --help` on that image), and the
development compose file enables the feature. Keycloak's version 2 permissions grant
`manage` on a user set, and `manage` covers update as well as delete; there is no
deletion-only scope to adopt, and none was exercised here. The adapter therefore
holds `manage-users`. If a later pinned Keycloak exposes a scope that permits deletion
alone, ADR-0107 § 7 gains a dated amendment, and the realm client, the adapter's token
check and this section change together.

## Provision it

Development provisioning is the realm reconciler's: `make dev`, `make seed` and
`make demo` create the client when it is absent and verify it exactly when present.

1. Add `KEYCLOAK_ACCOUNT_REMOVAL_SECRET` to the selected private source: 16 to 1,024
   bytes, not `**********`, not beginning `${`, and distinct from the web, admin,
   event-reader and federation admin secrets. Add the same value as
   `AccountRemoval__ClientSecret` for the API.
2. Start the provider. Compose refuses to start without the variable; the import
   carries only the placeholder `${KEYCLOAK_ACCOUNT_REMOVAL_SECRET}`.
3. Run the reconciler. It refuses an existing client that differs (`secret_conflict`,
   an extra role, scope or mapper) and never repairs or rotates one.

A production realm is provisioned by Phase 11. Whatever provisions it creates the client
with exactly these properties, and nothing broader:

- a confidential client with a service account, and **no** standard flow, implicit
  flow, direct access grants, redirect URIs, web origins, default or optional client
  scopes, and **full scope off**;
- one protocol mapper emitting `realm-management` client roles into the access token
  only, as `resource_access.realm-management.roles`;
- the service account holding the single client role `realm-management/manage-users`
  and no realm role, group or other client role, and that role as the client's only
  scope allowance.

Store the secret in the deployment's secret provider under `AccountRemoval:ClientSecret`
and set `Authentication:Tenant:Issuer` to the realm's issuer; the adapter reads both and
selects no other target.

## Verify the confined client

The adapter refuses a token that is not exactly this client's, but check the client
yourself after provisioning and after any provider upgrade.

1. In the realm console the client has service accounts on, every browser flow off, no
   client scopes and full scope off. Its service account has one role mapping,
   `manage-users`, and no group.
2. Request a client-credentials token and read its claims. The secret goes in a file,
   never on the command line or in shell history:

   ```bash
   curl --silent --data-urlencode grant_type=client_credentials \
     --data-urlencode client_id=learnstack-account-removal \
     --data-urlencode client_secret@account-removal.secret \
     "$ISSUER/protocol/openid-connect/token" \
     | jq -r .access_token \
     | jq -R 'split(".")[1] | @base64d | fromjson
         | {azp, realm_access, resource_access}'
   ```

   `azp` is `learnstack-account-removal`, `realm_access` is absent or has no roles, and
   `resource_access` has one entry, `realm-management`, whose roles are exactly
   `["manage-users"]`. Delete the secret file afterwards.
3. The token is refused for brokers and clients: `GET .../identity-provider/instances`
   and `GET .../clients` with it answer `403`.

## Rotate it

Rotate on schedule, on any suspicion, and whenever someone who held the value leaves.

1. Generate the new value (same bounds as above).
2. Set it at the provider: regenerate or replace the client secret in the realm console.
3. Set the same value in the private realm source (`KEYCLOAK_ACCOUNT_REMOVAL_SECRET`),
   the secret store and the API's `AccountRemoval__ClientSecret`, then restart the API.
4. Run the reconciler: it verifies the provider's secret equals the private source and
   refuses with `secret_conflict` if they differ. It never rotates.

Between steps 2 and 3 the provider refuses the old secret and every closure stops with
a refused answer. Nothing is lost: a closure's last step is repeated after rotation.

## Leak response

Treat a suspected leak of `AccountRemoval:ClientSecret` as a realm-wide credential
compromise.

1. **Stop the credential.** Disable the client in the realm console (new tokens stop at
   once). A token already issued works until it expires; the committed realm's access
   token lifespan is 900 seconds.
2. **Rotate.** Follow [Rotate it](#rotate-it) with a new value, then re-enable.
3. **Review the realm.** From the last time you know the secret was held only by
   LearnStack, list the users the client's service account created, updated, disabled,
   deleted or gave a new credential, from the provider's admin events or its logs.
   Treat every account whose credentials or profile attributes changed in that window
   as compromised: reset its credentials and required actions, and check its identity
   attributes against the LearnStack binding.
4. **Account for deletions.** A user the client deleted that no closure explains is a
   data event: check the retained closure requests and the tenants' audit rows for the
   subject, and report it under your incident process.
5. **Closures in flight** are unaffected by a rotation: repeat their last step.

## When a closure's last step does not finish

The closure's Finalize reports what the provider answered. The adapter's answers mean:

| Answer | Meaning | What to do |
|---|---|---|
| refused | Nothing changed. The secret is missing, unusable or wrong; the client is disabled or broader than `manage-users`; the subject is not a provider user identifier; or the provider answered `401`, `403` or another `4xx` | Fix the cause (configuration, secret, client), then repeat the step. Repeating without a repair answers the same |
| unavailable | Nothing was deleted: the provider was unreachable, answered `5xx` or `429`, the breaker is open after repeated faults, or another deletion is in flight | Wait (the breaker closes after its break duration, 30 seconds by default) and repeat |
| uncertain | The deletion may have happened and its answer was lost | Look the person up in the realm console with the e-mail you confirmed the request against, then repeat the step. A repeat reports the account absent, which is success |

There are no automatic retries by design: a repeat is the operator's, so a lost answer
is never hidden behind a quiet success. A missing account is success.

## What is not here

- **Realm provisioning in production.** Phase 11 owns it; this document states the
  properties it must produce.
- **The admin-event stream and its retention.** Phase 11 owns them; see above.
- **Reading or listing realm users from LearnStack.** The adapter reads and lists none;
  the confirmation of a closure request against the provider's e-mail is yours, in the
  realm console.
