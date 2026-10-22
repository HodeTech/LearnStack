#!/usr/bin/env python3
"""Prove the fragment audit fails on planted breaks and passes on the real tree."""
import importlib.util
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = Path(__file__).with_name('markdown-fragment-audit.py')
SPEC = importlib.util.spec_from_file_location('markdown_fragment_audit', SCRIPT)
audit = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audit)


def tree(files):
    directory = tempfile.TemporaryDirectory(prefix='learnstack-fragment-audit-')
    root = Path(directory.name)
    for name, text in files.items():
        (root / name).parent.mkdir(parents=True, exist_ok=True)
        (root / name).write_text(text)
    return directory, root


class SlugTests(unittest.TestCase):
    def test_github_slugs_of_real_heading_shapes(self):
        cases = {
            'P02b-6 Step 3 — shared seed/demo lifecycle (2026-10-21)':
                'p02b-6-step-3--shared-seeddemo-lifecycle-2026-10-21',
            'Identity integration — the `learnstack` realm': 'identity-integration--the-learnstack-realm',
            'Accepted G12/G13 answers': 'accepted-g12g13-answers',
            'Delivery Record (Packets 0–3)': 'delivery-record-packets-03',
            'Tenant runtime — host header to tenant_id resolution':
                'tenant-runtime--host-header-to-tenant_id-resolution',
            'Cohort × LiveSession × LiveBooking': 'cohort--livesession--livebooking',
            '[Linked](x.md) title': 'linked-title',
        }
        for text, expected in cases.items():
            with self.subTest(text=text):
                self.assertEqual(expected, audit.slug(text))

    def test_duplicates_are_numbered_in_order_and_fences_are_not_headings(self):
        found = audit.anchors('# A\n## Step 1 review round 1\n```\n## Step 1 review round 1\n```\n'
                              '### Step 1 review round 1\n<a id="kept"></a>\n')
        self.assertEqual({'a', 'step-1-review-round-1', 'step-1-review-round-1-1', 'kept'}, found)


class AuditTests(unittest.TestCase):
    FILES = {
        'docs/a.md': '# Title\n## Moved section\n## Twice\n## Twice\n',
        'docs/b.md': ('[ok](a.md#moved-section) [dup](a.md#twice-1) [self](#local)\n'
                      '## Local\n[repo](docs/a.md#title) [web](https://x.test/a.md#nope)\n'
                      '[code](../src/Note.cs#L1) [missing](gone.md#nope) `[sample](a.md#nope)`\n'
                      '```\n[fenced](a.md#nope)\n```\n'),
        'src/Note.cs': '/// <see href="../docs/a.md#twice">twice</see>\n',
    }

    def run_audit(self, extra=None):
        directory, root = tree({**self.FILES, **(extra or {})})
        with directory:
            return audit.audit(root, sorted(str(p.relative_to(root)) for p in root.rglob('*') if p.is_file()))

    def test_every_resolvable_shape_passes(self):
        problems, checked = self.run_audit()
        self.assertEqual([], problems)
        self.assertEqual(5, checked)

    def test_each_planted_break_is_reported(self):
        planted = {
            'docs/c.md': '[gone](a.md#moved-section-2026-10-22)\n',
            'docs/d.md': '[third](a.md#twice-2)\n',
            'docs/e.md': '[self](#nowhere)\n# Here\n',
            'src/Other.cs': '/// <see href="../docs/a.md#renamed">x</see>\n',
            'docs/nested/f.md': '[rel](../a.md#missing)\n',
        }
        problems, _ = self.run_audit(planted)
        reported = sorted(problem.split(':', 1)[0] for problem in problems)
        self.assertEqual(sorted(planted), reported)

    def test_actual_repository_has_no_broken_fragment(self):
        problems, checked = audit.audit(ROOT, audit.tracked(ROOT))
        self.assertEqual([], problems)
        self.assertGreater(checked, 1000)

    def test_renaming_a_real_heading_breaks_its_real_inbound_links(self):
        with tempfile.TemporaryDirectory(prefix='learnstack-fragment-real-') as directory:
            root = Path(directory)
            names = ['docs/roadmap/phase-02b-events-auth.md', 'docs/roadmap/phase-02b-delivery-log.md']
            for name in names:
                (root / name).parent.mkdir(parents=True, exist_ok=True)
                shutil.copy(ROOT / name, root / name)
            self.assertEqual([], audit.audit(root, names)[0])
            log = root / names[1]
            log.write_text(log.read_text().replace('### P02b-4 merge and closeout (2026-10-19)\n',
                                                   '### P02b-4 merge (2026-10-19)\n', 1))
            problems = audit.audit(root, names)[0]
            self.assertTrue(any(p.startswith(names[0]) and 'p02b-4-merge-and-closeout-2026-10-19' in p
                                for p in problems), problems)

    def test_a_reordered_duplicate_still_resolves_which_only_order_preservation_catches(self):
        # The audit proves a fragment exists, not that it names the same section as before:
        # renaming the first of two same-named headings hands its anchor to the second.
        # ADR-0069's migration verified each heading's anchor individually for this reason.
        directory, root = tree({'a.md': '## Twice\n## Twice\n', 'b.md': '[x](a.md#twice)\n'})
        with directory:
            (root / 'a.md').write_text('## Once\n## Twice\n')
            self.assertEqual([], audit.audit(root, ['a.md', 'b.md'])[0])

    def test_actual_cli_reports_and_fails_on_a_planted_break(self):
        directory, root = tree({'scripts/markdown-fragment-audit.py': SCRIPT.read_text(),
                                'a.md': '# A\n', 'b.md': '[x](a.md#b)\n'})
        with directory:
            subprocess.run(['git', 'init', '-q'], cwd=root, check=True)
            result = subprocess.run([sys.executable, str(root / 'scripts/markdown-fragment-audit.py')],
                                    cwd=root, capture_output=True, text=True)
            self.assertEqual(1, result.returncode, result.stdout)
            self.assertIn('b.md:1: #b is not an anchor of a.md', result.stdout)
            (root / 'b.md').write_text('[x](a.md#a)\n')
            result = subprocess.run([sys.executable, str(root / 'scripts/markdown-fragment-audit.py')],
                                    cwd=root, capture_output=True, text=True)
            self.assertEqual((0, ''), (result.returncode, result.stderr))

    def test_actual_workflow_and_make_target_run_the_audit(self):
        self.assertIn('python3 scripts/markdown-fragment-audit.py',
                      (ROOT / '.github/workflows/ci.yml').read_text())
        self.assertIn('python3 scripts/markdown-fragment-audit.py', (ROOT / 'Makefile').read_text())


if __name__ == '__main__':
    unittest.main()
