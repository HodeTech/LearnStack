#!/usr/bin/env python3
"""Generate, or check, the index that opens each delivery log (ADR-0069, 2026-10-23).

A row is a packet section, an entry, or a dated heading deeper than an entry, with
its anchor and its length in lines. Lengths, not line numbers: the index sits above
every heading, so one new row would renumber them all. Rows carry no inferred kind;
the heading text already says what a record is.

    python3 scripts/delivery-log-index.py          # fail when an index is stale
    python3 scripts/delivery-log-index.py --write  # regenerate every index
"""
import importlib.util
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
_SPEC = importlib.util.spec_from_file_location('markdown_fragment_audit', HERE / 'markdown-fragment-audit.py')
_AUDIT = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(_AUDIT)

BEGIN = ('<!-- BEGIN GENERATED INDEX: scripts/delivery-log-index.py — '
         'run `make docs-index`; do not edit by hand -->')
END = '<!-- END GENERATED INDEX -->'
DATE = re.compile(r'\b\d{4}-\d{2}-\d{2}\b')
LINK_TEXT = re.compile(r'\[([^\]]*)\]\([^)]*\)')


def headings(lines):
    """(index, level, text, anchor) in file order, with GitHub's duplicate numbering."""
    seen, found, fence = {}, [], False
    for index, line in enumerate(lines):
        if _AUDIT.FENCE.match(line):
            fence = not fence
            continue
        match = None if fence else _AUDIT.HEADING.match(line)
        if match:
            base = _AUDIT.slug(match.group(2))
            count = seen.get(base, 0)
            seen[base] = count + 1
            found.append((index, len(match.group(1)), match.group(2), base if count == 0 else f'{base}-{count}'))
    return found


def rows(lines):
    """The index list: ## sections, every ###, and each deeper heading that carries a date."""
    found = headings(lines)
    result, stack = [], []
    for position, (index, level, text, anchor) in enumerate(found):
        if level < 2 or (level > 3 and not DATE.search(text)):
            continue
        end = next((other for other, depth, _, _ in found[position + 1:] if depth <= level), len(lines))
        while stack and stack[-1] >= level:
            stack.pop()
        title = LINK_TEXT.sub(r'\1', text)
        result.append(f'{"  " * len(stack)}- [{title}](#{anchor}) — {end - index:,} lines')
        stack.append(level)
    return result


def block(lines):
    return [BEGIN, '', '**Index**', ''] + rows(lines) + ['', END]


def locate(lines):
    """(start, end) of the generated block, or the reason it cannot be used."""
    starts = [index for index, line in enumerate(lines) if line == BEGIN]
    ends = [index for index, line in enumerate(lines) if line == END]
    if len(starts) != 1 or len(ends) != 1 or ends[0] < starts[0]:
        return None, 'needs exactly one generated index block'
    first = next((index for index, level, _, _ in headings(lines) if level == 2), len(lines))
    if ends[0] >= first:
        return None, 'its generated index must close before the first ## section'
    return (starts[0], ends[0]), None


def regenerate(text):
    """The log with its index block rebuilt; raises when the block is missing or misplaced."""
    lines = text.split('\n')
    span, reason = locate(lines)
    if span is None:
        raise ValueError(reason)
    start, end = span
    return '\n'.join(lines[:start] + block(lines) + lines[end + 1:])


def logs(root):
    return sorted((Path(root) / 'docs/roadmap').glob('phase-*-delivery-log.md'))


def main(argv):
    root = HERE.parent
    write = argv[1:] == ['--write']
    if argv[1:] not in ([], ['--write']):
        print('usage: delivery-log-index.py [--write]', file=sys.stderr)
        return 2
    problems = []
    for path in logs(root):
        text = path.read_text(encoding='utf-8')
        name = path.relative_to(root)
        try:
            fresh = regenerate(text)
        except ValueError as error:
            problems.append(f'{name}: {error}')
            continue
        if fresh == text:
            continue
        if write:
            path.write_text(fresh, encoding='utf-8')
            print(f'regenerated {name}')
        else:
            problems.append(f'{name}: its index is stale; run `make docs-index`')
    for problem in problems:
        print(f'DELIVERY LOG INDEX: {problem}')
    if problems:
        print(f'::error::{len(problems)} delivery log index problem(s); see ADR-0069.')
        return 1
    if not write:
        print(f'Delivery log indexes: {len(logs(root))} current.')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
