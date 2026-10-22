# ADR-0069: Phase Documents and Delivery Logs

## Status

Accepted — 2026-10-22.

**Date:** 2026-10-22
**Deciders:** @cemil

The maintainer accepted this record after one review round whose findings the
accepted text resolves. Acceptance authorizes the migration and guards in
§ Implementation Notes; it claims neither as delivered.

## Decision Drivers

- **A phase document stopped being a plan.** Measured at `dada277b` on 2026-10-22,
  [Phase 02b](../roadmap/phase-02b-events-auth.md) is 18,884 lines (1.33 MB, about
  171,000 words). 17,420 of those lines (92%) are packet records: decision
  packages, step records, review rounds, corrections, CI receipts and merge
  closeouts. The plan sections, its 108-line Status block included, are about 1,460
  lines, and the real `## Scope` content starts 9,800 lines below its heading. The
  document had 1,383 lines on 2026-10-11; 408 commits touched it in the eleven days
  since.
- **The pattern is structural, not local.** [Phase 02d](../roadmap/phase-02d-walking-skeleton.md)
  is 8,319 lines. Its records sit inside `## Scope`, dated and undated alike —
  `### Delivery record: P02d-3`, `### P02d-6 Step 1: …` — and after
  `## Phase Exit Decision`. [Roadmap § Phase Structure](../roadmap/README.md#phase-structure)
  names six sections and says nothing about where a packet's records go, while
  [Documentation Standards § Doc Types](../standards/13-documentation.md#doc-types)
  makes a shipped packet's record immutable. An append-only record kept inside the
  document that must also stay a readable plan grows without bound.
- **The document cannot be read whole by the agents told to read it.** `CLAUDE.md`
  directs every task to the live phase. At roughly 300,000 tokens, Phase 02b fits
  in no single read, so the plan is consulted in fragments or not at all.
- **Status is restated, not linked.** One packet state is narrated in the phase
  Status block, the packet table, the Completion Criteria preamble, the roadmap
  index and `CLAUDE.md` — in its § What state this is in, its § Where to start and
  even one § Things to never do bullet. "Complete and merged" appears 19 times in
  Phase 02b alone, and `CLAUDE.md`, loaded into every session, is 856 lines, 471 of
  them status narrative. Every step edits all of them, which is how they keep
  growing.
- **Nothing checks a fragment.** CI's Markdown link audit strips `#fragment` before
  its file-existence test, skips pure in-file `#fragment` links, and reads only
  changed files. 63 tracked files carry 336 links to 81 distinct anchors in Phase
  02b, and 65 carry 301 links to 72 anchors in Phase 02d — 637 fragment links in
  102 files, the two phase documents' links to each other included. Phase 02b
  holds 94 in-file anchor links and Phase 02d 133. Moving a section breaks every
  anchor into it silently.
- **Written conventions did not hold.** Both documents grew under the existing
  rules; a rule with no failing check is the failure mode this corpus already
  learned to distrust.

## Considered Options

1. **A plan document plus one append-only delivery log per phase, with guards**
   (chosen).
2. **One record file per packet** (rejected). Related records then live in
   different files: a decision package and the correction that amends it a packet
   later, or a post-merge CI fix and the step it repairs, become cross-file links,
   and every new file is another link prefix to keep coherent. One log per phase
   keeps one file and one link prefix, and needs only three declared non-packet
   sections — a phase entry, cross-packet records and the status archive — for
   what no single packet owns.
3. **Keep records in the phase document, after `## Phase Exit Decision`**, as
   Phase 02a does (rejected). The plan becomes readable, but the file still grows
   without bound and every reader of the plan still loads every record. Phase 02a
   reached 3,606 lines with this shape.
4. **Keep records only in pull requests and CI artifacts** (rejected). ADRs,
   standards and catalogue entries cite dated records by anchor; pull request text
   is not versioned with the repository, and Actions artifacts expire.
5. **Written convention without automated checks** (rejected, see the last driver).

## Decision

LearnStack keeps each phase's roadmap material in sibling documents under
`docs/roadmap/`, holds status in one place, and enforces both with CI guards.

**File classes.** A file is classified by its name, suffix first:

| Class | Name | Holds |
|---|---|---|
| Delivery log | `phase-<id>-delivery-log.md` | The phase's packet records |
| Exit evidence index | `phase-<id>-exit-evidence.md` | Optional, written at phase exit: each exit criterion mapped to its proof, as [Phase 02d's](../roadmap/phase-02d-exit-evidence.md) does |
| Phase document | every other `phase-<id>-<topic>.md` | The plan |

**The phase document is the plan.** It carries the six sections
[Roadmap § Phase Structure](../roadmap/README.md#phase-structure) names, with that
section's declared exceptions; further plan sections may sit between `## Goal` and
`## Phase Exit Decision`, and nothing follows `## Phase Exit Decision`. A phase
with packets carries a Status block of at most 40 lines above `## Goal`, in
progress and after completion: the phase's state and one packet table whose rows
link each packet's records in the log. The phase document holds **no records**: no
heading carries a date, a packet identifier (`P<phase>-<n>`), or the words
*delivery record*, *review round*, *closeout*, *decision package* or *decision
pass*. Its whole file stays within 2,000 physical lines.

**The delivery log is the record.** Each `##` section is either a packet —
`P<phase>-<n> — <title>`, in packet order — or one of three non-packet sections:
`Phase entry` for records before the first decision pass, `Cross-packet records`
for what no single packet owns, and `Status archive` for retired Status prose.
Each entry is a `###` heading in its section with its date in the heading. The log
is append-only: a shipped packet's entries are not rewritten, and a false citation
gets the dated inline correction Documentation Standards already prescribes.

**Decision packages are normative.** A decision package in the log is the vehicle
a gate marked *phase-doc* closes in. Once Accepted, its answers bind exactly as the
phase document's own text would, the decision register's status cell links the
exact answer, and implementers read it before writing the code it governs.

**Status lives once.** Packet state lives in the phase document's Status block,
gate state in its decision register. The [roadmap index](../roadmap/README.md)
gives each phase at most six lines in § Phases and links no delivery log there.
`CLAUDE.md` gives § What state this is in and § Where to start at most 40 lines
each and links no delivery log anywhere; it points to the phase document.

**Two guards run in CI's `meta` job and in a new `make lint-docs` target.**

- `scripts/markdown-fragment-audit.py` resolves every relative link that carries a
  `#fragment` — `[text](path#fragment)`, pure in-file `[text](#fragment)`, and
  `href="path#fragment"` — in every tracked Markdown file and in the doc comments of
  tracked C#, TypeScript and JavaScript sources. A path resolves against the source
  file's directory, then against the repository root, as the existing link audit
  does. The fragment must match a GitHub heading slug of the target, with GitHub's
  `-1`, `-2` duplicate numbering, or an explicit `id` / `name` anchor. It reads the
  whole tree, because a heading edited in one file breaks links in files the change
  never touched.
- `scripts/roadmap-structure.py` holds the three file classes, the roadmap index
  and `CLAUDE.md` to the rules above.

Each ships with planted-violation controls that prove it can fail.

**Legacy shapes are declared, not silently exempted.** Phase 02a, complete on
2026-09-12, is exempt from every phase-document check as one frozen file: its
`## Delivery Record` sections after its exit decision, the dated headings inside
them and its 3,606 lines stay as they are. Phase 01 keeps the status annotation
block Roadmap § Phase Structure already declares. Ten Phase 02d entries whose
headings predate the dating rule are listed by anchor in the guard; any other
undated entry fails it.

## Context

**What the migration moves and what it touches.** The migration this decision
authorizes moves Phase 02b's and Phase 02d's records to their logs in their
original order within each packet, under one new `##` section per packet. Record
text does not change. Two things around it do: heading levels shift where a record
moves under its packet section, and a link whose target now sits in the other file
is retargeted — 138 of the 227 in-file links cross the split. The move is
therefore *verbatim in text*, not byte-identical.

**Anchors survive only if order does.** GitHub derives an anchor from heading text
and numbers repeated slugs by their order in the file: Phase 02b repeats 13 heading
texts, `step-1-review-round-1` four times, and `runtime-bounds-and-observation`
twice (P02b-2 and P02b-4), with
[Observability Standards](../standards/10-observability.md) linking the first.
Changing the order of two same-named headings swaps their anchors without breaking
a single link. The migration therefore keeps every packet's records in source order,
places the packets in source order, and verifies each heading individually: the
anchor it has in its new file must equal the anchor it had before. A deliberately
reordered run fails that check.

**Retargeting is not a correction.** Links into a moved record keep their text and
change their target. That is explicitly not a correction under
[ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md), so the Accepted
ADRs whose links move owe no amendment, and CI's Accepted ADR disclosure step
already ignores link-target-only diffs. Phase 04's only dated plan heading,
`Content-type quota accounting — accepted (2026-10-19)`, keeps its date in its
existing acceptance line and drops it from the heading; its six inbound links are
retargeted with the rest.

**Why a line budget and not only a shape check.** The heading rule catches the
forms the growth took: dated and packet-named record headings inside `## Scope`,
and sections appended after `## Phase Exit Decision`. A record pasted under a plan
heading would pass it. The 2,000-line budget is the backstop: the migrated plans,
new Status blocks included, are about 1,385 lines for Phase 02b and 1,680 for
Phase 02d, and no phase document without records exceeds 820.

**Why `CLAUDE.md` is in scope.** It is the one document every session loads, and
the same growth costs most there. Bounding its two status-bearing sections and
forbidding delivery-log links anywhere in it removes the incentive to narrate,
because the narration has nowhere to point.

**What would change this decision.** A phase whose plan legitimately outgrows 2,000
lines is split, as 02a/02b/02c/02d already are; the budget is not raised. If GitHub
changes its slug rules, the fragment audit's slug function follows them.

## Consequences

### Positive

- The phase document is readable in one pass again.
- A packet step updates one log entry and one Status row, not five narratives.
- A broken anchor anywhere in the corpus fails CI instead of failing a reader.

### Negative

- Two files per phase with packets instead of one, and a reader following a record
  link lands in a second file.
- The migration retargets about 600 links in about 100 files once.
- Implementers read the Accepted decision packages in the log, not only the plan;
  the decision register is their index.
- Contributors writing a record must know which file it belongs in; the guard tells
  them when they are wrong, not before.

### Neutral

- The log keeps growing, and its decision packages stay binding. A reader who needs
  an answer follows one register link; only a reader who wants the delivery history
  reads the rest.
- Phase 02a keeps its shape until someone has a reason to touch it.

## Implementation Notes

Acceptance authorizes this work, in this order:

1. **Migration.** Split Phase 02b and Phase 02d into plan and
   `phase-02b-delivery-log.md` / `phase-02d-delivery-log.md` with the per-heading
   anchor verification above; move each superseded Status block verbatim into its
   log's `Status archive`; write each phase document's bounded Status block;
   retarget every inbound link; drop the date from Phase 04's plan heading and
   retarget its six links.
2. **Status in one place.** Strip packet narrative from the phase documents' packet
   tables and Completion Criteria preambles, reduce the roadmap index's § Phases
   entries and `CLAUDE.md` § What state this is in and § Where to start to the
   budgets above, and remove the status sentence from `CLAUDE.md` § Things to never
   do.
3. **Carriers.** [Roadmap § Phase Structure](../roadmap/README.md#phase-structure)
   and § Decision Timing — whose *Acceptance is explicit* bullet names where closed
   parts are listed and where a *phase-doc* answer lives;
   [Documentation Standards](../standards/13-documentation.md) § Layout, § Doc Types
   and § When to Update Documentation; the [glossary](../glossary.md)'s *Packet*
   entry and new *Delivery Log*, *Delivery Record* and *Decision Package* entries;
   `CLAUDE.md` § Documentation layout and § Things to never do; the `implement-task`
   and `standards-check` skills; and this record's row in the
   [decisions index](README.md).
4. **Guards.** `scripts/markdown-fragment-audit.py` and
   `scripts/roadmap-structure.py` with their controls
   (`scripts/test-markdown-fragment-audit.py`, `scripts/test-roadmap-structure.py`),
   two `meta` job steps, and the new `make lint-docs` target. They are CI guards
   over the documentation corpus, like the Markdown link audit and the Accepted ADR
   disclosure step, not entries in the architecture-test catalogue.

## References

- [ADR-0007](0007-documentation-language-and-conventions.md) — documentation
  language and conventions, the authority of Documentation Standards.
- [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md) — link
  retargeting is not a correction.
- [Roadmap § Phase Structure](../roadmap/README.md#phase-structure) and
  [§ Decision Timing](../roadmap/README.md#decision-timing).
- [Documentation Standards](../standards/13-documentation.md).
- [Glossary § Phase, Packet](../glossary.md).
