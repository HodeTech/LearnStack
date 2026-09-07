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
| `ContentType` | ✓ | ✓ | ✓ | – | tenant-admin: read+write+delete; editor: read |
| `ContentTypePublication` | ✓ | ✓ | – | – | tenant-admin: read+write; editor: read |
| `LevelTaxonomy` | ✓ | ✓ | ✓ | – | tenant-admin: read+write+delete; editor: read |
| `LevelTaxonomyPublication` | ✓ | ✓ | – | – | tenant-admin: read+write; editor: read |

**Publication is a sub-resource, because the action set is closed.** Authoring a
schema and making it the live answer for a key are two decisions — the module
models them as two commands for exactly that reason — and they carry different
blast radii. A wrong draft constrains nothing: it exists, it is addressable, and
no content row is validated against it. A wrong publication retires the incumbent
in the same transaction, and every subsequent write of that content type is
validated against the new shape.

The way to say that is
[Permission Standards § Closed Action Set](../../standards/19-permissions.md)'s
own: there is no `publish` action, and "if a verb does not fit, model the verb as
a distinct sub-resource" — its first worked example being "publish a course" →
`education.course_publication.write`. So `customization.content_type.write`
covers `Register` and the additive revision, and
`customization.content_type_publication.write` covers `Publish`. An earlier
version of this matrix put publication on `admin` instead, which read as the same
split and was not one: `admin` means "full control including ownership transfer",
no default role held it, and the result was a matrix in which nobody could
publish anything.

**`delete` is a real action here, and it is soft.** A `Deprecated` revision is
never removed — content rows pin the version they were written against — but a
`Draft` is removable, and the partial index is partial on `deleted_at IS NULL`
precisely so that a retired definition releases its key to a successor.
[Permission Standards](../../standards/19-permissions.md) defines `delete` as
"remove or soft-delete the resource", and [audit.md](audit.md) classifies the
same operation as MUST-audit; a matrix with no `delete` disagreed with both.
Publication has none, because unpublishing is not deletion: the successor's
publish is what retires a revision.

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

**Every key in the `{module}.{resource}.{action}` form.** The resources above are
written PascalCase as the template writes them; the keys they generate are
lowercase snake_case — `customization.content_type.read`,
`customization.content_type_publication.write`,
`customization.level_taxonomy.delete`. `editor` is
[Permission Standards § Built-in Roles](../../standards/19-permissions.md)'s
`Editor`, named there rather than invented here.

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
