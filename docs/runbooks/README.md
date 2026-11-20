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
