#!/usr/bin/env python3
"""Run the complete source-censused realm controls with exact executed counts."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
# Update only with reviewed additions/removals, alongside their executing proof.
# Per-file expectations also fix the total; an empty or deleted test file cannot be
# replaced by a passing named suite or by extra tests in another file.
EXPECTED = {
    'development-realm-events.test.mjs': 51,
    'development-realm-core.test.mjs': 63,
    'development-realm-http.test.mjs': 42,
    'development-realm-owner.test.mjs': 56,
    'development-realm-plan.test.mjs': 106,
    'development-realm-preparation.test.mjs': 28,
    'development-realm-stock-console.test.mjs': 25,
    'reconcile-development-realm.test.mjs': 24,
}


def run():
    scripts = ROOT / 'scripts'
    actual = {path.name for path in scripts.glob('development-realm-*.test.mjs')}
    actual.update(path.name for path in scripts.glob('reconcile-development-realm*.test.mjs'))
    if actual != set(EXPECTED):
        raise ValueError('Development realm test-file census does not match its reviewed plan.')
    total = 0
    with tempfile.TemporaryDirectory(prefix='learnstack-realm-node-', dir=os.environ.get('RUNNER_TEMP')) as temporary:
        for index, (filename, expected) in enumerate(EXPECTED.items()):
            if type(expected) is not int or expected <= 0:
                raise ValueError('Development realm expected count must be positive.')
            report = Path(temporary) / f'{index}.xml'
            environment = dict(os.environ, LEARNSTACK_NODE_EXPECTED_TESTS=str(expected))
            subprocess.run(['node', '--test', '--test-reporter=./scripts/require-node-test-cases.mjs',
                '--test-reporter-destination=stdout', '--test-reporter=junit',
                '--test-reporter-destination=' + str(report), 'scripts/' + filename],
                cwd=ROOT, env=environment, check=True)
            root = ET.parse(report).getroot()
            cases = list(root.iter('testcase'))
            empty_file_names = {'scripts/' + filename, str((scripts / filename).resolve())}
            if (root.tag != 'testsuites' or len(cases) != expected
                    or any(case.get('name') in empty_file_names for case in cases)
                    or any(list(root.iter(tag)) for tag in ('failure', 'error', 'skipped'))):
                raise ValueError('Development realm JUnit requires the exact named passing cases.')
            total += expected
            print(f'{filename}: {expected} passed; zero cancellations/skips/todos.', flush=True)
    print(f'Complete development realm census: {len(EXPECTED)} files, {total} executed passing tests.', flush=True)


if __name__ == '__main__':
    if len(sys.argv) != 1:
        raise SystemExit('Development realm controls accept no arguments.')
    run()
