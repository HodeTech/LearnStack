#!/usr/bin/env python3
"""Exercise the real workflow link audit across squash merges and missing bases."""
from contextlib import contextmanager
import os
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[1]


def audit_script():
    workflow = (ROOT / '.github/workflows/ci.yml').read_text()
    block = workflow.split('      - name: Markdown link audit (changed docs)\n', 1)[1].split('      - name:', 1)[0]
    return textwrap.dedent(block.split('        run: |\n', 1)[1])


@contextmanager
def squashed_fixture(*, broken_change=False, broken_legacy=False):
    with tempfile.TemporaryDirectory(prefix='learnstack-link-audit-') as directory:
        root = Path(directory)
        env = {key: os.environ[key] for key in ('PATH', 'HOME', 'TMPDIR') if key in os.environ}
        env.update(GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull, GIT_TERMINAL_PROMPT='0', LC_ALL='C')

        def git(where, *args, check=True):
            result = subprocess.run(['git', *args], cwd=where, env=env, capture_output=True, text=True, timeout=10)
            if check and result.returncode:
                raise AssertionError(result.stderr)
            return result

        origin = root / 'origin.git'
        git(root, 'init', '--bare', '--initial-branch=main', str(origin))
        work = root / 'work'
        work.mkdir()
        git(work, 'init', '--initial-branch=main')
        git(work, 'config', 'user.name', 'Link audit fixture')
        git(work, 'config', 'user.email', 'fixture@example.invalid')
        git(work, 'config', 'core.hooksPath', os.devnull)
        docs = work / 'docs'
        docs.mkdir()
        (docs / 'target.md').write_text('# Target\n')
        (docs / 'changed.md').write_text('# Before\n')
        (docs / 'legacy.md').write_text('[legacy](missing-legacy.md)\n' if broken_legacy else '# Legacy\n')
        git(work, 'add', '.')
        git(work, 'commit', '-qm', 'Fixture base')
        base = git(work, 'rev-parse', 'HEAD').stdout.strip()
        target = 'missing-changed.md' if broken_change else 'target.md'
        (docs / 'changed.md').write_text(
            '# After\n[changed](' + target + ')\n[external](https://example.invalid/external)\n'
            '[anchor](#section)\n```md\n[example](missing-fenced.md)\n```\n'
            'Sample: `[inline](missing-inline.md)` is code, not a link.\n')
        git(work, 'add', '.')
        git(work, 'commit', '-qm', 'Fixture topic')
        head = git(work, 'rev-parse', 'HEAD').stdout.strip()
        tree = git(work, 'rev-parse', 'HEAD^{tree}').stdout.strip()
        merge = git(work, 'commit-tree', tree, '-p', base, '-p', head, '-m', 'Fixture PR checkout').stdout.strip()
        squash = git(work, 'commit-tree', tree, '-p', base, '-m', 'Fixture squash').stdout.strip()
        git(work, 'remote', 'add', 'origin', str(origin))
        git(work, 'push', 'origin', squash + ':refs/heads/main', head + ':refs/heads/development', merge + ':refs/pull/34/merge')
        checkout = root / 'checkout'
        git(root, 'clone', '--no-local', str(origin), str(checkout))
        git(checkout, 'fetch', '--no-tags', 'origin', 'refs/pull/34/merge')
        git(checkout, 'checkout', '--detach', merge)
        # Match the failure: a later depth-one fetch truncates the advanced
        # squash branch, while the old event base remains locally available.
        git(checkout, 'fetch', '--no-tags', '--depth=1', 'origin', 'main')
        assert git(checkout, 'rev-parse', '--verify', base + '^{commit}').returncode == 0
        assert git(checkout, 'merge-base', 'origin/main', 'HEAD', check=False).returncode == 1

        def run(event='pull_request', comparison=base):
            values = {**env, 'EVENT_NAME': event, 'PR_BASE_SHA': comparison,
                      'PR_BASE_REF': 'main', 'PUSH_BEFORE_SHA': comparison}
            return subprocess.run(['bash', '-eo', 'pipefail', '-c', audit_script()], cwd=checkout,
                                  env=values, capture_output=True, text=True, timeout=10)

        yield run, merge, squash, checkout, git


class MarkdownLinkAuditTests(unittest.TestCase):
    def test_closed_pr_rerun_uses_its_event_snapshot_after_a_squash(self):
        with squashed_fixture(broken_legacy=True) as (run, *_):
            result = run()
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn('Auditing 1 file(s)', result.stdout)
            self.assertNotIn('No changed Markdown', result.stdout)

    def test_closed_pr_rerun_still_refuses_a_broken_changed_link(self):
        with squashed_fixture(broken_change=True) as (run, *_):
            result = run()
            self.assertEqual(1, result.returncode, result.stderr)
            self.assertIn('BROKEN: docs/changed.md', result.stdout)
            self.assertNotIn('no merge base', result.stderr)
            self.assertNotIn('missing-inline.md', result.stdout)

    def test_push_uses_the_before_snapshot_without_ancestry_traversal(self):
        with squashed_fixture(broken_change=True) as (run, _, squash, checkout, git):
            git(checkout, 'checkout', '--detach', squash)
            result = run(event='push')
            self.assertEqual(1, result.returncode, result.stderr)
            self.assertIn('BROKEN: docs/changed.md', result.stdout)

    def test_missing_or_manual_base_audits_the_complete_tree(self):
        with squashed_fixture(broken_legacy=True) as (run, *_):
            for event, comparison in (('pull_request', 'f' * 40), ('push', '0' * 40), ('workflow_dispatch', '')):
                with self.subTest(event=event):
                    result = run(event, comparison)
                    self.assertEqual(1, result.returncode, result.stderr)
                    self.assertIn('auditing every tracked Markdown file', result.stdout)
                    self.assertIn('BROKEN: docs/legacy.md', result.stdout)

    def test_an_empty_valid_range_does_not_audit_unchanged_legacy_links(self):
        with squashed_fixture(broken_legacy=True) as (run, merge, *_):
            result = run(comparison=merge)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn('No changed Markdown files', result.stdout)


if __name__ == '__main__':
    unittest.main()
