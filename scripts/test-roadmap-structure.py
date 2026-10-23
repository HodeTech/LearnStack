#!/usr/bin/env python3
"""Prove the roadmap structure guard fails on each planted violation and passes the real tree."""
import importlib.util
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = Path(__file__).with_name('roadmap-structure.py')
SPEC = importlib.util.spec_from_file_location('roadmap_structure', SCRIPT)
guard = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(guard)

PLAN = ('# Phase 07: Fixture\n\n> **Status (2026-10-22).** In progress; records live in the\n'
        '> [delivery log](phase-07-delivery-log.md).\n\n## Goal\n\n## Scope\n\n### Plan detail\n\n'
        '## Deliverables\n\n## Completion Criteria\n\n## Risks\n\n## Phase Exit Decision\n\nGate.\n')
LOG = ('# Phase 07 — delivery log\n\nThe record of [Phase 07](phase-07-fixture.md).\n\n'
       '## P07-0 — Entry\n\n### P07-0 entry verification (2026-10-20)\n\n#### Undated detail\n\n'
       '## P07-1 — Producer\n\n### P07-1 decision package (2026-10-21)\n\n'
       '## Status archive\n\nRetired prose.\n')
INDEX = ('# Roadmap\n\n## Phases\n\n- [Phase 07](phase-07-fixture.md) — **in progress**;\n'
         '  its Status block owns packet state.\n\n## Decision Timing\n\n'
         '[accepted answer](phase-07-delivery-log.md#p07-1-decision-package-2026-10-21)\n')
CLAUDE = '# Guide\n\n## What state this is in\n\nPointer.\n\n## Where to start\n\nRead.\n'
README = '# LearnStack\n\n## Where it is today\n\nPointer.\n\n## Quickstart\n'


class Fixture:
    def __init__(self, **overrides):
        self.directory = tempfile.TemporaryDirectory(prefix='learnstack-roadmap-guard-')
        self.root = Path(self.directory.name)
        files = {'docs/roadmap/phase-07-fixture.md': PLAN, 'docs/roadmap/phase-07-delivery-log.md': LOG,
                 'docs/roadmap/README.md': INDEX, 'CLAUDE.md': CLAUDE, 'README.md': README}
        files.update(overrides)
        for name, text in files.items():
            if text is None:
                continue
            (self.root / name).parent.mkdir(parents=True, exist_ok=True)
            (self.root / name).write_text(text)

    def __enter__(self):
        return guard.check(self.root)

    def __exit__(self, *_):
        self.directory.cleanup()


def status_lines(count):
    return ''.join(f'> line {n}\n' for n in range(count))


