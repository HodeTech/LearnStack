#!/usr/bin/env python3
"""Exercise the parser CI uses, including real Accepted ADR delivery banners."""
import importlib.util
from pathlib import Path
import subprocess
import os
import tempfile
import textwrap
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = Path(__file__).with_name('adr-status.py')
SPEC = importlib.util.spec_from_file_location('adr_status', SCRIPT)
status = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(status)


class LifecycleTests(unittest.TestCase):
    def test_lifecycle_survives_banners_and_prose_suffixes(self):
        for value in ('Proposed', 'Accepted', 'Superseded', 'Deprecated'):
            with self.subTest(value=value):
                source = '# ADR\n\n## Status\n\n> Accepted banner, not a decision\n\n' + value + ' — historical prose\n\n## Context\nAccepted elsewhere\n'
                self.assertEqual(value, status.lifecycle(source))

    def test_bold_superseded_status_is_not_accepted_original_decision(self):
        self.assertEqual('Superseded', status.lifecycle('## Status\n**Superseded by ADR-0018**\n## Original Decision\nAccepted\n'))

    def test_missing_or_ambiguous_lifecycle_fails_closed(self):
        for source in ('', '## Status\n> Accepted\n## Context\nAccepted\n', '## Status\nAccepted\nProposed\n', '## Status\nAccepted\n## Status\nAccepted\n', '## Status\n```\nAccepted\n```\n'):
            with self.subTest(source=source):
                with self.assertRaises(ValueError):
                    status.lifecycle(source)

    def test_real_banner_records_are_accepted_and_all_records_parse(self):
        for file in sorted((ROOT / 'docs/decisions').glob('[0-9]*.md')):
            with self.subTest(file=file.name):
                value = status.lifecycle(file.read_text())
                if file.name.startswith(('0050-', '0051-', '0052-')):
                    self.assertEqual('Accepted', value)

    def test_actual_cli_consumes_a_large_input_without_early_exit(self):
        source = '## Status\n> delivery banner\n\nAccepted\n## Context\n' + 'context\n' * 100000
        result = subprocess.run([sys.executable, str(SCRIPT)], input=source, capture_output=True, text=True)
        self.assertEqual((0, 'Accepted\n', ''), (result.returncode, result.stdout, result.stderr))
        refused = subprocess.run([sys.executable, str(SCRIPT)], input='## Status\n> Accepted\n', capture_output=True, text=True)
        self.assertEqual(1, refused.returncode)

    def test_actual_workflow_refuses_undisclosed_banner_record_changes(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        block = workflow.split('      - name: Accepted ADR disclosure\n', 1)[1].split('      - name:', 1)[0]
        script = 'set -euo pipefail\n' + textwrap.dedent(block.split('        run: |\n', 1)[1])
        with tempfile.TemporaryDirectory(prefix='learnstack-adr-disclosure-') as directory:
            root = Path(directory)
            (root / 'scripts').mkdir()
            (root / 'scripts/adr-status.py').write_text(SCRIPT.read_text())
            record = root / 'docs/decisions/0052-fixture.md'
            record.parent.mkdir(parents=True)
            git_env = {key: value for key, value in os.environ.items() if not key.startswith('GIT_')}
            def git(*args):
                return subprocess.check_output(['git', *args], cwd=root, env=git_env, text=True).strip()
            git('init', '-q')
            git('config', 'user.name', 'ADR fixture')
            git('config', 'user.email', 'fixture@example.invalid')
            record.write_text('## Status\n> Delivery banner\n\nAccepted\n## Decision\nOriginal.\n')
            git('add', '.')
            git('-c', 'core.hooksPath=/dev/null', 'commit', '-qm', 'Fixture base')
            base = git('rev-parse', 'HEAD')
            record.write_text(record.read_text().replace('Original.', 'Changed.'))
            git('add', '.')
            git('-c', 'core.hooksPath=/dev/null', 'commit', '-qm', 'Fixture edit')
            env = {**git_env, 'EVENT_NAME': 'push', 'PUSH_BEFORE_SHA': base}
            result = subprocess.run(['bash', '-c', script], cwd=root, env=env, capture_output=True, text=True)
            self.assertEqual(1, result.returncode)
            self.assertIn('UNDISCLOSED: docs/decisions/0052-fixture.md', result.stdout)
            record.write_text(record.read_text() + '\n## Amendment 1 — Disclosure (2026-10-09)\nFixture note.\n')
            git('add', '.')
            git('-c', 'core.hooksPath=/dev/null', 'commit', '-qm', 'Fixture disclosure')
            result = subprocess.run(['bash', '-c', script], cwd=root, env=env, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn('Accepted ADR disclosure: clean.', result.stdout)

    def test_workflow_uses_the_tested_parser_for_base_status(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        self.assertIn('python3 scripts/test-adr-status.py', workflow)
        self.assertIn('| python3 scripts/adr-status.py)', workflow)
        self.assertNotIn("want && NF { print; exit }", workflow)


if __name__ == '__main__':
    unittest.main()
