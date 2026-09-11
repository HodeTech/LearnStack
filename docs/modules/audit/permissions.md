# Audit — Permission Matrix

Per [Permission Standards](../../standards/19-permissions.md), which names this file.
Part of the [module spec](README.md).

**No permission keys are enforced yet.** The matrix below is a forward declaration in
the `{module}.{resource}.{action}` form with the closed action set of
[Permission Standards](../../standards/19-permissions.md). Registration runs through
`IModule.RegisterPermissions(IPermissionRegistry)`, and neither type exists in
`backend/src` yet; the registry lands with the Identity module in
[Phase 03](../../roadmap/phase-03-identity-admin.md), together with the query API these
keys gate.

| Resource | read | write | delete | admin | Default role grants |
|----------|:----:|:-----:|:------:|:-----:|---------------------|
| `event` | ✓ | – | – | – | tenant-admin: read; org-admin: read, confined to its organization |
| `event_export` | ✓ | ✓ | – | – | tenant-admin: read+write |
| `config` | ✓ | ✓ | ✓ | – | tenant-admin: read+write+delete |

And one Platform-scope key, which is not in the table above because
[Permission Standards](../../standards/19-permissions.md) scopes a key to Platform,
Tenant or Organization and this one is the only Platform key the module declares today:

| Key | Scope | Governs |
|---|---|---|
| `platform.audit.read` | Platform | The cross-tenant query, which runs as `learnstack_platform` through the audited `EnterPlatformAdminScope(reason)` path |

**`event` has no `write`, no `delete` and no `admin`, and the absence is the point.**
Every other resource in the corpus gets the actions its commands need. This one has no
command at all: rows arrive from `PostgresAuditStore` on behalf of the operation being
audited, and the two mutating paths that exist — GDPR redaction, which lands with the
erasure handler in [Phase 03](../../roadmap/phase-03-identity-admin.md), and the retention
purge, which lands in [Phase 11](../../roadmap/phase-11-production-hardening.md) — run as
`learnstack_platform` through `EnterPlatformAdminScope(reason)` and are gated by a
Platform-scope key rather than a tenant-facing one. **That key's name is pending.** Phase 03
registers it with the permission registry and the erasure handler, in the closed action
set [Permission Standards](../../standards/19-permissions.md) fixes, and Phase 11's purge
takes the same key or a sibling Phase 03 names — so no writer of `audit_log` lands with a
permission nobody has declared. A
`write` on `event` would be a key a tenant admin could hold and a database grant would
refuse: `learnstack_app` holds `SELECT, INSERT` on `audit_log` and no request path
inserts through it. Two layers disagreeing about what is permitted is how a permission
matrix stops being read.

**Export is a sub-resource, because the action set is closed.** `export` is not one of
`read | write | delete | admin`, and
[Permission Standards § Closed Action Set](../../standards/19-permissions.md) says a
verb that does not fit becomes a distinct sub-resource. So creating the asynchronous
export job is `audit.event_export.write` and fetching its download URL is
`audit.event_export.read`. The split is not cosmetic: the job reads across a tenant's
whole log and produces a file that outlives the request, so the ability to start one and
the ability to collect one are different grants — and an export whose URL leaked is a
copy of the log outside every policy in this document.

**`config` carries all three write actions and no writer ships with them.** Both runtime
roles hold `SELECT` on `audit_config` in this packet; the `INSERT, UPDATE, DELETE` grant
for `learnstack_app` lands in [Phase 06](../../roadmap/phase-06-renderer-admin-studio.md)'s
migration, beside the Studio command that needs it
([Database Standards § GRANT matrix](../../standards/05-database.md)). The keys are
declared now because the matrix is what Phase 03's registry reads, and a key that
appears at the same time as its command is a key nobody reviewed.

**What `config` write cannot do.** A tenant admin holding `audit.config.write` can
narrow a SHOULD or a MAY and cannot remove a MUST: `ClassifyAsync` applies the override
and then re-applies the in-process catalogue's floor
([ADR-0033](../../decisions/0033-audit-durability-model.md)). This is the one place in
the corpus where a permission and a database grant are both present and still do not
add up to the capability the key's name suggests — deliberately, because an attacker who
compromises one tenant admin must not be able to switch off the detector that would
catch the next cross-tenant probe.

**Scope.** `event_export` and `config` are **Tenant**-scope. `event.read` is registered
Tenant-scope too, for the investigator: a tenant admin reads the trail whole, because an
organization-scoped view would hide exactly the cross-organization act an investigation
looks for. **It is also grantable through an organization-scoped role**, which is how
[Security Standards](../../standards/11-security.md) and the Phase 06 audit viewer give an
`Org Admin` their own organization's rows: per
[Permission Standards § Scope](../../standards/19-permissions.md), an organization-scope
binding requires the holder's organization to match the row's `organization_id`, so that
reader sees its organization's rows and neither another organization's nor the tenant-wide
ones. That confinement is **authorization**, decided by the binding, never a filter the
caller chooses: an organization parameter on the query narrows what a tenant-scope reader
asks for and widens nothing for anyone. [Phase 03](../../roadmap/phase-03-identity-admin.md)
builds the read API and enforces both bindings; nothing reads `audit_log` from a request
until then.
