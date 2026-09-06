# Customization — Permission Matrix

Per [Permission Standards](../../standards/19-permissions.md), which names this
file. Part of the [module spec](README.md).

**No permission keys yet.** The matrix below is a forward declaration in the
`{module}.{resource}.{action}` form with the closed action set of
[Permission Standards](../../standards/19-permissions.md). Registration runs
through `IModule.RegisterPermissions(IPermissionRegistry)`, and neither type
exists in `backend/src` yet; the catalogue lands with the Identity module in
[Phase 03](../../roadmap/phase-03-identity-admin.md), together with `Role`,
`Permission` and the lighting-up of the `AuthorizationBehavior` shell.

| Resource | read | write | delete | admin | Default role grants |
|----------|:----:|:-----:|:------:|:-----:|---------------------|
| `ContentType` | ✓ | ✓ | – | ✓ | tenant-admin: read+write; content-editor: read. **No `delete`**: a revision content rows pin cannot be removed, and a draft nothing references is removed by the same `write` that made it |
| `LevelTaxonomy` | ✓ | ✓ | – | ✓ | tenant-admin: read+write; content-editor: read. Same reason for the missing `delete` |

**`admin` is publication, and that is the split worth having.** Authoring a
schema and making it the live answer for a key are two decisions — the module
models them as two commands for exactly that reason — and they carry different
blast radii. A wrong draft constrains nothing: it exists, it is addressable, and
no content row is validated against it. A wrong publication retires the incumbent
in the same transaction and every subsequent write of that content type is
validated against the new shape. So `write` covers `Register` and the additive
revision, and `admin` covers `Publish`.

**Four handlers exist and all are deliberately unauthorized.** Reachability
stands in for authorization exactly as it does in
[Tenancy](../tenancy/permissions.md), and for the same two reasons: none has an
HTTP endpoint, so the only callers are `LearnStack.Tools.Seeder` and — from
[Phase 02c](../../roadmap/phase-02c-hub-foundation.md) — the Hub over
`/api/internal/*`; and every one takes its tenant from the context and never from
the request, so a caller cannot name another tenant even without a permission
check. The database refuses the write, and
`Write_With_Foreign_TenantId_Is_Rejected_By_WithCheck` is what proves it rather
than this paragraph.

**The Admin Studio editors are what make these keys load-bearing.** The
`TenantContentType` editor lands in
[Phase 04](../../roadmap/phase-04-cms-media-pages.md), the `TenantLevelTaxonomy`
editor in [Phase 05](../../roadmap/phase-05-education-learning-content.md), and
[Phase 06](../../roadmap/phase-06-renderer-admin-studio.md) consolidates them —
each one is the first caller with a human behind it, and therefore the first that
needs a key rather than an absence of endpoints.

**Scope is Tenant, not Organization.** Both aggregates are tenant-owned
tenant-wide: a content type is what the whole tenant's content looks like, and a
branch publishing its own shape would make one organization's rows unreadable to
another's renderer. There is no `organization_id` on either table and no
restrictive write guard, which is
[Database Standards § Table classes](../../standards/05-database.md)'s answer for
this class rather than an omission.
