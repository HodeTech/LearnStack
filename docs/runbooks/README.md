# Runbooks

Operational procedures, one per failure or privileged act. Each names the signal, the
diagnostic access, the safe recovery and what is not recoverable. They live under
`runbooks/` per [Documentation Standards](../standards/13-documentation.md). Phase 03
begins this directory with the platform operator runbooks;
[Phase 11](../roadmap/phase-11-production-hardening.md) completes the set.

| Runbook | Use it when |
|---|---|
| [Platform operator access](operator-access.md) | An operator needs a token, a grant, or an answer to a 401, 403 or 503 |
| [Killswitch](killswitch.md) | A capability must be switched off for every tenant during an incident |
| [Tenant onboarding](tenant-onboarding.md) | A production tenant must be provisioned, mapped to a host and given its first administrator, or an administrator must be recovered |
| [Identity issuer rebind](identity-issuer-rebind.md) | The tenant identity provider keeps its realm and users but changes address, and the stored issuer must follow without re-keying anyone |
| [Operator recovery](operator-recovery.md) | An event or delivery is dead-lettered, a job is quarantined, and an operator must inspect it or reset one delivery or outbox row under a fence |
| [Federation operator](federation-operator.md) | A tenant's identity-provider broker is orphaned, unreadable or nonconforming, and an operator must report it, remove it or disable it, and an account review is owed |
| [Data erasure](data-erasure.md) | A tenant erasure awaits its privileged step and an operator must redact the person's audit rows and complete it, or a data request is overdue, stuck or has an unpurged artifact |
| [Data export](data-export.md) | The data export key ring must be rotated or a key was lost or exposed, an export request never finishes, a download hit a ticket bound or lost its last chunk, an expired artifact was not purged, the export definitions are being deployed, or a process dump was taken |
| [Account closure](account-closure.md) | A person's closure request is past its window and must be confirmed with them and begun, a Begin was refused or answered incomplete, or a begun closure must be finalized, its groups are pending or its provider step did not finish |
| [Account removal client](account-removal-client.md) | The credential that deletes a person's identity-provider account is being provisioned, verified or rotated, it may have leaked, or a closure's last step answered refused, unavailable or uncertain |
