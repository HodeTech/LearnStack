#!/usr/bin/env python3
"""Execute the actual source-census/count/JUnit owner against counterexamples."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class RealmNodeGateControls(unittest.TestCase):
    def execute(self, sources, *, expected=None, without_counter_guard=False):
        with tempfile.TemporaryDirectory(prefix='learnstack-node-gate-') as temporary:
            directory = Path(temporary)
            scripts = directory / 'scripts'
            scripts.mkdir()
            for name, source in sources.items():
                (scripts / name).write_text(source)
            reporter = (ROOT / 'scripts/require-node-test-cases.mjs').read_text()
            (scripts / 'require-node-test-cases.mjs').write_text(reporter)
            owner = (ROOT / 'scripts/run-realm-node-tests.py').read_text()
            if without_counter_guard:
                needle = "'--test-reporter=./scripts/require-node-test-cases.mjs',\n                '--test-reporter-destination=stdout', "
                self.assertEqual(1, owner.count(needle))
                owner = owner.replace(needle, '', 1)
            (scripts / 'run-realm-node-tests.py').write_text(owner)
            plan = expected if expected is not None else {name: 1 for name in sources}
            wrapper = ("import importlib.util\n"
                "spec=importlib.util.spec_from_file_location('owner','scripts/run-realm-node-tests.py')\n"
                "owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)\n"
                "owner.EXPECTED=" + repr(plan) + "\nowner.run()\n")
            environment = {key: os.environ[key] for key in ('PATH', 'HOME') if key in os.environ}
            environment['RUNNER_TEMP'] = str(directory)
            return subprocess.run(['python3', '-c', wrapper], cwd=directory, env=environment,
                                  capture_output=True, text=True, timeout=10)

    def test_workflow_executes_the_single_actual_census_owner(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        marker = '      - name: Development realm transport, ownership and convergence controls\n'
        self.assertEqual(1, workflow.count(marker))
        block = workflow.split(marker, 1)[1].split('\n      - name:', 1)[0]
        self.assertIn('        run: python3 scripts/run-realm-node-tests.py\n', block)

    def test_real_cases_and_nested_real_cases_pass_the_actual_owner(self):
        for source in ("import {test} from 'node:test'; test('real',()=>{});",
                       "import {test,describe} from 'node:test'; describe('suite',()=>{"
                       "describe('nested',()=>{test('real',()=>{});});});"):
            with self.subTest(source=source):
                result = self.execute({'development-realm-fixture.test.mjs': source})
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn('1 executed tests passed;', result.stdout)

    def test_empty_named_suites_need_the_real_counter_guard(self):
        for source in ("import {describe} from 'node:test'; describe('empty named suite',()=>{});",
                       "import {describe} from 'node:test'; describe('outer',()=>{describe('inner',()=>{});});"):
            with self.subTest(source=source):
                sources = {'development-realm-fixture.test.mjs': source}
                dirty = self.execute(sources, without_counter_guard=True)
                self.assertEqual(0, dirty.returncode, 'JUnit-only gate must expose this defect')
                self.assertNotEqual(0, self.execute(sources).returncode)

    def test_empty_file_fail_skip_todo_cancel_and_nested_skip_are_refused(self):
        for source in ('',
                       "import {test} from 'node:test'; test('failed',()=>{throw Error('planted');});",
                       "import {test} from 'node:test'; test.skip('skipped',()=>{});",
                       "import {test} from 'node:test'; test.todo('todo');",
                       "import {test} from 'node:test'; test('cancelled',()=>new Promise(()=>{}));",
                       "import {test,describe} from 'node:test'; describe('suite',()=>{test.skip('skip',()=>{});});"):
            with self.subTest(source=source):
                self.assertNotEqual(0, self.execute({'development-realm-fixture.test.mjs': source}).returncode)

    def test_deleted_cases_cannot_be_hidden_by_a_named_passing_case_or_another_file(self):
        one = "import {test} from 'node:test';test('one',()=>{});"
        sources = {'development-realm-first.test.mjs': one, 'development-realm-second.test.mjs': one}
        expected = {'development-realm-first.test.mjs': 2, 'development-realm-second.test.mjs': 1}
        self.assertNotEqual(0, self.execute(sources, expected=expected).returncode)
        sources['development-realm-second.test.mjs'] += "test('extra',()=>{});"
        self.assertNotEqual(0, self.execute(sources, expected=expected).returncode)
        sources['development-realm-first.test.mjs'] += "test('two',()=>{});"
        sources['development-realm-second.test.mjs'] = one
        self.assertEqual(0, self.execute(sources, expected=expected).returncode)

    def test_added_or_missing_test_files_refuse_before_node_launch(self):
        one = "throw Error('node must not launch');"
        first, second = 'development-realm-first.test.mjs', 'development-realm-second.test.mjs'
        for sources, expected in (({first: one}, {first: 1, second: 1}),
                                  ({first: one, second: one}, {first: 1})):
            with self.subTest(sources=list(sources)):
                result = self.execute(sources, expected=expected)
                self.assertNotEqual(0, result.returncode)
                self.assertIn('test-file census', result.stderr)
                self.assertNotIn('node must not launch', result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
