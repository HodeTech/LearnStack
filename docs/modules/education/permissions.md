# Education Permission Matrix

**Status:** Accepted design — 2026-09-14, with the [module spec](README.md).

P02d-2 Step 3 implements six unrouted commands and two verification queries.
`EducationOperationPolicySource` binds these requests to finite Seed authority
through the [live contract](../../standards/19-permissions.md#live-permission-enforcement-contract).
They have no HTTP endpoint or assignable permission registration. Tenant and
organization derive from execution context; database isolation independently
protects every table. Human-facing authoring remains Phase 05.

The current resource/action matrix uses the
[Permission Standards template](../../standards/19-permissions.md#permission-matrix-template):

| Resource | read | write | delete | admin | Registered scope | Default role grants |
|---|:---:|:---:|:---:|:---:|---|---|
| `Course` | — | — | — | — | None | None |
| `Lesson` | — | — | — | — | None | None |

Here **—** means no assignable Education permission exists. This describes the
current authoring surface, not the actions or grants a later authoring decision
must choose.

| Surface | Registration and reachability |
|---|---|
| Course and lesson seed writes | P02d-2; no HTTP route and no registered permission yet |
| Anonymous content reads | P02d-4 Step 3 implemented GET/HEAD marked reads; accepted ADR-0076 virtual public definitions below preserve matching host ceiling, normal filters/RLS and publication predicates; credentials never widen visibility |
| Authenticated authoring | Phase 05; uses the permission infrastructure from Phase 03 |

The matrix gains permission keys, scopes and default-role grants with the corresponding
command-surface decision, under [Permission Standards](../../standards/19-permissions.md),
before any permission is registered. The reachability table grants no capability and
introduces no assignable permission key ahead of its decision.

Accepted [ADR-0076](../../decisions/0076-live-scoped-authorization.md) classifies
the existing anonymous reads with Tenant `education.public_course.read` and
`education.public_lesson.read`. These Portal Public definitions are virtual and
unavailable as stored Role grants. They add no Membership, Role template or
authoring capability and require no identity/grant lookup for credential-free
reads. Their policy classification preserves the physical READ ONLY boundary.
[Permission Standards](../../standards/19-permissions.md) owns the virtual class;
the [Phase 03 Status block](../../roadmap/phase-03-identity-admin.md) owns its
runtime implementation and qualification. Acceptance changes no current route
or grant.

The [accepted P02d-2 contract](README.md#p02d-2-accepted-writer-contract) names six
implemented unrouted write commands and contextual verification queries;
none admits unresolved context or anonymous/public invocation. The accepted access
policy does not create a permission or a grant evaluator. Authenticated authoring
and protected learner reads retain their Phase 05 and Phase 07 owners.
