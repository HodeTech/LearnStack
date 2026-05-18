---
name: update-glossary
description: >
  Add or refine a term in the canonical LearnStack glossary at `docs/glossary.md`.
  USE FOR: introducing a project-specific term (`TenantPageBlock`, `IModule`,
  `Cohort`, `Hub Operator`, etc.) that other docs cite, fixing a stale glossary
  entry, removing a deprecated term. DO NOT USE FOR: general programming terms
  (link to their canonical source), terms only used in `docs/analysis/`, or
  duplicating a definition that already exists.
---

# Updating the LearnStack glossary

## Purpose

`docs/glossary.md` is the **single source of truth** for project-specific terms. A
new term lands there first; every other doc links to it instead of redefining it.

## When to use

- A new term appears in an ADR / standard / architecture doc and isn't yet in the
  glossary.
- An existing entry no longer matches the implementation.
- A term has been renamed (e.g. `AuditLog` → `AuditEntry`).
- A term has been deprecated by a superseding ADR (mark, don't delete immediately).

## When not to use

- General-purpose programming terms (CRUD, JWT, RLS). Link to a canonical external
  source if needed.
- Terms used only in `docs/analysis/` (gitignored). Glossary is for the committed
  corpus.
- Aliases / synonyms that don't carry distinct meaning. Use the term's primary
  entry's "Synonym" line instead.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Term | Yes | The exact PascalCase / kebab-case form as it appears in code or docs. |
| Definition | Yes | One paragraph max. Link the canonical ADR / standard / architecture doc. |
| Section | Yes | The thematic section under which the term fits (Tenancy, Identity, Audit, …). |

## Workflow

### Step 1: Search first

```bash
grep -n -i "<term>" docs/glossary.md
grep -rn "<term>" docs/ --include='*.md' | head -10
```

If the term exists, edit the existing entry. If it lives under a redirect alias,
update the primary entry and leave the alias pointing at it. Don't create a second
entry for the same concept.

### Step 2: Pick the section

Open `docs/glossary.md`. Sections (in order):

- Platform & Tenancy
- Identity
- Customization (note: this section uses the phrase "Tenant Customization
  Aggregate")
- Content
- Media
- Education / Catalog
- Learning Content
- Assessment
- Enrollment & Access
- Scheduling / Live Classroom
- Notifications
- Billing
- Analytics
- Tenant Customization
- Audit
- Permissions
- Page Builder
- Custom Domains
- Data Protection
- Branding
- Module-Loading Contracts
- Marker Attributes

Pick the section that fits. If the term spans two, file under the **owning** module's
section and reference from the other.

### Step 3: Write the entry

Each entry is a row in the section's Markdown table:

```markdown
| **<Term>** | <One-paragraph definition>. <Optional: link to the canonical doc / ADR / standard>. |
```

Rules:

- One paragraph maximum. If you need two, the topic deserves its own architecture
  doc, not a long glossary entry.
- Lead with what it **is**, not what it's *for*.
- Cite the authoritative source inline (e.g.
  "[ADR-0017](decisions/0017-tenant-organization-hierarchy.md)").
- Avoid restating things the canonical doc already says — link.

### Step 4: Update downstream references

If you renamed or deprecated a term:

```bash
grep -rn "<old-term>" docs/ --include='*.md' | grep -v docs/analysis/
```

Walk the list; either update the references or, for a deprecated term, leave a
glossary entry marked **(deprecated)** with a forward pointer to the replacement
and a renaming date.

### Step 5: Sanity-check

```bash
# Are all glossary terms used elsewhere?
for term in $(grep -oE '^\| \*\*[^*]+\*\*' docs/glossary.md | sed 's/^| \*\*//;s/\*\*$//'); do
    refs=$(grep -rln "$term" docs/ --include='*.md' | grep -v docs/glossary.md | grep -v docs/analysis/ | wc -l)
    echo "$refs  $term"
done | sort -n | head -20
```

A term with zero references outside the glossary is either dead weight or used
inconsistently. Investigate both possibilities.

## Validation

- The term appears exactly once in `docs/glossary.md` (or once + one alias row).
- At least one other doc cites the glossary entry (or, if the term is fresh, the
  ADR / architecture doc introducing it does).
- The definition contains a link to the canonical doc (ADR / standard / architecture)
  rather than repeating that doc's content.
- A rename leaves a deprecated entry for one release before removal (per
  [13-documentation.md](../../../docs/standards/13-documentation.md)).

## Common pitfalls

- **Defining the same term twice.** Use the search step. Aliases live as a sub-bullet
  under the primary entry, not as a second row.
- **Restating an architecture doc.** Glossary entries are dictionary-length, not
  essay-length.
- **Skipping the section.** A term filed in the wrong section is hard to find.
- **Removing a deprecated term silently.** Mark **(deprecated)** for one release;
  remove when downstream references are gone.
- **Adding a term to glossary but not using it.** Glossary entries should be cited.
  An entry with zero callers is usually a sign the term shouldn't exist.
