#!/usr/bin/env python3
"""Fail on a relative link whose #fragment names no heading or anchor in its target.

ADR-0069: the Markdown link audit checks that a target file exists and strips the
fragment first, so a moved or renamed heading broke every link into it silently.
This audit reads the whole tree, because a heading edited in one file breaks links
in files the change never touched.
"""
import re
import subprocess
import sys
from pathlib import Path

SOURCES = {'.md', '.cs', '.ts', '.tsx', '.mts', '.cts', '.js', '.jsx', '.mjs', '.cjs'}
FENCE = re.compile(r'^\s*(```|~~~)')
HEADING = re.compile(r'^ {0,3}(#{1,6})\s+(.*?)(?:\s+#+)?\s*$')
CODE_SPAN = re.compile(r'(`+)(?:(?!\1).)+?\1')
LINK = re.compile(r'\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)|\bhref="([^"]+)"')
EXPLICIT = re.compile(r'<a\s+[^>]*\b(?:id|name)="([^"]+)"')
SCHEME = re.compile(r'^[A-Za-z][A-Za-z0-9+.-]*:')


def slug(text):
    """GitHub's heading anchor: link text kept, code ticks and punctuation dropped."""
    text = re.sub(r'\[([^\]]*)\]\([^)]*\)', r'\1', text)
    text = text.replace('`', '').strip().lower()
    return re.sub(r'[^\w\- ]', '', text).replace(' ', '-')


def anchors(text):
    """Every fragment a GitHub rendering of this Markdown answers, duplicates numbered."""
    seen, found, fence = {}, set(), False
    for line in text.splitlines():
        if FENCE.match(line):
            fence = not fence
            continue
        if fence:
            continue
        heading = HEADING.match(line)
        if heading:
            base = slug(heading.group(2))
            count = seen.get(base, 0)
            found.add(base if count == 0 else f'{base}-{count}')
            seen[base] = count + 1
        found.update(EXPLICIT.findall(line))
    return found


def links(path, text):
    """(line number, target) for each link carrying a fragment; fences and code spans are samples."""
    markdown = path.suffix == '.md'
    fence = False
    for number, line in enumerate(text.splitlines(), 1):
        if markdown and FENCE.match(line):
            fence = not fence
            continue
        if fence:
            continue
        if markdown:
            line = CODE_SPAN.sub('', line)
        for match in LINK.finditer(line):
            target = match.group(1) or match.group(2)
            if '#' in target and not SCHEME.match(target):
                yield number, target


def resolve(root, source, path):
    """The Markdown file a link names, source-relative first, then repository-relative."""
    if not path:
        return source
    for candidate in (source.parent / path, root / path):
        candidate = candidate.resolve()
        if candidate.is_relative_to(root) and candidate.is_file():
            return candidate
    return None


def audit(root, files):
    root = root.resolve()
    cache, problems, checked = {}, [], 0
    for relative in files:
        source = root / relative
        if source.suffix not in SOURCES or not source.is_file():
            continue
        text = source.read_text(encoding='utf-8', errors='replace')
        for number, target in links(source, text):
            path, fragment = target.split('#', 1)
            destination = resolve(root, source, path.split('?', 1)[0])
            # A missing file is the link audit's finding; a code file's line anchor is GitHub's.
            if destination is None or destination.suffix != '.md':
                continue
            if destination not in cache:
                cache[destination] = anchors(destination.read_text(encoding='utf-8', errors='replace'))
            checked += 1
            if fragment not in cache[destination]:
                problems.append(f'{relative}:{number}: #{fragment} is not an anchor of '
                                f'{destination.relative_to(root)}')
    return problems, checked


def tracked(root):
    """Tracked and new unignored files; nested repositories such as worktrees are skipped."""
    listing = subprocess.run(['git', 'ls-files', '--cached', '--others', '--exclude-standard'],
                             cwd=root, capture_output=True, text=True, check=True).stdout
    return sorted({line for line in listing.splitlines() if line and not line.endswith('/')})


def main():
    root = Path(__file__).resolve().parents[1]
    problems, checked = audit(root, tracked(root))
    for problem in problems:
        print(f'BROKEN FRAGMENT: {problem}')
    if problems:
        print(f'::error::{len(problems)} link fragment(s) name no heading or anchor (ADR-0069).')
        return 1
    print(f'Markdown fragment audit: {checked} fragment links resolve.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
