#!/usr/bin/env python3
"""Run the actual CI Node/JUnit command and gate against executable counterexamples."""
import os
from pathlib import Path
import re
import shlex
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[1]
REPORTER_ARGS = ('--test-reporter=./scripts/require-node-test-cases.mjs '
                 '--test-reporter-destination=stdout ')


def workflow_step():
    workflow = (ROOT / '.github/workflows/ci.yml').read_text()
    marker = '      - name: Development realm transport, ownership and convergence controls\n'
    assert workflow.count(marker) == 1
    block = workflow.split(marker, 1)[1].split('\n      - name:', 1)[0]
    return textwrap.dedent(block.split('        run: |\n', 1)[1])


class RealmNodeGateControls(unittest.TestCase):
    def execute(self, source, *, without_counter_guard=False):
        with tempfile.TemporaryDirectory(prefix='learnstack-node-gate-') as temporary:
            directory = Path(temporary)
            test_file = directory / 'fixture.test.mjs'
            test_file.write_text(source)
            step, count = re.subn(r'for test_file in scripts/development-realm-core\.test\.mjs \\\n'
                                 r'.*?scripts/reconcile-development-realm\.test\.mjs; do',
                                 'for test_file in ' + shlex.quote(str(test_file)) + '; do',
                                 workflow_step(), count=1, flags=re.DOTALL)
            self.assertEqual(1, count, 'replace only the actual fixed workflow test-file selection')
            self.assertEqual(1, step.count(REPORTER_ARGS))
            if without_counter_guard:
                step = step.replace(REPORTER_ARGS, '', 1)
            environment = {key: os.environ[key] for key in ('PATH', 'HOME') if key in os.environ}
            environment['RUNNER_TEMP'] = str(directory)
            return subprocess.run(['bash', '-c', step], cwd=ROOT, env=environment,
                                  capture_output=True, text=True, timeout=10)

    def test_real_cases_and_nested_real_cases_pass_the_actual_workflow_gate(self):
        for source in ("import {test} from 'node:test'; test('real',()=>{});",
                       "import {test,describe} from 'node:test'; describe('suite',()=>{"
                       "describe('nested',()=>{test('real',()=>{});});});"):
            with self.subTest(source=source):
                result = self.execute(source)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn('1 executed tests passed;', result.stdout)

    def test_empty_named_suites_need_the_real_counter_guard(self):
        for source in ("import {describe} from 'node:test'; describe('empty named suite',()=>{});",
                       "import {describe} from 'node:test'; describe('outer',()=>{describe('inner',()=>{});});"):
            with self.subTest(source=source):
                dirty = self.execute(source, without_counter_guard=True)
                self.assertEqual(0, dirty.returncode, 'the original JUnit-only gate must expose this defect')
                self.assertNotEqual(0, self.execute(source).returncode)

    def test_empty_file_fail_skip_todo_cancel_and_nested_skip_are_refused(self):
        for source in ('',
                       "import {test} from 'node:test'; test('failed',()=>{throw Error('planted');});",
                       "import {test} from 'node:test'; test.skip('skipped',()=>{});",
                       "import {test} from 'node:test'; test.todo('todo');",
                       "import {test} from 'node:test'; test('cancelled',()=>new Promise(()=>{}));",
                       "import {test,describe} from 'node:test'; describe('suite',()=>{test.skip('skip',()=>{});});"):
            with self.subTest(source=source):
                self.assertNotEqual(0, self.execute(source).returncode)


if __name__ == '__main__':
    unittest.main()
