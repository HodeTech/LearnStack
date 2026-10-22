#!/usr/bin/env python3
"""Prove the delivery log index is generated exactly and that a stale one fails."""
import importlib.util
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = Path(__file__).with_name('delivery-log-index.py')
SPEC = importlib.util.spec_from_file_location('delivery_log_index', SCRIPT)
index = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(index)

LOG = '\n'.join([
    '# Phase 07 — delivery log', '', 'Intro.', '', index.BEGIN, index.END, '',
    '## P07-1 — Producer', '',
    '### P07-1 decision package (2026-10-21)', 'body', '',
    '#### Accepted answers', 'body', '',
    '##### Maintainer acceptance (2026-10-21)', 'body', '',
    '###### Fix review (2026-10-22)', 'body', '',
    '### Step 1 review round 1', 'undated legacy entry', '',
    '```', '### Not a heading (2026-10-22)', '```', '',
    '## P07-2 — Consumer', '',
    '### Step 1 review round 1', 'duplicate text', '',
    '## Status archive', '', 'Retired prose.', ''])


class RowTests(unittest.TestCase):
    def test_rows_nest_by_included_ancestors_and_count_whole_sections(self):
        self.assertEqual([
            '- [P07-1 — Producer](#p07-1--producer) — 21 lines',
            '  - [P07-1 decision package (2026-10-21)](#p07-1-decision-package-2026-10-21) — 12 lines',
            '    - [Maintainer acceptance (2026-10-21)](#maintainer-acceptance-2026-10-21) — 6 lines',
            '      - [Fix review (2026-10-22)](#fix-review-2026-10-22) — 3 lines',
            '  - [Step 1 review round 1](#step-1-review-round-1) — 7 lines',
            '- [P07-2 — Consumer](#p07-2--consumer) — 5 lines',
            '  - [Step 1 review round 1](#step-1-review-round-1-1) — 3 lines',
            '- [Status archive](#status-archive) — 4 lines',
        ], index.rows(LOG.split('\n')))

    def test_link_text_in_a_heading_is_kept_as_plain_text(self):
        self.assertEqual(['- [See ADR-0069 (2026-10-23)](#see-adr-0069-2026-10-23) — 1 lines'],
                         index.rows(['## See [ADR-0069](x.md) (2026-10-23)']))


class BlockTests(unittest.TestCase):
    def test_regenerate_is_idempotent_and_touches_only_the_block(self):
        once = index.regenerate(LOG)
        self.assertEqual(once, index.regenerate(once))
        outside = lambda text: text.split(index.BEGIN)[0] + text.split(index.END)[1]
        self.assertEqual(outside(LOG), outside(once))
        self.assertIn('**Index**', once)

    def test_missing_duplicated_or_misplaced_markers_are_refused(self):
        for text, reason in (
                (LOG.replace(index.BEGIN + '\n', ''), 'exactly one'),
                (LOG.replace(index.END, index.END + '\n' + index.BEGIN + '\n' + index.END), 'exactly one'),
                (LOG.replace(index.BEGIN + '\n' + index.END + '\n', '')
                 .replace('## Status archive', index.BEGIN + '\n' + index.END + '\n## Status archive'),
                 'before the first ## section')):
            with self.subTest(reason=reason):
                with self.assertRaisesRegex(ValueError, reason):
                    index.regenerate(text)


class RepositoryTests(unittest.TestCase):
    def run_cli(self, root, *args):
        return subprocess.run([sys.executable, str(root / 'scripts/delivery-log-index.py'), *args],
                              capture_output=True, text=True)

    def fixture(self):
        directory = tempfile.TemporaryDirectory(prefix='learnstack-log-index-')
        root = Path(directory.name)
        (root / 'scripts').mkdir()
        for name in ('delivery-log-index.py', 'markdown-fragment-audit.py'):
            shutil.copy(ROOT / 'scripts' / name, root / 'scripts' / name)
        shutil.copytree(ROOT / 'docs/roadmap', root / 'docs/roadmap')
        return directory, root

    def test_every_actual_delivery_log_has_a_current_index(self):
        found = index.logs(ROOT)
        self.assertGreaterEqual(len(found), 2)
        for path in found:
            with self.subTest(log=path.name):
                text = path.read_text()
                self.assertEqual(text, index.regenerate(text))

    def test_an_entry_appended_to_the_actual_log_fails_until_regenerated(self):
        directory, root = self.fixture()
        with directory:
            self.assertEqual(0, self.run_cli(root).returncode)
            log = root / 'docs/roadmap/phase-02b-delivery-log.md'
            text = log.read_text()
            log.write_text(text.replace('## Status archive',
                                        '### P02b-6 later correction (2026-10-24)\n\nRecord.\n\n## Status archive', 1))
            stale = self.run_cli(root)
            self.assertEqual(1, stale.returncode, stale.stdout)
            self.assertIn('phase-02b-delivery-log.md: its index is stale', stale.stdout)
            self.assertEqual(0, self.run_cli(root, '--write').returncode)
            self.assertIn('[P02b-6 later correction (2026-10-24)]', log.read_text())
            self.assertEqual((0, ''), (self.run_cli(root).returncode, self.run_cli(root).stderr))

    def test_a_hand_edited_row_fails(self):
        directory, root = self.fixture()
        with directory:
            log = root / 'docs/roadmap/phase-02d-delivery-log.md'
            text = log.read_text()
            log.write_text(text.replace('— 276 lines', '— 275 lines', 1))
            self.assertEqual(1, self.run_cli(root).returncode)

    def test_unknown_arguments_are_refused(self):
        self.assertEqual(2, self.run_cli(ROOT, '--check').returncode)

    def test_actual_workflow_and_make_targets_run_the_generator(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        makefile = (ROOT / 'Makefile').read_text()
        self.assertIn('python3 scripts/delivery-log-index.py\n', workflow)
        self.assertIn('python3 scripts/test-delivery-log-index.py', workflow)
        self.assertIn('\tpython3 scripts/delivery-log-index.py\n', makefile)
        self.assertIn('\tpython3 scripts/delivery-log-index.py --write\n', makefile)


if __name__ == '__main__':
    unittest.main()
