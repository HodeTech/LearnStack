# Contributing to LearnStack

The full engineering corpus lives in [`docs/`](../docs/) — this file is the
short, branch-protection-and-PR-hygiene companion.

## Branch protection (settings on `main`)

Configure these in **GitHub → Settings → Branches → Branch protection rules
→ Branch name pattern: `main`** so the corpus matches what GitHub enforces:

- **Require a pull request before merging**
  - Require approvals: **1** (raise to 2 once the team grows past two
    active contributors).
  - Dismiss stale approvals when new commits are pushed: **on**.
  - Require review from CODEOWNERS: **off** (no CODEOWNERS file yet).
- **Require status checks to pass before merging**
  - Require branches to be up to date before merging: **on**.
  - Required status checks (the job names from `.github/workflows/ci.yml`):
    - `backend (build + unit + arch + contract)`
    - `frontend (typecheck + lint + build + test)`
    - `meta (commit hygiene + link audit)`
    - `secret scan (leakwatch)`
  - Deferred checks — flip the `if: false` guards in `ci.yml` AND add the
    job name here when the owning phase lands:
    - `backend integration (Testcontainers — deferred)` — Phase 02a.
    - `openapi diff (deferred to Phase 03)` — Phase 03.
    - `lighthouse budget (deferred to Phase 04)` — Phase 04.
- **Require conversation resolution before merging**: on.
- **Require signed commits**: optional (off until the team rolls out signing keys).
- **Require linear history**: on (we use squash-merge or rebase-merge, never bubble).
- **Do not allow bypassing the above settings**: on (admins included).
- **Restrict who can push to matching branches**: off (PRs only — no direct push).
- **Allow force pushes**: off.
- **Allow deletions**: off.

The CI workflow is intentionally fast (~3 min target). If a step exceeds
that budget for two consecutive merges, raise a follow-up issue rather
than skipping the step on `main`.

## Commit messages

Per CLAUDE.md § Commit conventions:

- **Conventional Commits**: `type(scope): subject` with subject in
  imperative mood, ≤ 72 chars.
- AI-assisted commits carry the trailer
  `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`
  (replace the model name when authoring with a different assistant).
- `docs(scope)` for doc-only commits; scope ∈ `architecture | decisions |
  standards | roadmap` or omitted for cross-cutting changes.

## Pull requests

- Title mirrors the primary commit's subject.
- Description has three sections:
  1. **What** — bullet list of changes grouped by area.
  2. **Why** — one paragraph; link to the ADR / phase / issue.
  3. **Verification** — what suites you ran locally, what manual checks
     you walked.
- Link the related ADR / phase doc with relative paths (`../docs/...`).

## Local checks before pushing

```bash
make install        # one-time per clone: deps + git hooks
make lint           # dotnet format --verify + ESLint
make typecheck      # tsc --noEmit
make test           # unit + arch + contract + vitest
```

The pre-commit hook (activated by `make install`) runs `dotnet format` +
prettier + ESLint + (if installed) `leakwatch scan fs <staged-file>` on
staged files — so the lint / typecheck / test / secret-scan pass above is
mostly a sanity check. CI re-runs every check as a hard gate, so a
bypassed local commit will fail the PR build.

The secret scanner is [Leakwatch](https://github.com/cemililik/Leakwatch)
— MIT licensed, verifier-equipped, hybrid Aho-Corasick + regex + entropy
detection engine. Config lives at `.leakwatch.yaml` + `.leakwatchignore`
at the repo root. Install once for the local pre-commit scan (CI runs it
regardless, this is just earlier feedback):

```bash
brew install cemililik/tap/leakwatch      # macOS (Homebrew)
# or:
go install github.com/cemililik/leakwatch@latest
```

If Leakwatch flags an intentional dev credential, prefer:

1. **Inline ignore** at the literal — `# leakwatch:ignore` (or
   `# leakwatch:ignore:<detector-id>` for a targeted skip) at the end
   of the line carrying the dev credential. Lowest blast radius.
2. **`.leakwatchignore`** path entry — for whole files where every
   value is dev-only (env templates, the LiveKit / Coturn confs).
3. **`.leakwatch.yaml` config tweak** — last resort; document the why.

## Never

- `git push --force` to `main`. Branch protection blocks it; do not work
  around it.
- Bypass the pre-commit hook (`--no-verify`) for anything but a documented
  emergency — CI will catch it and the PR will fail.
- Edit an Accepted ADR's Decision section. Open a new ADR that supersedes
  it, with the same number rule preserved.
