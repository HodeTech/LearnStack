# Education Permission Matrix

**Status:** Accepted design — 2026-09-14, with the [module spec](README.md).

No Education command, endpoint or permission registration exists in P02d-1. P02d-2
introduces unrouted seed commands; their reachability boundary follows the
[Tenancy precedent](../tenancy/permissions.md), with tenant and organization derived
from the execution context. Database isolation is already required for every table.

| Surface | Registration and reachability |
|---|---|
| Course and lesson seed writes | P02d-2; no HTTP route and no registered permission yet |
| Anonymous content reads | P02d-4; explicit public surface, no authoring access |
| Authenticated authoring | Phase 05; uses the permission infrastructure from Phase 03 |

The concrete resource/action/scope and default-role matrix is defined with the command
surface, under [Permission Standards](../../standards/19-permissions.md), before any
permission is registered. This table grants no capability and introduces no permission
key ahead of its decision.