class GuardTests(unittest.TestCase):
    def assertFlags(self, fragment, **overrides):
        with Fixture(**overrides) as problems:
            self.assertTrue(any(fragment in problem for problem in problems), problems)

    def test_conforming_fixture_and_actual_repository_pass(self):
        with Fixture() as problems:
            self.assertEqual([], problems)
        self.assertEqual([], guard.check(ROOT))

    def test_phase_document_record_headings_fail(self):
        for heading in ('### P07-1 Step 2 — writers', '### Writers (2026-10-22)',
                        '#### Step 2 review round 1', '### Merge and closeout',
                        '### Decision package', '### Delivery record'):
            with self.subTest(heading=heading):
                self.assertFlags('record heading in a phase document',
                                 **{'docs/roadmap/phase-07-fixture.md': PLAN.replace(
                                     '### Plan detail', heading)})

    def test_phase_document_shape_fails(self):
        self.assertFlags('nothing may follow ## Phase Exit Decision',
                         **{'docs/roadmap/phase-07-fixture.md': PLAN + '\n## Delivery Record (P07-1)\n'})
        self.assertFlags('missing or out of order: ## Risks',
                         **{'docs/roadmap/phase-07-fixture.md': PLAN.replace('## Risks\n', '')})

    def test_status_and_file_budgets_fail_one_past_the_limit(self):
        def plan(extra):
            return PLAN.replace('## Goal', status_lines(extra) + '\n## Goal')
        with Fixture(**{'docs/roadmap/phase-07-fixture.md': plan(38)}) as problems:
            self.assertEqual([], problems)
        self.assertFlags('Status block has 41 non-blank lines',
                         **{'docs/roadmap/phase-07-fixture.md': plan(39)})
        long_plan = PLAN.replace('### Plan detail', '### Plan detail\n' + 'text\n' * 1990)
        self.assertFlags('the budget is 2000', **{'docs/roadmap/phase-07-fixture.md': long_plan})

    def test_phase_document_must_link_its_log_and_log_its_phase_document(self):
        self.assertFlags('does not link phase-07-delivery-log.md',
                         **{'docs/roadmap/phase-07-fixture.md': PLAN.replace('phase-07-delivery-log.md', 'x.md')})
        self.assertFlags('needs exactly one phase document',
                         **{'docs/roadmap/phase-07-fixture.md': None})
        self.assertFlags('introduction does not link',
                         **{'docs/roadmap/phase-07-delivery-log.md': LOG.replace('(phase-07-fixture.md)', '(x.md)')})

    def test_delivery_log_sections_and_entries_fail(self):
        log = 'docs/roadmap/phase-07-delivery-log.md'
        self.assertFlags('neither a packet section', **{log: LOG.replace('## P07-1 — Producer', '## Notes')})
        self.assertFlags('not in packet order', **{log: LOG.replace('## P07-0 — Entry', '## P07-2 — Entry')})
        self.assertFlags('entry heading carries no date',
                         **{log: LOG.replace('### P07-1 decision package (2026-10-21)', '### P07-1 decision package')})
        self.assertFlags('Status archive is the last section', **{log: LOG + '\n## Cross-packet records\n'})
        self.assertFlags('Phase entry precedes every packet section',
                         **{log: LOG.replace('## Status archive', '## Phase entry\n\n## Status archive')})

    def test_carriers_fail_on_log_links_budgets_and_missing_sections(self):
        link = '[record](docs/roadmap/phase-07-delivery-log.md#x)\n'
        self.assertFlags('CLAUDE.md:10: links a delivery log', **{'CLAUDE.md': CLAUDE + link})
        self.assertFlags('README.md:8: links a delivery log', **{'README.md': README + link})
        self.assertFlags('What state this is in has 41 non-blank lines',
                         **{'CLAUDE.md': CLAUDE.replace('Pointer.\n', 'line\n' * 40)})
        self.assertFlags('## Where it is today is missing', **{'README.md': '# LearnStack\n'})

    def test_index_bounds_phase_entries_but_not_decision_citations(self):
        long_entry = '- [Phase 07](phase-07-fixture.md)\n' + '  more\n\n' * 6
        self.assertFlags('§ Phases entry has 7 non-blank lines',
                         **{'docs/roadmap/README.md': INDEX.replace(
                             '- [Phase 07](phase-07-fixture.md) — **in progress**;\n'
                             '  its Status block owns packet state.\n', long_entry)})
        self.assertFlags('§ Phases links a delivery log',
                         **{'docs/roadmap/README.md': INDEX.replace('(phase-07-fixture.md)', '(phase-07-delivery-log.md#x)')})

    def test_declared_shapes_pass_and_frozen_phase_02a_is_exempt(self):
        pointer = '# Phase 12\n\n## Goal\n\n## Standing\n\n## Scope on the LearnStack side\n\n## Trigger\n\n## Phase Exit Decision\n'
        frozen = PLAN + '\n## Delivery Record (Packet 9)\n\n### The fifth review (2026-09-11)\n'
        with Fixture(**{'docs/roadmap/phase-12-hub-marketplace.md': pointer,
                        'docs/roadmap/phase-02a-kernel-tenancy.md': frozen}) as problems:
            self.assertEqual([], problems)

    def test_a_record_appended_to_the_actual_phase_02b_document_fails(self):
        with tempfile.TemporaryDirectory(prefix='learnstack-roadmap-real-') as directory:
            root = Path(directory)
            shutil.copytree(ROOT / 'docs/roadmap', root / 'docs/roadmap')
            for name in ('CLAUDE.md', 'README.md'):
                shutil.copy(ROOT / name, root / name)
            self.assertEqual([], guard.check(root))
            phase = root / 'docs/roadmap/phase-02b-events-auth.md'
            text = phase.read_text()
            phase.write_text(text.replace('## Deliverables\n',
                                          '### P02b-7 decision package (2026-10-23)\n\nRecord.\n\n## Deliverables\n', 1))
            self.assertTrue(any('record heading in a phase document' in p for p in guard.check(root)))

    def test_actual_cli_exit_codes(self):
        result = subprocess.run([sys.executable, str(SCRIPT)], capture_output=True, text=True)
        self.assertEqual((0, ''), (result.returncode, result.stderr), result.stdout)
        fixture = Fixture(**{'CLAUDE.md': CLAUDE + '[x](docs/roadmap/phase-07-delivery-log.md)\n'})
        try:
            (fixture.root / 'scripts').mkdir()
            shutil.copy(SCRIPT, fixture.root / 'scripts/roadmap-structure.py')
            result = subprocess.run([sys.executable, str(fixture.root / 'scripts/roadmap-structure.py')],
                                    capture_output=True, text=True)
            self.assertEqual(1, result.returncode, result.stdout)
            self.assertIn('CLAUDE.md:10: links a delivery log', result.stdout)
        finally:
            fixture.directory.cleanup()

    def test_actual_workflow_and_make_target_run_the_guard(self):
        self.assertIn('python3 scripts/roadmap-structure.py', (ROOT / '.github/workflows/ci.yml').read_text())
        self.assertIn('python3 scripts/roadmap-structure.py', (ROOT / 'Makefile').read_text())


if __name__ == '__main__':
    unittest.main()
