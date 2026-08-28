---
name: commit-and-pr
description: >
  Format LearnStack commits and pull requests to the project's conventions —
  Conventional Commits + AI co-author trailer + scope-correct PR body. USE FOR:
  preparing a commit, opening a PR, picking the right scope for a doc-only change,
  adding the AI co-author trailer correctly. DO NOT USE FOR: deciding whether a
  change is ready to commit (that's a code-review concern, not a commit-format
  concern), force-pushing, or amending an Accepted ADR (write a new ADR instead).
---

# LearnStack commit + PR conventions

## Purpose

Make every commit and PR conform to the project's
[git-workflow standard](../../../docs/standards/14-git-workflow.md) and
[code-review standard](../../../docs/standards/17-code-review.md) so reviewers can
move fast and `git log --grep` stays useful.

## When to use

- Producing one or more commits at the end of a unit of work.
- Opening a pull request against `main`.
- Adding trailers (ADR refs, module list, AI co-author).

## When not to use

- Squash-rebasing months-old branches — read 14-git-workflow first; the rules below
  assume a single coherent change.
- Reverting (use `git revert` and let the auto-message stand).
- Force-pushing to `main` — never, regardless of skill.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Staged diff | Yes | What you're about to commit. |
| Touched modules | Yes | Determines the `scope` of the commit and the `Module:` trailer. |
| Relevant ADRs | If applicable | Becomes the `ADR:` trailer. |

## Workflow

### Step 1: Pick `type(scope): subject`

Conventional Commits style. Allowed types:

| Type | When |
|------|------|
| `feat` | New behavior the user can observe. |
| `fix` | Bug fix. |
| `refactor` | Internal restructure with no user-visible change. |
| `perf` | Performance improvement. |
| `test` | Adding or fixing tests only. |
| `chore` | Build / CI / tooling. |
| `docs` | Documentation-only change. |
| `build` | Project / SDK / package version bumps. |

Scope:

- For backend changes: the module name (`identity`, `enrollment`, `classroom`, …) or
  `kernel` for shared kernel changes.
- For frontend: `web` (the tenant-facing app), or a route group (`studio`, `portal`,
  `public`).
- For doc-only changes: one of `architecture` / `decisions` / `standards` / `roadmap`
  — or omit for cross-cutting doc changes.

Subject:

- Imperative mood: "add", "fix", "rename" — not "added", "fixes", "renaming".
- ≤ 72 characters, no trailing period.

### Step 2: Write the body

For non-trivial commits, the body says **why** (one paragraph) and lists what
changed if the diff is large. Wrap at 72 cols.

### Step 3: Add trailers

Trailers go at the **end** of the body (after a blank line). The supported set:

| Trailer | When |
|---------|------|
| `ADR: NNNN[, NNNN]` | The commit implements or derives from one or more ADRs. **Bare numbers** — `ADR: 0040, 0003` — never `ADR: ADR-0040`: `git log --grep='ADR: 0017'` is what the trailer exists for, and the prefixed form does not match it. A `feat` commit that creates a schema an ADR decides carries this too; the trailer is about the *derivation*, not the commit type. |
| `Module: <list>` | Multi-module change; list every module touched. |
| `I18n: <keys>` | Added / renamed / removed user-facing i18n keys. |
| `Co-Authored-By: …` | Required for AI-assisted commits. |

### Step 4: AI co-author trailer

Pick the trailer that matches the agent that did material work:

- Claude Code session:
  `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`
- OpenAI Codex session:
  `Co-Authored-By: Codex Opus 4.7 (1M context) <noreply@anthropic.com>`

Multiple assistants: include one trailer per assistant.

### Step 5: HEREDOC the message

Always pass the message via a HEREDOC so newlines and quotes survive:

```bash
git commit -m "$(cat <<'EOF'
feat(enrollment): grant entitlement on order paid

Wire OrderPaidV1 → Enrollment.GrantEntitlement via the outbox so paid
access shares the per-learner entitlement read path with free access.

ADR: 0010
Module: Enrollment, Billing
Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

### Step 6: Pull request

The PR body uses the structure in
[14-git-workflow.md § Pull Requests](../../../docs/standards/14-git-workflow.md).
Minimum sections:

```markdown
## Summary
- <1-3 bullet points: what changed and why>

## Test plan
- [ ] <Concrete checks the reviewer can run>

## Risk
- <Anything that could ripple: schema, contract, perf>

🤖 Generated with [Claude Code](https://claude.com/claude-code)
```

If the change touches the Hub HTTPS surface, the entitlement projection, or any
tenant-isolation boundary, the PR description **must** call it out under "Risk" and
reference the relevant ADR.

### Step 7: Open the PR

```bash
gh pr create --title "<imperative subject>" --body "$(cat <<'EOF'
... body above ...
EOF
)"
```

The PR title follows the **same** rules as the commit subject (imperative mood,
≤ 72 chars, type-scoped where applicable).

## Validation

- `git log --pretty=format:'%s' main..HEAD` shows commits in
  `type(scope): subject` form.
- Every AI-assisted commit carries the `Co-Authored-By` trailer.
- `git log --grep='ADR: 0017'` finds commits that implemented ADR-0017.
- PR title matches the convention; PR body has Summary + Test plan + Risk.
- No commit on the branch force-pushes after review starts.

## Common pitfalls

- **Wrong scope.** A backend change touching three modules has `scope = primary` and
  `Module: A, B, C` trailer — not a comma-separated scope.
- **Past-tense subject.** "Added" / "Fixed" is wrong; use the imperative.
- **Skipping `--no-verify`.** Pre-commit hooks exist for a reason. If a hook fails,
  diagnose; don't bypass.
- **Amending a published commit.** If a hook fails during commit creation, fix the
  issue and create a **new** commit — don't `--amend` because the commit didn't
  happen, so `--amend` would modify the wrong (previous) commit.
- **Force-pushing to `main`.** Never.
- **Trailer order.** Trailers always go after the body, separated by a blank line.
  Out-of-order trailers don't fail anything but they look broken.
