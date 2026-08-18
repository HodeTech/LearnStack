# 14 — Git Workflow Standards

**Status:** Active
**Derives from:** [ADR 0007 — Documentation Language and Conventions](../decisions/0007-documentation-language-and-conventions.md). Release-tagging policy is tracked as an open ADR draft in [decisions/README.md](../decisions/README.md).

Branches, commits, pull requests, reviews.

## Branching

- Main branch: `main`. Always deployable.
- Feature branches: `feat/<short-slug>`, `fix/<short-slug>`, `chore/<short-slug>`, `docs/<short-slug>`.
- Long-lived branches are discouraged; rebase regularly on `main`.
- Hotfix branches: `hotfix/<short-slug>` against `main`; production cherry-pick possible.

## Commits

We follow a relaxed Conventional Commits style:

```
<type>(<scope>): <imperative summary>

<body — optional, wraps at 72 cols>

<footer — optional>
```

| Type | Use |
|------|-----|
| `feat` | New behavior |
| `fix` | Bug fix |
| `refactor` | Code change without behavior change |
| `docs` | Documentation only |
| `test` | Test only |
| `chore` | Tooling, deps, scaffolding |
| `perf` | Performance improvement |
| `build` | Build system / CI |
| `revert` | Revert an earlier commit |

Examples:
- `feat(education): add CourseVersion publish flow`
- `fix(classroom): correct token TTL calculation`
- `docs(standards): expand security headers section`

Rules:
- Imperative mood ("add", not "added" or "adds").
- ≤ 72 chars subject.
- Each commit is a meaningful unit; squash messy WIP before opening the PR.
- For multi-module changes, pick the primary module as `scope`; list the others in the body.
- Co-author tags allowed (`Co-authored-by: ...`).
- AI-assisted commits **must** include a `Co-Authored-By` trailer naming the assistant
  that materially contributed. The canonical form is the agent's product name +
  underlying model + context length, with `<noreply@anthropic.com>` as the email
  unless the agent vendor specifies otherwise. Examples:
  - `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`
    (default for Claude Code sessions in this repo).
  - `Co-Authored-By: Codex Opus 4.7 (1M context) <noreply@anthropic.com>` (when the
    assistant is OpenAI Codex / a Codex-derived agent acting on `AGENTS.md`).
  - One trailer per assistant when multiple were used in the same commit.

### Trailers

Trailers go at the end of the commit body and make `git log --grep` queryable:

| Trailer | When |
|---------|------|
| `ADR: NNNN` | The commit implements or derives directly from a specific ADR. Multiple ADRs allowed (`ADR: 0004, 0010`). |
| `I18n: <keys>` | The commit adds, renames, or removes user-facing i18n keys. List the keys or namespaces. |
| `Module: <list>` | Multi-module commits — names of all modules touched. |
| `Co-Authored-By: ...` | Standard co-authorship trailer. Required for AI-assisted commits. |

Example:

```
feat(classroom): wire LiveKit join token issuance

The join-token endpoint now returns a scoped token plus the LiveKit URL,
backed by ILiveClassProvider.CreateJoinTokenAsync.

ADR: 0005
Module: Classroom, Identity
Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
```

## Pull Requests

### Size

- Aim for < 400 lines of diff per PR.
- Prefer many small PRs over one massive one.
- Schema migrations land in their own PR.

### Title

Same format as a commit subject. The title is also the squash-merge commit message.

### Description

Every PR description includes:

```markdown
## Summary
<1–3 bullets explaining intent>

## Approach
<short — only when non-obvious>

## Tests
<unit, integration, manual, screenshots>

## Migration / Rollback
<for schema or config changes>

## Related
<links to issues, ADRs, standards>
```

For UI changes, attach screenshots or a Loom.

### Checks

PRs do not merge unless:
- CI is green.
- Architecture tests pass.
- Tenant-isolation tests pass.
- Coverage report is attached (informational).
- At least one approving review (**deferred while the repository has one active contributor** — see [CONTRIBUTING § Branch protection](../../.github/CONTRIBUTING.md) for the dated decision and its trigger) (two for security-sensitive changes).
- Migration is reviewed by a database-owner reviewer if schema changes.

### Merge Strategy

- **Squash merge** by default. The PR title becomes the squash commit message.
- **Rebase merge** allowed when commit-by-commit history is meaningful and clean.
- **Merge commits** discouraged.

## Review Etiquette

See [17-code-review.md](17-code-review.md) for full review standards. Highlights:

- Critique code, not people.
- Distinguish blockers from suggestions.
- Reference standards/ADRs instead of personal taste.
- Approve when you are willing to ship the change yourself.

## Hotfixes

- Branch from `main`.
- Smallest viable diff.
- Approval and CI as usual.
- Post-incident: an ADR or postmortem may be added.

## Reverts

- A revert is its own PR. Don't force-push a revert onto a public branch.
- The revert PR description references the original PR and the reason.

## Tagging and Releases

- Production releases are tagged `vYYYY.MM.DD.<n>` or `vMAJOR.MINOR.PATCH`. Specifically: ADR-pending.
- Release notes generated from PR titles + bodies.
- Frontend and backend may release independently.

## Forbidden

- Force push to `main` or to a shared feature branch.
- Skipping `--no-verify` on commits (pre-commit hooks must pass).
- Bypassing CI to merge.
- Committing secrets, lock files for the wrong package manager, or large binaries.
- "WIP" or unrelated commits squashed into a feature PR.
- Long-running personal branches with no rebase for > 1 week.
