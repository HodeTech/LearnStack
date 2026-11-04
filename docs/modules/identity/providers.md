# Identity — Provider Adapters

**Derives from:** [ADR-0093](../../decisions/0093-verified-invitation-acceptance-and-handoff.md).
This is the Accepted bounded invitation provider contract; the
[Phase 03 Status block](../../roadmap/phase-03-identity-admin.md) owns implementation
and qualification. Acceptance is not evidence of a configured production sender.

## Invitation SMTP

| Boundary | Contract |
|---|---|
| Ordinary staging | `IInvitationEmailStager`, called only by declared Identity invitation writers; encrypted provider envelope and typed job share their existing business/audit transaction |
| Typed job port | Identity Application.Contracts `IInvitationEmailOperation : IBackgroundJobExternalOperation<InvitationEmailPlan, InvitationEmailOutcome>`; exactly Execute/Reconcile |
| Job adapter | Identity Infrastructure `InvitationSmtpOperation`; prepare/finalize remain exact ordinary Application requests |
| SMTP transport | Common `IEmailProvider`, implemented in common Infrastructure using MailKit/MimeKit 4.18.2; SDK types/exceptions stay inside the adapter namespace |
| Resilience | `IProviderResilience<IEmailProvider>`, configuration `Resilience:email`; retry disabled, finite timeout/circuit-breaker/bulkhead configuration; actual task/socket cleanup remains owned |
| Capacity | Tenant-wide `identity.invitation_email`, ceiling 4, no new Hard limit; existing external queue/pool/worker counts |
| Custody | Common Infrastructure SQL-only provider envelope/attempt records and the exact provider connector; no Identity aggregate or additional invitation root |

