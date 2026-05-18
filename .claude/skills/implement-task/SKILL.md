---
name: implement-task
description: >
  Take a substantive task from intent to ship: scope, inspect, plan, implement,
  self-check, run linters + tests, update documentation, commit, and produce a
  reviewable summary plus a follow-up review-agent prompt. USE FOR: any non-trivial
  change that produces real artefacts and needs verification + docs + a clean
  commit (a feature, a refactor larger than one file, a schema change, an
  architectural addition, a new skill, an ADR + downstream updates). DO NOT USE
  FOR: a one-line typo fix (just edit), a quick exploratory question (chat),
  scoping-only sessions before the work starts (use `start-task`), pure code
  review of someone else's work (use `code-review`), or auditing the corpus for
  standards conformance only (use `standards-check`).
---

# Implementing a task end-to-end

## Purpose

Run a substantive task with the discipline the project expects: scope it
correctly, understand the affected surface fully, implement against the
standards corpus and Clean Code principles, prove it works, keep documentation
synchronised, commit cleanly, and hand the reviewer everything they need —
without rushing and without leaving "I'll fix it later" debt behind.

This skill exists because the same long instruction prompt is otherwise repeated
verbatim every time. Loading this skill is the equivalent of that prompt; the
workflow steps below are its expansion.

## When to use

- The user has handed you a substantive task and said "implement it" /
  "geliştir" / "ship it" without further breakdown.
- The work spans multiple files, requires tests, and will need documentation
  updates.
- The work needs to leave behind a clean commit and a review-ready summary.

## When not to use

- A trivial one-line edit. Just make the edit.
- Pure scoping. Use [start-task](../start-task/SKILL.md) instead and stop when
  the plan is clear.
- A review of work you didn't author. Use
  [code-review](../code-review/SKILL.md).
- A standards-conformance walk over an existing diff or doc. Use
  [standards-check](../standards-check/SKILL.md).
- A documentation-only change with no code impact. Edit + use
  [commit-and-pr](../commit-and-pr/SKILL.md) directly.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Task description | Yes | The user's request. |
| Acceptable scope | No | If unstated, infer from the task and confirm during the plan step. |
| Allowed to commit / push | Yes | Default: commit on the current branch unless the user said otherwise. Never `--force` to `main`. |
| Allowed to open a PR | No | Default: no, unless the user asks. |

## Workflow

The ten steps below are mandatory. Skipping any step is the bug this skill
exists to prevent.

### Step 1 — Scope and alignment

Run the [start-task](../start-task/SKILL.md) workflow first. Reading-not-skimming
the right docs, checking phase fit, walking the hard rules, picking the
**specific** workflow skill(s) you'll need (e.g.
[add-tenant-owned-entity](../add-tenant-owned-entity/SKILL.md),
[add-mediatr-handler](../add-mediatr-handler/SKILL.md), …).

Output at the end of Step 1: a one-paragraph problem statement in your own
words, the phase the task belongs to, the standards that govern the change, and
the specific skill(s) you'll invoke for the implementation.

### Step 2 — Inspect and understand

Read every file the change will touch **before** modifying any of them. Trace
the dependencies one hop out (who calls this; who reads from this table; what
integration events flow). If a related ADR is Accepted, read it. If a related
architecture doc explains the intent, read it.

Do **not** rely on the file system's recent state to be canonical; if `git log`
shows recent edits, read the latest commits' messages to understand the
direction.

The check at the end of Step 2: you can state, without re-opening files, what
the change will touch and which adjacent surfaces it must not regress.

### Step 3 — Plan

State the plan to the user, briefly:

1. What you're going to do (1–3 sentences).
2. Which files / directories you'll touch (paths only).
3. Which validation you'll run before declaring done.
4. Any open assumption that, if wrong, would invalidate the plan.

Ask for confirmation **only** when the plan would touch:

- More than one module's `Domain` namespace.
- An Accepted ADR.
- The Hub contract surface.
- A customization aggregate's schema in a non-additive way.
- A destructive migration.

For routine work, state the plan and continue.

### Step 4 — Implement

Implement against the project's standards corpus and Clean Code principles.
Concretely:

