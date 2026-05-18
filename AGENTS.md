# Working in this repository — agent guidance

**This file is a redirect.** The single source of truth for project guidance, hard
rules, conventions, and documentation layout is [CLAUDE.md](CLAUDE.md). Read that
file first; everything in it applies to **every** AI agent operating on this
repository, not only to Claude Code.

`AGENTS.md` exists because some agent runtimes (notably OpenAI Codex and tools that
follow the `AGENTS.md` convention) look for that filename specifically before falling
back to `CLAUDE.md`. The repository keeps both filenames so neither runtime is left
without a guide.

## Differences from CLAUDE.md

There are no rule differences. The only thing that varies between agent runtimes is
the **`Co-Authored-By` commit trailer**, which names the assistant that contributed:

- Claude Code sessions:
  `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`
- OpenAI Codex sessions:
  `Co-Authored-By: Codex Opus 4.7 (1M context) <noreply@anthropic.com>`

If multiple agents contributed materially to the same commit, include one trailer per
agent. The full trailer convention lives in
[docs/standards/14-git-workflow.md § Trailers](docs/standards/14-git-workflow.md).

## Skills

Both Claude Code and Codex respect skills declared as `SKILL.md` files. LearnStack's
skill catalogue lives in [`.claude/skills/`](.claude/skills/) and applies to every
agent runtime — there is no Codex-specific skills directory. Begin every task with
[start-task](.claude/skills/start-task/SKILL.md) and pick the workflow-specific skill
from the [catalogue](.claude/skills/README.md).

## Maintaining this file

Do **not** copy CLAUDE.md content into AGENTS.md. If a guidance rule needs to change,
change CLAUDE.md; the rule applies everywhere by virtue of the redirect above. The
single-source-of-truth principle ([Principles § 14](docs/standards/00-principles.md))
applies here too.