The [database contract](../../standards/05-database.md#invitation-smtp-provider-storage)
is the sole owner of encryption/key-ring format, provider tables and grants, the
two SECURITY DEFINER functions, receipt authority, connector bounds and uncertain
COMMIT handling. The [infrastructure contract](../../standards/20-infrastructure-stack.md#bounded-invitation-smtp-consumer)
owns composition with the existing job runtime. Neither general Phase 08a
notification orchestration nor a tenant-selectable SMTP provider is introduced.

## Deployment configuration

- One operator-configured SMTP host/port and sender apply to this deployment.
  Message content and caller input cannot select a host, credentials, sender or
  alternate transport. Validate addresses and reject header injection.
- Production requires authenticated TLS with certificate/hostname validation;
  implicit TLS or required STARTTLS is explicit configuration, without plaintext
  fallback or opportunistic downgrade. Missing or invalid configuration fails
  qualification; a log-only/null sender cannot report success.
- Development uses the existing local Mailpit endpoint. A development plaintext
  connection is an explicit Development-only composition choice; the module never
  branches on DeploymentMode. A reachable local capture service proves local SMTP
  submission, not an external mailbox delivery or production configuration.
- Resolve credentials and the named encryption key ring at startup through
  `ISecretProvider`, following the existing fixed-for-process secret lifecycle.
  Keys/credentials do not enter checked-in settings, DTOs, jobs, logs or audit.
  Retain decryption versions while provider envelopes need them; a lost key is a
  visible custody failure, not permission to generate another token silently.
- Disable SMTP protocol logging and reject provider exception/message/body text
  at the boundary. Expose only closed outcome/status diagnostics. Preserve the
  SDK-exception translation and sensitive-message controls from the common
  provider-adapter standards.

## Delivery and recovery meaning

`queued`, `submission_accepted`, `failed` and `unresolved` describe provider work;
only the Invitation acceptance owner can report an accepted membership offer.
A final SMTP 250 establishes submission acceptance, never inbox delivery.
Definite no-effect temporary failures may use the existing bounded job retry;
permanent rejection is final. Positive pre-send cancellation fences the old
provider claim and remains distinguishable from framework no-ExecuteIntent proof.

Reconcile reproduces the exact durable provider-attempt receipt without sending.
A crash or lost final response before that receipt remains Unknown with its
reservation retained. A stable Message-ID is identification, not deduplication.
An explicit administrator resend creates new generation/token/job and invalidates
the old acceptance token; it cannot release an old unknown reservation or promise
that previously started mail will not arrive. Duplicate or delayed notices never
authorize a second membership grant.

Provider qualification includes final-250-before-runtime-checkpoint restart,
crash-before-provider-receipt quarantine, fenced delayed Execute, exact terminal
receipt replay/conflict refusal, late original-owner evidence, uncertain provider
COMMIT, no hidden retry after possible acceptance, retained actual cleanup and
cross-tenant/org ciphertext/function refusal. Local qualification and production
SMTP configuration remain separate evidence.

## Configuration surface and source bindings

The API resolves `InvitationEmail:KeyRing` through `ISecretProvider` once at
startup. Its JSON shape is `{ "active": "version", "keys": { "version":
"base64-encoded-32-byte-key" } }`; one to four distinct keys and a declared active
version are required. This is a secret value, never a checked-in example key.
SMTP settings are `InvitationEmail:Smtp:Host`, `Port`, `Sender`, and `Tls`
(`RequiredStartTls` or `ImplicitTls`); `Username` and `Password` resolve through
`ISecretProvider`. `DevelopmentPlaintext` is admitted only by Development host
composition. Missing configuration prevents startup. No connection is made by
configuration validation.

Two active definition keys, `identity.invitation_email_tenant.v1` and
`identity.invitation_email_organization.v1`, bind four exact ordinary roots:
`PrepareTenantInvitationEmailCommand`, `FinalizeTenantInvitationEmailCommand`,
`PrepareOrganizationInvitationEmailCommand`, and
`FinalizeOrganizationInvitationEmailCommand`. Their shared parameters/plan are
three GUIDs (InvitationId, GenerationId, SpoolId); the outcome is only ReceiptId.
The scope-specific roots share logical audit operations, not request authority.

The stager and provider admission verify the actual database logging settings
listed in [Database Standards](../../standards/05-database.md#invitation-smtp-provider-storage).
The invitation creation/reissue owner must qualify privacy before its first
person-field write. A clean provider connection does not retroactively protect an
earlier business INSERT. The staging implementation accepts only those exact
writer identities. Create and Reissue call the real stager/scheduler/envelope
writer on their announced business owner. A failed MUST write rolls their work
back together; no test-only producer exemption supplies production authority.
Admin status uses only the authorized root's current JobId. Missing-job data is
finite Unavailable; SQL failure/private timeout fails the whole audited read with
503 and rollback-only state, while actual caller cancellation remains cancellation.
A page shares a five-second status budget under the parent request deadline.

## Setup, key rotation and restoration runbook

The deployment operator owns these actions through the existing
[secret configuration](../../standards/12-infrastructure.md#secrets-management)
boundary. Never place keys, credentials or captured invitation messages in a
ticket, terminal transcript, repository, test report or deployment log.

1. Generate a cryptographically random 32-byte encryption key in the approved
   secret store. Give it a new version identifier and configure the key ring and
   fixed SMTP settings above. The default configuration provider reads these
   exact configuration keys; environment variables use `__` in place of `:`.
   Development plaintext requires the Development environment and explicit TLS
   mode; production uses authenticated required TLS.
2. Apply the [database privacy settings](../../standards/05-database.md#database-diagnostic-privacy-qualification)
   before enabling invitation writes. Qualify independent collectors separately.
   Startup configuration validation makes no SMTP connection and proves neither
   credentials nor delivery. Use the packet's isolated provider controls before
   enabling the real invitation producer.
3. For rotation, retain every old key required by retained envelopes, add a
   distinct key/version and select it as active. Restart through the normal
   owned shutdown/startup path because the key ring is fixed for the process.
   New envelopes use the new version; old envelopes keep their original version.
   Never reuse a version for different key bytes. Four retained versions are the
   limit, not permission to discard an older required key to make room.
4. Retire a version only after an authorized retention assessment proves no
   retained envelope or required restoration backup needs it. Application
   credentials intentionally cannot enumerate provider custody. Phase 11 owns
   the production retention/purge workflow; expiry or a terminal job alone is
   not key-retirement evidence.
5. Restore missing keys from the protected backup under their original version
   identifiers, then restart. Never substitute a new key or regenerate the
   encrypted link. An unclaimed job can continue through normal admission;
   already-Sending/Unknown work remains uncertain until authoritative recovery.
   Restoring a key is not proof that SMTP never accepted an earlier attempt.
   [P03-9](../../roadmap/phase-03-identity-admin.md) owns privileged unresolved-work
   recovery. Explicit reissue neither releases that reservation nor prevents
   already-prepared old mail from arriving.
