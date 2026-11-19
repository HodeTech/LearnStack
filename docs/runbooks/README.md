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
