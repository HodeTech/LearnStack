#!/usr/bin/env python3
"""Read the one lifecycle declaration inside an ADR's Status section."""
import re
import sys


def lifecycle(source: str) -> str:
    statuses = []
    sections = 0
    inside = False
    fence = False
    for line in source.splitlines():
        if re.match(r'^\s*(```|~~~)', line):
            fence = not fence
            continue
        if fence:
            continue
        if re.match(r'^##\s+', line):
            inside = line.strip() == '## Status'
            sections += inside
            continue
        if not inside or line.lstrip().startswith('>'):
            continue
        declaration = re.sub(r'\*\*|__', '', line.strip())
        match = re.match(r'^(Proposed|Accepted|Superseded|Deprecated)(?=$|\s*(?:[—(]|$)|\s+(?:with|by)\b)', declaration)
        if match:
            statuses.append(match[1])
    if sections != 1 or len(statuses) != 1:
        raise ValueError('ADR needs exactly one lifecycle declaration in one Status section')
    return statuses[0]


if __name__ == '__main__':
    try:
        print(lifecycle(sys.stdin.read()))
    except ValueError as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
