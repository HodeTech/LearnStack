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
