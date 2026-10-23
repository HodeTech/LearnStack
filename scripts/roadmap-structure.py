#!/usr/bin/env python3
"""Hold phase documents, delivery logs and the status carriers to ADR-0069.

The phase document is the plan, the delivery log is the record, and packet status
lives once. Every budget except the whole-file one counts non-blank lines.
"""
import re
import sys
from pathlib import Path

ROADMAP = Path('docs/roadmap')
PLAN = ('Goal', 'Scope', 'Deliverables', 'Completion Criteria', 'Risks', 'Phase Exit Decision')
POINTER = ('Goal', 'Scope on the LearnStack side', 'Trigger', 'Phase Exit Decision')
# Roadmap § Phase Structure's declared shapes; Phase 01 also keeps its long annotation block.
SHAPES = {
    'phase-01-repository-tooling.md': ('Goal', 'Scope', 'Deliverables', 'Completion Criteria',
                                       'Technical Notes', 'Risks'),
    'phase-09b-hub-billing.md': POINTER,
    'phase-12-hub-marketplace.md': POINTER,
}
UNBOUNDED_STATUS = {'phase-01-repository-tooling.md'}
# Phase 02a closed on 2026-09-12 with its records inside the file; ADR-0069 keeps it frozen.
FROZEN = {'phase-02a-kernel-tenancy.md'}
# Entries whose headings predate the dating rule, moved verbatim on 2026-10-22.
UNDATED_ENTRIES = {
    'phase-02d-delivery-log.md': {
        'Delivery Record (P02d-1)', 'Pending Course Marketplace proposal',
        'Delivery record: P02d-3', 'P02d-4 Step 3: public Education reads',
        'P02d-4 Step 4: contract, SDK and CI', 'P02d-5 delivery record',
        'P02d-6 Step 1: Localization and document foundation',
        'P02d-6 Step 2: Catalog and course pages',
        'P02d-6 Step 3: Ordered lesson presentation',
        'P02d-6 Step 4: Product proof and accessibility closeout',
    },
}
LOG_SECTIONS = ('Phase entry', 'Cross-packet records', 'Status archive')
STATUS_BUDGET, FILE_BUDGET, INDEX_ENTRY_BUDGET = 40, 2000, 6
CARRIERS = {'CLAUDE.md': ('What state this is in', 'Where to start'),
            'README.md': ('Where it is today',)}

NAME = re.compile(r'^phase-(\d{2}[a-z]?)-([a-z0-9-]+)\.md$')
FENCE = re.compile(r'^\s*(```|~~~)')
HEADING = re.compile(r'^(#{1,6})\s+(.*?)\s*$')
DATE = re.compile(r'\b\d{4}-\d{2}-\d{2}\b')
RECORD = re.compile(r'\b\d{4}-\d{2}-\d{2}\b|\bP\d{2}[a-z]?-\d+'
                    r'|\b(?:delivery record|review round|closeout|decision (?:package|pass))',
                    re.IGNORECASE)
LOG_LINK = re.compile(r'(?:\]\(|href=")[^)"\s]*phase-\d{2}[a-z]?-delivery-log\.md')


def headings(lines):
    """(index, level, text) outside fenced blocks."""
    fence = False
    for index, line in enumerate(lines):
        if FENCE.match(line):
            fence = not fence
            continue
        match = None if fence else HEADING.match(line)
        if match:
            yield index, len(match.group(1)), match.group(2)


def nonblank(lines):
    return sum(1 for line in lines if line.strip())


def section(lines, title, level=2):
    """The lines of one heading's section, or None when the heading is missing."""
    found = [index for index, depth, text in headings(lines) if depth == level and text == title]
    if len(found) != 1:
        return None
    start = found[0]
    end = next((index for index, depth, _ in headings(lines) if index > start and depth <= level),
               len(lines))
    return lines[start:end]


def classify(name):
    match = NAME.match(name)
    if not match:
        return None, None
    phase, topic = match.groups()
    if topic == 'delivery-log':
        return 'log', phase
    if topic == 'exit-evidence':
        return 'evidence', phase
    return 'phase', phase


def check_phase(name, lines, has_log):
    problems = []
    found = [(index, text) for index, depth, text in headings(lines) if depth == 2]
    titles = [text for _, text in found]
    required = SHAPES.get(name, PLAN)
    position = 0
    for title in titles:
        if position < len(required) and title == required[position]:
            position += 1
    if position < len(required):
        problems.append(f'{name}: missing or out of order: ## {required[position]}')
    elif titles and titles[-1] != required[-1]:
        last = max(index for index, text in found if text == required[-1])
        after = [text for index, text in found if index > last]
        problems.append(f'{name}: nothing may follow ## {required[-1]}; found ## {after[0]}')
    for index, _, text in headings(lines):
        if index > 0 and RECORD.search(text):
            problems.append(f'{name}:{index + 1}: record heading in a phase document '
                            f'(move it to the delivery log): {text}')
    first = found[0][0] if found else len(lines)
    status = lines[1:first]
    if name not in UNBOUNDED_STATUS and nonblank(status) > STATUS_BUDGET:
        problems.append(f'{name}: Status block has {nonblank(status)} non-blank lines; '
                        f'the budget is {STATUS_BUDGET}')
    if len(lines) > FILE_BUDGET:
        problems.append(f'{name}: {len(lines)} lines; the budget is {FILE_BUDGET} — '
                        'split the phase rather than raise it')
    log = name.split('-', 2)[0] + '-' + name.split('-', 2)[1] + '-delivery-log.md'
    if has_log and not any(f']({log})' in line or f']({log}#' in line for line in status):
        problems.append(f'{name}: its Status block does not link {log}')
    return problems


