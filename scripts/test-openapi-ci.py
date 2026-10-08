#!/usr/bin/env python3
"""Falsifiable controls for event selection, snapshot admission and bootstrap."""
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import textwrap
import unittest
from unittest.mock import patch

MODULE_SPEC = importlib.util.spec_from_file_location('openapi_ci', Path(__file__).with_name('openapi-ci.py'))
ci = importlib.util.module_from_spec(MODULE_SPEC)
MODULE_SPEC.loader.exec_module(ci)
REAL_RUN = subprocess.run
EMPTY = {'openapi': '3.1.0', 'info': {'title': 'Fixture', 'version': '1'}, 'paths': {}}
CURRENT = {**EMPTY, 'paths': {'/api/v1/fixture': {'get': {'responses': {'200': {'description': 'OK'}}}}}}


class SnapshotControls(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='learnstack-openapi-control-')
        self.root = Path(self.temporary.name)
        self.root_patch = patch.object(ci, 'ROOT', self.root)
        self.root_patch.start()
        self.git('init', '-q')
        self.git('config', 'user.name', 'Compatibility test')
        self.git('config', 'user.email', 'test@example.invalid')
        (self.root / 'fixture.txt').write_text('Synthetic git data, not a project checkout.\n')
        self.base = self.save()

    def tearDown(self):
        self.root_patch.stop()
        self.temporary.cleanup()

    def git(self, *arguments):
        return REAL_RUN(['git', *arguments], cwd=self.root, check=True, capture_output=True).stdout.decode().strip()

    def save(self, value=None):
        file = self.root / ci.SNAPSHOT
        if value is not None:
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text(json.dumps(value))
        self.git('add', '.')
        self.git('-c', 'core.hooksPath=/dev/null', 'commit', '-qm', 'Synthetic control')
        return self.git('rev-parse', 'HEAD')

    def run_ci(self, base, head):
        def run(command, **kwargs):
            if command[0] == sys.executable:
                return subprocess.CompletedProcess(command, 0)
            return REAL_RUN(command, **kwargs)
        with patch.object(sys, 'argv', ['openapi-ci.py', '--base', base, '--head', head,
                '--tool', str(self.root / 'tool'), '--output', str(self.root / 'reports')]), patch.object(ci.subprocess, 'run', side_effect=run):
            return ci.main()

    def select_event(self, event, head, before='', pr_base='', pr_head='', manual_base=''):
        workflow = (Path(__file__).resolve().parents[1] / '.github/workflows/ci.yml').read_text()
        selections = re.findall(r'(?m)^          case "\$EVENT_NAME" in\n(?:.*\n)*?          esac\n', workflow)
        self.assertEqual(1, len(selections), 'Exercise the actual workflow event selector')
        script = 'set -euo pipefail\n' + textwrap.dedent(selections[0])
        script += '\nprintf "%s\\n%s\\n" "$openapi_base" "$openapi_head"\n'
        result = REAL_RUN(['bash', '-c', script], cwd=self.root, check=True, capture_output=True, text=True,
            env={**os.environ, 'EVENT_NAME': event, 'HEAD_SHA': head, 'PUSH_BEFORE_SHA': before,
                 'PR_BASE_SHA': pr_base, 'PR_HEAD_SHA': pr_head, 'MANUAL_BASE_REF': manual_base})
        return result.stdout.splitlines()

    def test_new_branch_push_uses_the_selected_heads_verified_parent(self):
        parent = self.save(CURRENT)
        head = self.save({**CURRENT, 'info': {**CURRENT['info'], 'title': 'Selected head'}})
        self.save({**CURRENT, 'info': {**CURRENT['info'], 'title': 'Later checkout'}})
        base_ref, head_ref = self.select_event('push', head, before='0' * 40)
        self.assertEqual(parent, ci.commit(base_ref))
        self.assertEqual(head, head_ref)
        self.assertEqual(0, self.run_ci(base_ref, head_ref))
        recorded = json.loads((self.root / 'reports/commits.json').read_text())
        self.assertEqual((parent, head, False), (recorded['base'], recorded['head'], recorded['firstBaseline']))

    def test_existing_branch_push_preserves_before_across_multiple_commits(self):
        before = self.save(CURRENT)
        self.save({**CURRENT, 'info': {**CURRENT['info'], 'title': 'Intermediate'}})
        head = self.save({**CURRENT, 'info': {**CURRENT['info'], 'title': 'Head'}})
        self.assertEqual([before, head], self.select_event('push', head, before=before))

    def test_pull_request_preserves_its_base_and_head(self):
        head = self.save(CURRENT)
        self.assertEqual([self.base, head], self.select_event('pull_request', 'unused-runner-head',
            before='0' * 40, pr_base=self.base, pr_head=head))

    def test_manual_run_preserves_its_explicit_base_ref(self):
        self.git('branch', 'manual-base', self.base)
        head = self.save(CURRENT)
        base_ref, head_ref = self.select_event('workflow_dispatch', head, manual_base='manual-base')
        self.assertEqual('manual-base', base_ref)
        self.assertEqual(self.base, ci.commit(base_ref))
        self.assertEqual(head, head_ref)

    def test_unknown_event_is_refused(self):
        with self.assertRaises(subprocess.CalledProcessError) as error:
            self.select_event('pull_request_target', self.base)
        self.assertIn('Unsupported OpenAPI event', error.exception.stderr)

    def test_new_branch_push_without_a_parent_is_refused(self):
        base_ref, head_ref = self.select_event('push', self.base, before='0' * 40)
        with patch.object(ci, 'bootstrap') as bootstrap, self.assertRaises(subprocess.CalledProcessError):
            self.run_ci(base_ref, head_ref)
        bootstrap.assert_not_called()

    def test_missing_head_snapshot_is_refused_before_bootstrap(self):
        with patch.object(ci, 'bootstrap') as bootstrap, self.assertRaisesRegex(ValueError, 'Head snapshot'):
            self.run_ci(self.base, self.base)
        bootstrap.assert_not_called()

    def test_deleted_head_snapshot_is_not_a_new_bootstrap(self):
        previous = self.save(CURRENT)
        (self.root / ci.SNAPSHOT).unlink()
        deleted = self.save()
        with patch.object(ci, 'bootstrap') as bootstrap, self.assertRaisesRegex(ValueError, 'Head snapshot'):
            self.run_ci(previous, deleted)
        bootstrap.assert_not_called()

    def test_missing_verified_ref_is_not_bootstrap(self):
        head = self.save(CURRENT)
        with patch.object(ci, 'bootstrap') as bootstrap, self.assertRaises(subprocess.CalledProcessError):
            self.run_ci('missing-control-ref', head)
        bootstrap.assert_not_called()

    def test_missing_snapshot_requires_actual_bootstrap_proof(self):
        head = self.save(CURRENT)
        with patch.object(ci, 'bootstrap', side_effect=ValueError('Existing v1 operations')) as bootstrap, self.assertRaisesRegex(ValueError, 'Existing v1'):
            self.run_ci(self.base, head)
        bootstrap.assert_called_once_with(self.base, self.root / 'reports')

    def test_bootstrap_fetch_or_build_error_does_not_pass(self):
        head = self.save(CURRENT)
        with patch.object(ci, 'bootstrap', side_effect=subprocess.CalledProcessError(1, 'dotnet')), self.assertRaises(subprocess.CalledProcessError):
            self.run_ci(self.base, head)

    def test_verified_operation_free_base_is_the_only_bootstrap_success(self):
        head = self.save(CURRENT)
        with patch.object(ci, 'bootstrap', return_value=json.dumps(EMPTY).encode()) as bootstrap:
            self.assertEqual(0, self.run_ci(self.base, head))
        bootstrap.assert_called_once()
        self.assertTrue(json.loads((self.root / 'reports/commits.json').read_text())['firstBaseline'])

    def test_existing_snapshot_never_uses_the_bootstrap_exception(self):
        previous = self.save(CURRENT)
        head = self.save({**CURRENT, 'info': {**CURRENT['info'], 'title': 'Updated'}})
        with patch.object(ci, 'bootstrap') as bootstrap:
            self.assertEqual(0, self.run_ci(previous, head))
        bootstrap.assert_not_called()
        self.assertFalse(json.loads((self.root / 'reports/commits.json').read_text())['firstBaseline'])

    def test_empty_head_is_refused(self):
        head = self.save(EMPTY)
        with self.assertRaisesRegex(ValueError, 'no v1 operations'):
            self.run_ci(self.base, head)


if __name__ == '__main__':
    unittest.main()
