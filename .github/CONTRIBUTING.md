# Contributing to LearnStack

The full engineering corpus lives in [`docs/`](../docs/) — this file is the
short, branch-protection-and-PR-hygiene companion.

## Branch protection (settings on `main`)

Configure these in **GitHub → Settings → Branches → Branch protection rules
→ Branch name pattern: `main`** so the corpus matches what GitHub enforces:

> **Two settings are deferred — maintainer decision, 2026-08-10.** While
> LearnStack has one active contributor, the live `main` rule sets
> **Require approvals: 0** and leaves **Do not allow bypassing** *off*. A second
> approver on a single-contributor repository blocks every merge, and admin
> enforcement with no second admin blocks the only person who could unblock it.
> The settings below are the **target state**, not the current one.
>
> **Trigger:** a second active contributor gains write access. That is the
> condition that makes the rule mean something, and it is self-evidencing —
> nobody has to remember to check a date. Activating it is the same discipline
> as the deferred status checks below: set both settings, and drop this note in
> the same pull request.
>
> Everything else in this section — required status checks, linear history, no
> force pushes, no direct pushes — is live today. Only the two named settings
> are deferred.
>
> **Two required-check edits are outstanding**, both flagged in the list below:
> the `meta` check is required under a name nothing reports any more, and
> `backend integration (Testcontainers)` runs on every pull request and is not
> required at all. The first blocks every merge; the second gates nothing.

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
    - `meta (compose + commit hygiene + link audit)`
    - `secret scan (leakwatch)`
    - `meta (compose + commit hygiene + link audit)` — ⚠️ **the live rule still
      requires the pre-rename name** `meta (commit hygiene + link audit)`, which
      nothing reports. GitHub matches by name, so that required check never
      arrives and **every** pull request sits at "Expected — waiting for status to
      be reported". Re-require it under the current name; the job itself is green.
      This is the failure the warning below describes, in the direction that
      blocks rather than the one that waves through.
    - `backend integration (Testcontainers)` — **activated in Phase 02a Packet 6**
      with the four-role provisioning suite, one packet earlier than planned:
      Packet 6 ships the first Docker-bound test and is therefore the packet that
      has to split them. The job carries no `vars.ENABLE_*` gate and no placeholder
      step, so it already runs on every pull request. **Adding it to the live
      branch-protection rule is the one remaining edit**, and it is a repository
      setting rather than a file in this repo — until it is made, the job runs and
      gates nothing.
  - Deferred checks. Each is gated on a repository variable (`vars.ENABLE_*`,
    unset by default — a constant `if: false` is rejected by actionlint).
    Activating one is **four edits, in the same pull request wherever possible**:
    set the variable in **Settings → Secrets and variables → Actions → Variables**,
    replace the placeholder step with the real one, **rename the job** to drop the
    `(deferred …)` suffix, and add the new name both to this list and to the live
    branch-protection setting. Setting the variable alone leaves a job that runs
    but gates nothing.
    - `openapi diff (deferred to Phase 02d)` — **Phase 02d**, with the first real
      `/api/v1/*` read endpoints.
    - `lighthouse budget (deferred to Phase 02d)` — **Phase 02d**, with the first
      content-bearing public pages.

    GitHub matches required checks **by name**, so the rename is the dangerous
    half: a renamed check that nobody re-required is a check that no longer blocks
    anything, and the PR still shows green.
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
  `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`
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
make test           # unit + arch + contract + integration + vitest
```

`make test` starts Testcontainers since Phase 02a Packet 6, so it needs a Docker
socket and takes noticeably longer than it did. The Docker-bound cases are split
out by `[Trait("Requires","Docker")]`; to skip them, run
`dotnet test backend/LearnStack.slnx --filter "Requires!=Docker"`, which is
exactly what CI's `backend` job runs.

The pre-commit hook (activated by `make install`) runs, on staged files only:
`dotnet format` on `*.cs`; prettier on JS / TS / JSON / Markdown **under
`frontend/`**; `next lint --fix` on JS / TS under `frontend/apps/web` — the one
workspace with a `lint` script, so this is exactly what `pnpm -r lint` covers in
CI; and, when the binary is on PATH, `leakwatch scan fs <staged-file>`. So the
three commands above are mostly a sanity check. There is deliberately no
`make secret-scan` to pair with them — the hook and CI are the scanner's only
runners, and CI re-runs every check as a hard gate, so a bypassed local commit
will fail the PR build.

The **commit-msg** hook (same activation) checks the subject line against the two
rules in [Git Workflow § Commit messages](../docs/standards/14-git-workflow.md):
Conventional Commits shape, and 72 characters. It enforces exactly what CI's
`meta` job enforces and nothing more — the point is not a new rule but where the
failure lands. CI catches an over-long subject after a push, and a subject is
only fixable by rewriting history; locally it is a retry. Merge, revert and
fixup subjects are generated by git rather than authored, and are skipped. The
`ADR:` and `Module:` trailers are deliberately not checked: whether a commit owes
one depends on judgement a hook does not have, and a hook that guessed is a hook
people disable.

Prettier stops at the `frontend/` boundary, and the root `.prettierignore`
is what enforces it in your editor — `.vscode/settings.json` maps `[markdown]`
to the Prettier extension, which honours that file, so neither format-on-save
nor an explicit Format Document reaches `docs/`. Keep it that way: prettier's
Markdown printer rewrites hard-wrapped prose — it joins blockquote lines and
reads `*` inside a code span as emphasis — and it corrupted a roadmap record
doing exactly that. Nothing verifies its output outside the frontend workspace
anyway, since no CI job runs prettier and `make format` invokes it from
`frontend/`.

Leakwatch builds before v1.6.0 only accept a directory target. CI pins v1.8.0,
which accepts files; the hook detects an older *local* build and skips the scan
with an upgrade hint rather than failing your commit. CI scans the whole tree
either way.

The secret scanner is [Leakwatch](https://github.com/HodeTech/leakwatch)
— MIT licensed, verifier-equipped, hybrid Aho-Corasick + regex + entropy
detection engine. Config lives at `.leakwatch.yaml` + `.leakwatchignore`
at the repo root. Install once for the local pre-commit scan (CI runs it
regardless, this is just earlier feedback):

```bash
brew install HodeTech/tap/leakwatch        # macOS (Homebrew)
# or:
go install github.com/HodeTech/leakwatch@v1.8.0
```

The version is pinned, and to the same one CI installs. `@latest` on the old
`cemililik/` path resolves to v1.5.0 — the module renamed its path at v1.6.0, so
nothing newer is installable there — and v1.5.0 does not understand
`leakwatch:ignore`, so a developer following an unpinned instruction would get a
scanner that disagrees with the one gating their pull request.

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
- Edit an Accepted ADR's body outside the two bounded mechanisms in
  [Documentation Standards § Correcting and Amending ADRs](../docs/standards/13-documentation.md)
  ([ADR-0041](../docs/decisions/0041-correcting-false-statements-in-accepted-adrs.md)):
  an inline erratum by default, in-place replacement only for a canonical
  artifact for reuse, both only for a statement false when it entered the record,
  and both owing a dated Amendment in every Accepted ADR the diff changes. A
  changed decision is a new ADR that supersedes the old one, with the same number
  rule preserved.