- **Standards first.** [docs/standards/](../../../docs/standards/README.md) is
  the authority. The skill from Step 1 walks you through the specific rules
  that govern this surface; honour them.
- **Clean Code defaults.** Small functions, single responsibility, descriptive
  names, no dead code, no commented-out blocks, no "TODO later" without a
  date + owner. The
  [code-review § Refactor checklist](../code-review/SKILL.md) is the same lens
  you'd apply to someone else's diff; apply it to your own as you go.
- **Don't rush.** The cost of one careful pass is far less than the cost of
  two sloppy passes plus the fix. If a step needs thirty extra seconds of
  thought, take them.
- **Don't drift.** If you find yourself wanting to refactor surrounding code,
  stop. Bug fixes don't need cleanup; one-shot operations don't need helpers.
  Note the candidate for a follow-up issue, do not bundle it here.
- **No half-finished implementations.** A change that compiles but leaves a
  handler unwired, a permission unregistered, or a migration without RLS is
  worse than no change at all.

### Step 5 — Self-check against the existing structure

After writing the code, **before** running the toolchain, check that the
change fits the project structurally. Run the
[standards-check](../standards-check/SKILL.md) workflow over your own diff —
it's the same checklist a reviewer would apply. Focus areas:

- **Cross-module references.** Did you add a dependency the architecture-test
  set forbids?
- **Defense-in-depth.** Every new tenant-owned entity has all four layers
  (marker + filter + RLS + isolation test)?
- **Adjacent updates.** Does the change make any other file go stale? Module
  audit matrix, permission matrix, glossary, ADR cross-link, roadmap entry?
- **Existing structures' contract.** Did you change a contract another module
  depends on without bumping the version (integration event) / following the
  deprecation cycle (permission key, i18n key)?

If anything is off, fix it now, not at review time. **Sonradan dönüp tekrar
tekrar fix yapma**.

### Step 6 — Run linter + every relevant test suite

Use [run-tests-locally](../run-tests-locally/SKILL.md) to walk the suites that
match the change:

- Backend changes → `dotnet build`, `LearnStack.Tests.Architecture`,
  `LearnStack.Tests.Unit`, `LearnStack.Tests.Integration` (Testcontainers).
- Frontend changes → `pnpm lint`, `pnpm test`, `pnpm test:a11y`,
  Lighthouse on representative routes if the public surface changed.