def check_log(name, phase, lines, phase_docs):
    problems = []
    if len(phase_docs) != 1:
        problems.append(f'{name}: needs exactly one phase document phase-{phase}-<topic>.md, '
                        f'found {len(phase_docs)}')
    elif not any(f']({phase_docs[0]})' in line for line in lines[:next(
            (index for index, depth, _ in headings(lines) if depth == 2), len(lines))]):
        problems.append(f'{name}: its introduction does not link {phase_docs[0]}')
    packet = re.compile(rf'^P{re.escape(phase)}-(\d+)[a-z]? — \S')
    numbers, current, seen_packet = [], None, False
    sections = [(index, text) for index, depth, text in headings(lines) if depth == 2]
    for position, (index, text) in enumerate(sections):
        match = packet.match(text)
        if match:
            numbers.append(int(match.group(1)))
            seen_packet = True
        elif text not in LOG_SECTIONS:
            problems.append(f'{name}:{index + 1}: ## {text} is neither a packet section '
                            f'(P{phase}-<n> — <title>) nor one of {", ".join(LOG_SECTIONS)}')
        elif text == 'Phase entry' and seen_packet:
            problems.append(f'{name}:{index + 1}: ## Phase entry precedes every packet section')
        elif text == 'Status archive' and position != len(sections) - 1:
            problems.append(f'{name}:{index + 1}: ## Status archive is the last section')
    if numbers != sorted(set(numbers)):
        problems.append(f'{name}: packet sections are not in packet order: {numbers}')
    legacy = UNDATED_ENTRIES.get(name, set())
    for index, depth, text in headings(lines):
        if depth == 2:
            current = text
        elif depth == 3 and current != 'Status archive' and not DATE.search(text) and text not in legacy:
            problems.append(f'{name}:{index + 1}: entry heading carries no date: {text}')
    return problems


def check_index(lines):
    problems = []
    phases = section(lines, 'Phases')
    if phases is None:
        return ['docs/roadmap/README.md: ## Phases is missing']
    entry = None
    # An entry is a top-level item and its indented continuation, blank lines included.
    for line in phases[1:] + ['<end>']:
        if line.startswith('- ') or (line.strip() and not line.startswith(' ')):
            if entry and nonblank(entry) > INDEX_ENTRY_BUDGET:
                problems.append(f'docs/roadmap/README.md: § Phases entry has {nonblank(entry)} '
                                f'non-blank lines; the budget is {INDEX_ENTRY_BUDGET}: {entry[0][:70]}')
            entry = [line] if line.startswith('- ') else None
        elif entry is not None:
            entry.append(line)
        if LOG_LINK.search(line):
            problems.append('docs/roadmap/README.md: § Phases links a delivery log; '
                            'link the phase document instead')
    return problems


def check_carrier(name, lines):
    problems = []
    for number, line in enumerate(lines, 1):
        if LOG_LINK.search(line):
            problems.append(f'{name}:{number}: links a delivery log; link the phase document instead')
    for title in CARRIERS[name]:
        body = section(lines, title)
        if body is None:
            problems.append(f'{name}: ## {title} is missing')
        elif nonblank(body) > STATUS_BUDGET:
            problems.append(f'{name}: § {title} has {nonblank(body)} non-blank lines; '
                            f'the budget is {STATUS_BUDGET}')
    return problems


def check(root):
    root = Path(root)
    roadmap = root / ROADMAP
    names = sorted(path.name for path in roadmap.glob('phase-*.md'))
    classes = {name: classify(name) for name in names}
    problems = [f'{ROADMAP / name}: not phase-<id>-<topic>.md' for name, (kind, _) in classes.items()
                if kind is None]
    docs = {}
    for name, (kind, phase) in classes.items():
        if kind == 'phase':
            docs.setdefault(phase, []).append(name)
    logs = {phase for kind, phase in classes.values() if kind == 'log'}
    for name, (kind, phase) in classes.items():
        lines = (roadmap / name).read_text(encoding='utf-8').splitlines()
        if kind == 'phase' and name not in FROZEN:
            problems += check_phase(name, lines, phase in logs)
        elif kind == 'log':
            problems += check_log(name, phase, lines, docs.get(phase, []))
        elif kind == 'evidence' and not docs.get(phase):
            problems.append(f'{name}: no phase document for its phase')
    problems += check_index((roadmap / 'README.md').read_text(encoding='utf-8').splitlines())
    for name in CARRIERS:
        problems += check_carrier(name, (root / name).read_text(encoding='utf-8').splitlines())
    return problems


def main():
    root = Path(__file__).resolve().parents[1]
    problems = check(root)
    for problem in problems:
        print(f'ROADMAP STRUCTURE: {problem}')
    if problems:
        print(f'::error::{len(problems)} roadmap structure violation(s); see ADR-0069.')
        return 1
    print('Roadmap structure: phase documents, delivery logs, the roadmap index, '
          'CLAUDE.md and README.md conform to ADR-0069.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
