# Education Permission Matrix

**Status:** Accepted design — 2026-09-14, with the [module spec](README.md).

No Education command, endpoint or permission registration exists in P02d-1. P02d-2
introduces unrouted seed commands; their reachability boundary follows the
[Tenancy precedent](../tenancy/permissions.md), with tenant and organization derived
from the execution context. Database isolation is already required for every table.

The current resource/action matrix uses the
[Permission Standards template](../../standards/19-permissions.md#permission-matrix-template):

| Resource | read | write | delete | admin | Registered scope | Default role grants |
|---|:---:|:---:|:---:|:---:|---|---|
| `Course` | — | — | — | — | None | None |
| `Lesson` | — | — | — | — | None | None |

Here **—** means no registered permission exists in P02d-1. This describes the current
surface, not the actions or grants a later authoring decision must choose.

| Surface | Registration and reachability |
|---|---|
| Course and lesson seed writes | P02d-2; no HTTP route and no registered permission yet |
| Anonymous content reads | P02d-4; explicit public surface, no authoring access |
| Authenticated authoring | Phase 05; uses the permission infrastructure from Phase 03 |

The matrix gains permission keys, scopes and default-role grants with the corresponding
command-surface decision, under [Permission Standards](../../standards/19-permissions.md),
before any permission is registered. The reachability table grants no capability and
introduces no permission key ahead of its decision.