- Documentation-only → broken-link sweep + `docs/analysis/` residual scan
  (see Step 7's link audit).

A failing test is the work telling you to keep going. Fix the underlying issue;
do not mark `[Skip]`. Architecture tests are **non-skippable** by policy.

The check at the end of Step 6: every relevant suite is green and the
architecture-test set ran (not skipped by accident).

### Step 7 — Update every related document

The corpus is single-source-of-truth; this step keeps it that way. Walk the
list, update what applies, leave nothing stale:

- **Roadmap.** Did this phase deliver something new? Update the phase doc's
  Deliverables / Completion Criteria. Did this complete a phase exit
  criterion? Note it.
- **Standards.** Did the change introduce a new rule or refine an existing
  one? Update the relevant standard + cite the ADR (use
  [write-adr](../write-adr/SKILL.md) if the rule deserves one).
- **Architecture docs.** Did the conceptual shape change? Update the relevant
  `NN-topic.md`.
- **Module specs.** Audit matrix (`docs/modules/<m>/audit.md`), permission
  matrix (`docs/modules/<m>/permissions.md`).
- **Glossary.** Did the change introduce a new project-specific term? Use
  [update-glossary](../update-glossary/SKILL.md).
- **README.md / CHANGELOG.md.** Only if user-visible direction changed.
- **Skills.** Did a new mechanical workflow emerge that another agent will
  re-run? Open a follow-up issue to add a skill (don't bundle here unless
  trivial).
- **Link audit.**
  ```bash
  # Broken relative-link sweep over the changed docs (run from repo root):
  for f in $(git diff --name-only --diff-filter=AM | grep '\.md$'); do
      grep -oE '\]\(\.\.?/[^)]+\)' "$f" \
        | sed -E 's/.*\((.+)\).*/\1/' \
        | while read link; do
              target="$(dirname "$f")/$link"
              [ -e "$target" ] || echo "BROKEN: $f → $link"
          done
  done

  # docs/analysis/ residual scan:
  git diff --name-only --diff-filter=AM | xargs grep -l 'docs/analysis/' 2>/dev/null
  ```

### Step 8 — Commit cleanly

Run [commit-and-pr](../commit-and-pr/SKILL.md). Concretely:

- Conventional Commits `type(scope): subject`. Scope = primary module / route
  group / doc area; multi-module changes use `Module: A, B, C` trailer.
- Subject imperative, ≤ 72 chars.
- Body: one short paragraph saying *why* (not *what* — the diff is what).
- Trailers in this order: `ADR:` (if applicable), `Module:` (if multi-module),
  `I18n:` (if applicable), `Co-Authored-By:` (the AI assistant doing the
  work — Claude / Codex / both).
- HEREDOC every multi-line message.

Do **not** push or open a PR unless the user has asked. Default = local
commit only.

### Step 9 — Produce a detailed summary

After the commit, write a summary the user (and a reviewer) can read in two
minutes:

1. **What changed** — bullet list of additions / modifications / deletions
   grouped by area (backend / frontend / docs / infra / skills).
2. **Why it serves the user** — one or two sentences per area in **Turkish**
   ("bu fazda yapılanların ne işe yaradığını Türkçe olarak açıkla"). This is
   not a literal translation of *what*; it's an explanation of *why*,
   tailored to the user.
3. **Verification done** — which suites ran green; which manual checks ran;
   any deferred follow-up.
4. **Open follow-ups** — anything you noticed during implementation that
   warrants its own task. Linked clearly so they don't drop on the floor.

### Step 10 — Compose a review-agent prompt

The user will dispatch a separate agent to review this work. Compose the
prompt for that agent so it doesn't have to re-learn the context. Use the
[code-review § Generating a review-agent prompt](../code-review/SKILL.md)
template. The prompt must:

- Set the project context (LearnStack PaaS, pre-implementation phase, the
  hard-rules summary).
- Point at the specific commit / branch / file list under review.
- Tell the review agent to walk security + bug / potential bug + optimisation
  + refactor + standards conformance.
- Define the output format (Blocker / Major / Minor / Suggestion).
- Insist on context-awareness — read the surrounding docs, don't review in
  isolation.

Deliver the prompt as a code-fence in the response so the user can copy and
dispatch it.

## Validation (Definition of Done)

The task is **not** done until every box below is checked:

- [ ] Step 1's problem statement is written and the phase fit is confirmed.
- [ ] Step 4's implementation walks the right workflow skill(s) without
      shortcut.
- [ ] Step 5's structural self-check is green.
- [ ] Step 6's linter + test runs are green (or the failure is documented +
      tracked).
- [ ] Step 7's adjacent docs are updated; broken-link audit is clean;
      `docs/analysis/` residual scan is clean.
- [ ] Step 8's commit is on the right branch with the right trailers.
- [ ] Step 9's summary is delivered to the user — including the Turkish
      "ne işe yaradı" explanation.
- [ ] Step 10's review-agent prompt is provided as a copy-paste block.

If any box is unchecked, the task is in progress, not done. Surface what's
left honestly; don't claim "complete" prematurely.

## Common pitfalls

- **Rushing the inspect step.** The next eight steps cost an order of
  magnitude more when Step 2 is sloppy. Read the files; don't pattern-match
  from filenames.
- **Implementing before planning.** Step 3 takes two minutes and saves twenty.
- **Skipping the self-check (Step 5).** The reviewer will find what you'd
  have found in 60 seconds. Save the round trip.
- **Treating the test suite as optional.** Architecture tests are
  non-skippable for a reason. If a test fails because of *your* change, that
  is the test telling you the change is wrong, not a flaky test.
- **Stale documentation.** A change that doesn't update the matrix /
  glossary / standard it touches creates rot that other agents amplify.
  Twenty minutes here saves hours later.
- **Force-push or amend on `main`.** Forbidden. New commits only.
- **Skipping the Turkish summary.** The user uses it; not optional.
- **"I'll fix it in the next commit."** No. Fix it now or open a clearly
  scoped follow-up issue with a link from this commit.
- **Submitting without the review prompt.** The user explicitly wants the
  review agent's prompt as part of the deliverable. Don't drop it.
