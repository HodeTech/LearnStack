#!/usr/bin/env python3
"""Offline controls for the real launcher; no host cgroup/core/Docker mutation.

Only fixed proc/executable constants change in the byte-verified copy. Fixture
executables are real subprocesses, including exit, stdout and setsid behavior.
These controls do not qualify a hosted systemd/cgroup implementation or CI fit.
"""
from collections import Counter
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'scripts/ci-native-tests.py'

FIXTURE = r'''#!PYTHON
import json, os, pathlib, signal, sys, time
base = pathlib.Path(__file__).parent
config_path = base / 'fixture.json'
config = json.loads(config_path.read_text())
name = pathlib.Path(sys.argv[0]).name
with (base / 'calls.jsonl').open('a') as stream:
    stream.write(json.dumps({'name': name, 'argv': sys.argv[1:],
                            'stdin': None, 'secret': os.environ.get('PRIVATE_CANARY')}) + '\n')
if name == 'sudo':
    assert sys.argv[1] == '-n'
    os.execv(sys.argv[2], sys.argv[2:])
elif name == 'git':
    print('a' * 40)
elif name == 'systemd-run':
    assert '--wait' not in sys.argv and '--pipe' not in sys.argv
    log = next(arg.split('append:', 1)[1] for arg in sys.argv if arg.startswith('--property=StandardOutput='))
    pathlib.Path(log).parent.joinpath('ready.json').write_text('{"protocol":1,"ready":true}')
elif name == 'systemctl':
    if sys.argv[1] == 'show':
        for key, value in config.get('unit', {}).items():
            print(key + '=' + str(value))
elif name == 'docker':
    args = sys.argv[1:]
    if args[:2] == ['--host', 'unix:///var/run/docker.sock']:
        args = args[2:]
    key = ('image_info' if args[0] == 'info' else 'image_ids' if args[:2] == ['image', 'ls']
           else 'image_inspect' if args[:2] == ['image', 'inspect'] else 'docker')
    print(config.get(key, ''), end='')
elif name == 'find':
    if config.get('real_find'):
        # Map only this fixed test root into the owned temporary tree; execute
        # the real find parser/traversal with the owner's unchanged expression.
        args = [config['real_find_root'] + arg[len('/var/lib/docker'):]
                if arg == '/var/lib/docker' or arg.startswith('/var/lib/docker/') else arg
                for arg in sys.argv[1:]]
        os.execv('/usr/bin/find', ['/usr/bin/find', *args])
    print(config.get('crash_after_prune' if '-prune' in sys.argv else 'crash', ''), end='')
elif name == 'file':
    print(config.get('file_description', ''), end='')
elif name == 'realpath':
    assert sys.argv[1:3] == ['-e', '--']
    for path in sys.argv[sys.argv.index('--') + 1:]:
        print(config.get('realpaths', {}).get(path, path))
elif name == 'stat':
    assert sys.argv[1:3] == ['--format=%F', '--']
    for path in sys.argv[3:]:
        print(config.get('kinds', {}).get(path, 'directory'))
elif name == 'dotnet':
    time.sleep(config.get('startup_delay', 0))
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
    os.setsid()
    proc = base / 'proc' / str(os.getpid())
    proc.mkdir(parents=True)
    proc.joinpath('cmdline').write_bytes(b'dotnet\0testhost.dll\0')
    proc.joinpath('cgroup').write_text('0::' + config.get('work', '/owned/work') + '\n')
    base.joinpath('session.json').write_text(json.dumps({'pid':os.getpid(), 'sid':os.getsid(0)}))
    try:
        if '--diag' in sys.argv:
            diagnostic = pathlib.Path(sys.argv[sys.argv.index('--diag') + 1])
            with diagnostic.open('w') as stream:
                for chunk in config.get('diagnostic_chunks', []):
                    stream.write(chunk)
                    stream.flush()
                    time.sleep(0.02)
        for line in config.get('console', []):
            print(line, flush=True)
        time.sleep(config.get('delay', 0.2))
    finally:
        for item in proc.iterdir(): item.unlink()
        proc.rmdir()
else:
    raise SystemExit(93)
raise SystemExit(config.get('status', {}).get(name, 0))
'''


class OwnerControls(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='learnstack-ci-owner-control-')
        self.root = Path(self.temporary.name)
        scripts = self.root / 'scripts'
        scripts.mkdir()
        self.bin = self.root / 'fixture'
        self.bin.mkdir()
        (self.bin / 'proc').mkdir()
        (self.bin / 'cgroup').mkdir()
        (self.root / 'backend').mkdir()
        self.config = {}
        self.progresses = []
        self.configure()
        fixture = FIXTURE.replace('PYTHON', sys.executable, 1)
        replacements = {'PROC': str(self.bin / 'proc'), 'CGROUP': str(self.bin / 'cgroup')}
        for name in ('DOTNET', 'SUDO', 'SYSTEMD_RUN', 'SYSTEMCTL', 'DOCKER', 'GIT', 'FIND', 'FILE', 'REALPATH', 'STAT'):
            executable = self.bin / name.lower().replace('_', '-')
            executable.write_text(fixture)
            executable.chmod(0o700)
            replacements[name] = str(executable)
        original = SOURCE.read_bytes()
        copied = original
        changes = []
        for name, replacement in replacements.items():
            pattern = re.compile(rb'^' + name.encode() + rb' = [^\n]+$', re.MULTILINE)
            matches = list(pattern.finditer(copied))
            self.assertEqual(1, len(matches), name)
            old = matches[0].group()
            new = (name + ' = ' + repr(replacement)).encode()
            copied = copied.replace(old, new, 1)
            changes.append((old, new))
        recovered = copied
        for old, new in reversed(changes):
            recovered = recovered.replace(new, old, 1)
        self.assertEqual(original, recovered, 'Every non-seam byte must remain unchanged')
        self.assertEqual(hashlib.sha256(SOURCE.read_bytes()).digest(), hashlib.sha256(original).digest())
        for filename in ('ci-test-shards.py', 'ci-test-shards-plan.json'):
            (scripts / filename).write_bytes((ROOT / 'scripts' / filename).read_bytes())
        self.copy = scripts / SOURCE.name
        self.copy.write_bytes(copied)
        spec = importlib.util.spec_from_file_location('ci_owner_control', self.copy)
        self.owner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.owner)

    def tearDown(self):
        # Every fixture dotnet invocation is finite, even when its caller refuses
        # early. Wait for only those test-owned fixture processes to exit.
        end = time.monotonic() + 2
        while any((self.bin / 'proc').iterdir()) and time.monotonic() < end:
            time.sleep(0.02)
        self.assertEqual([], list((self.bin / 'proc').iterdir()))
        for progress in self.progresses:
            progress.close()
        self.temporary.cleanup()

    def configure(self, **values):
        self.config.update(values)
        (self.bin / 'fixture.json').write_text(json.dumps(self.config))

    def calls(self):
        path = self.bin / 'calls.jsonl'
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

    def progress(self, path, assembly, expected, stream):
        value = self.owner.ExecutionProgress(path, assembly, expected, stream)
        self.progresses.append(value)
        return value

    def refuses(self, reason, function, *args, **kwargs):
        with self.assertRaises(self.owner.Refusal) as caught:
            function(*args, **kwargs)
        self.assertEqual(reason, caught.exception.reason)

    def case(self, index, name='same truncated display···'):
        return {'assembly': 'learnstack.tests.integration', 'id': f'{index:08x}-1111-2222-3333-444444444444', 'name': name}

    def population(self):
        native = [self.case(1, self.owner.NATIVE_CLASSES[0] + '.Proof(row: 1)'),
                  self.case(2, self.owner.NATIVE_CLASSES[1] + '.Proof(row: 2)')]
        ordinary = [self.case(3), self.case(4)]
        return {'all': native + ordinary, 'native': native, 'ordinary': ordinary,
                'assemblies': ['LearnStack.Tests.Integration']}

    def execution_result(self, case, outcome=1):
        return {'TestCase': {'Id': case['id'], 'DisplayName': case['name'],
                             'Source': '/private/LearnStack.Tests.Integration.dll'},
                'Outcome': outcome, 'Duration': '00:00:00.0010000',
                'ErrorMessage': 'PRIVATE-CANARY provider detail', 'Messages': ['PRIVATE-CANARY']}

    def execution_message(self, kind, payload):
        return (self.owner.ExecutionProgress.marker.decode() + json.dumps(
            {'MessageType': kind, 'Payload': payload}) + '\n')

    def execution_event(self, cases):
        return self.execution_message('TestExecution.StatsChange',
                                      {'NewTestResults': [self.execution_result(case) for case in cases]})

    def execution_complete(self, cases, *, tail=()):
        return self.execution_message('TestExecution.Completed', {
            'TestRunCompleteArgs': {'IsCanceled': False, 'IsAborted': False, 'Error': None,
                                    'TestRunStatistics': {'ExecutedTests': len(cases),
                                                          'Stats': {'Passed': len(cases)}}},
            'LastRunTests': {'NewTestResults': [self.execution_result(case) for case in tail]}})

    def trx(self, output, cases, *, outcome='Passed', summary='Completed', raw_canary=False):
        output.mkdir(parents=True, exist_ok=True)
        tag = lambda name: '{' + self.owner.NS['t'] + '}' + name
        root = ET.Element(tag('TestRun'))
        results = ET.SubElement(root, tag('Results'))
        definitions = ET.SubElement(root, tag('TestDefinitions'))
        for case in cases:
            row = ET.SubElement(results, tag('UnitTestResult'), testId=case['id'], testName=case['name'],
                                outcome=outcome, duration='00:00:00.0010000')
            if raw_canary:
                ET.SubElement(row, tag('Output')).text = 'PRIVATE-CANARY collector --credential=secret'
            ET.SubElement(definitions, tag('UnitTest'), id=case['id'], storage=case['assembly'] + '.dll')
        summary = ET.SubElement(root, tag('ResultSummary'), outcome=summary)
        ET.SubElement(summary, tag('Counters'), total=str(len(cases)),
                      executed=str(len(cases) if outcome != 'NotExecuted' else 0),
                      passed=str(len(cases) if outcome == 'Passed' else 0),
                      failed=str(len(cases) if outcome == 'Failed' else 0),
                      notExecuted=str(len(cases) if outcome == 'NotExecuted' else 0))
        path = output / 'owned.trx'
        ET.ElementTree(root).write(path, encoding='utf-8')
        return path

    def test_cli_is_closed_and_budget_query_is_read_only(self):
        for args in ([], ['native'], ['ordinary'], ['aggregate'], ['native', '--command', '/bin/true'],
                     ['--help'], ['_native-leaf', '/tmp'], ['arbitrary']):
            result = subprocess.run([sys.executable, self.copy, *args], capture_output=True, text=True, check=False)
            self.assertEqual(1, result.returncode)
            self.assertEqual('mode_refused', json.loads(result.stdout)['error'])
        result = subprocess.run([sys.executable, self.copy, 'upload-budget'], capture_output=True, text=True, check=False)
        self.assertEqual(0, result.returncode)
        self.assertEqual('minutes=1\n', result.stdout)
        self.assertEqual([], self.calls())

    def test_unqualified_leaf_never_invokes_host_executables(self):
        result = subprocess.run([sys.executable, self.copy, '_native-shard-leaf'], env={}, capture_output=True, text=True)
        self.assertEqual(1, result.returncode)
        self.assertEqual('unsupported_context', json.loads(result.stdout)['error'])
        self.assertEqual([], self.calls())

    def test_identity_accepts_only_absent_service_shapes_for_both_execution_jobs(self):
        old_cwd = Path.cwd()
        try:
            os.chdir(self.root)
            for mode, job, label in (('native-shard', 'backend-native', 'ubuntu-24.04'),
                                     ('ordinary-0', 'backend-integration-tests', 'ubuntu-24.04')):
                env = {'GITHUB_ACTIONS': 'true', 'GITHUB_JOB': job,
                       'RUNNER_OS': 'Linux', 'RUNNER_ARCH': 'X64',
                       'LEARNSTACK_NATIVE_RUNNER_CONTEXT': 'github-hosted',
                       'LEARNSTACK_NATIVE_RUNNER_OS': 'Linux',
                       'LEARNSTACK_NATIVE_RUNNER_ARCH': 'X64',
                       'LEARNSTACK_NATIVE_RUNNER_LABEL': label,
                       'LEARNSTACK_NATIVE_JOB_CONTAINER': 'null', 'GITHUB_EVENT_NAME': 'push',
                       'LEARNSTACK_CI_HEAD_SHA': 'a' * 40}
                for name, value in {
                    'RUN_ID': '42', 'RUN_ATTEMPT': '1', 'JOB': job,
                    'SHA': 'a' * 40, 'REPOSITORY': 'HodeTech/LearnStack',
                    'WORKFLOW_REF': 'HodeTech/LearnStack/.github/workflows/ci.yml@refs/heads/development',
                    'WORKFLOW_SHA': 'a' * 40, 'WORKSPACE': str(self.root),
                }.items():
                    env['GITHUB_' + name] = value
                    env['LEARNSTACK_NATIVE_' + ('ATTEMPT' if name == 'RUN_ATTEMPT' else name)] = value
                with mock.patch.object(self.owner.sys, 'platform', 'linux'), mock.patch.dict(os.environ, env, clear=True):
                    for services in ('null', '{}'):
                        with self.subTest(mode=mode, services=services):
                            os.environ['LEARNSTACK_NATIVE_JOB_SERVICES'] = services
                            self.assertEqual('42', self.owner.identity(mode)['runId'])
                    calls_before_dirty = len(self.calls())
                    for services in ('[]', '{"db":{}}', '"foreign"', 'broken', None):
                        with self.subTest(mode=mode, services=services):
                            if services is None:
                                os.environ.pop('LEARNSTACK_NATIVE_JOB_SERVICES', None)
                            else:
                                os.environ['LEARNSTACK_NATIVE_JOB_SERVICES'] = services
                            self.refuses('unsupported_job_topology', self.owner.identity, mode)
                            # Identity reads the checkout before job topology.
                            self.assertEqual(calls_before_dirty + 1, len(self.calls()))
                            self.assertEqual('git', self.calls()[-1]['name'])
                            calls_before_dirty += 1
        finally:
            os.chdir(old_cwd)

    def test_composed_native_prevalidation_rejects_actual_unknown_collector_before_privilege(self):
        spec = importlib.util.spec_from_file_location(
            'provisioning_control_fixture', ROOT / 'scripts/test-ci-native-provisioning.py')
        controls = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(controls)
        fixture = controls.Fixture(self.root / 'prevalidation')
        launcher = fixture.repo / 'scripts' / self.copy.name
        launcher.write_bytes(self.copy.read_bytes())
        fixture.runner.write_text('''import importlib.util
import json
import time
from pathlib import Path
def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    owner = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(owner)
    return owner
launcher = load('launcher', LAUNCHER)
provisioner = load('provisioner', PROVISIONER)
try:
    launcher.prevalidate_native(provisioner, time.monotonic() + 10)
    Path(AUTHORIZED).touch()
    print(json.dumps({'ok': True}))
except launcher.Refusal as failure:
    print(json.dumps({'ok': False, 'reason': failure.reason}))
'''.replace('LAUNCHER', repr(str(launcher)))
           .replace('PROVISIONER', repr(str(fixture.owner)))
           .replace('AUTHORIZED', repr(str(fixture.authorized))))
        fixture.pattern.write_text('|/unknown/collector PRIVATE-COLLECTOR-CANARY\n')
        self.assertEqual({'ok': False, 'reason': 'unsupported_collector'}, fixture.run())
        self.assertFalse(fixture.authorized.exists())
        self.assertEqual([], fixture.commands('sudo'))
        fixture.pattern.write_text('core.%p\n')
        self.assertEqual({'ok': True}, fixture.run())
        self.assertTrue(fixture.authorized.exists())
        self.assertEqual([], fixture.commands('sudo'))

    def test_actual_subprocess_failure_and_timeout_disclose_no_provider_text(self):
        self.configure(status={'docker': 7}, docker='PRIVATE-CANARY')
        self.refuses('failed', self.owner.command, [self.owner.DOCKER, 'ps'], time.monotonic() + 2, 'failed')
        self.configure(status={}, delay=0.3)
        self.refuses('timed', self.owner.command, [self.owner.DOTNET], time.monotonic() + 0.05, 'timed')
        # subprocess.run really reaps its directly timed-out child; fixture's
        # finally cannot run under SIGKILL, so remove only its synthetic proc data.
        for directory in (self.bin / 'proc').iterdir():
            for path in directory.iterdir(): path.unlink()
            directory.rmdir()

    def test_real_subprocess_and_setsid_keep_observed_membership_and_completed_duration(self):
        case = self.case(1)
        diagnostic = self.root / 'execution.diag'
        self.configure(console=['raw PRIVATE-CANARY', '  Passed ' + case['name'] + ' [< 1 ms]'],
                       diagnostic_chunks=[self.execution_event([case]), self.execution_complete([case])])
        evidence, console = io.StringIO(), io.StringIO()
        progress = self.progress(diagnostic, 'LearnStack.Tests.Integration', [case], evidence)
        with contextlib.redirect_stdout(console):
            status = self.owner.dotnet(['test', '--diag', str(diagnostic)], time.monotonic() + 3,
                                       '/owned/work', lambda _value: None, progress)
        self.assertEqual(0, status)
        self.assertEqual({**case, 'outcome': 'Passed', 'duration': '00:00:00.0010000'},
                         json.loads(evidence.getvalue()))
        self.assertNotIn('PRIVATE-CANARY', console.getvalue())
        session = json.loads((self.bin / 'session.json').read_text())
        self.assertEqual(session['pid'], session['sid'], 'The actual subprocess changed session')

    def test_actual_testhost_outside_the_work_cgroup_is_refused(self):
        self.configure(work='/unrelated/session')
        self.refuses('testhost_outside_owned_subtree', self.owner.dotnet, ['test'],
                     time.monotonic() + 3, '/owned/work', lambda _line: None)

    def test_interrupted_protocol_keeps_actual_colliding_id_before_partial_json_without_trx(self):
        cases = self.population()['ordinary']
        output = self.root / 'interrupted'
        output.mkdir()
        diagnostic = self.root / 'interrupted.diag'
        partial = self.execution_event(cases[1:])[:100]
        self.configure(diagnostic_chunks=[self.execution_event(cases[:1]), partial], delay=10, startup_delay=0.25)
        console = io.StringIO()
        with (output / 'completed-cases.jsonl').open('w') as stream:
            progress = self.progress(diagnostic, 'LearnStack.Tests.Integration', cases, stream)
            observe = progress.poll
            def interrupt_after_actual_admission(*args, **kwargs):
                observe(*args, **kwargs)
                if progress.seen and progress.pending:
                    self.owner.CANCELLED = True
            try:
                with contextlib.redirect_stdout(console), mock.patch.object(progress, 'poll', side_effect=interrupt_after_actual_admission):
                    self.refuses('cancelled', self.owner.dotnet, ['test', '--diag', str(diagnostic)],
                                 time.monotonic() + 5, '/owned/work', lambda _line: None, progress)
            finally:
                self.owner.CANCELLED = False
            self.assertTrue(progress.pending, 'Interruption follows actual partial-event observation')
        rows = [json.loads(line) for line in (output / 'completed-cases.jsonl').read_text().splitlines()]
        self.assertEqual([{**cases[0], 'outcome': 'Passed', 'duration': '00:00:00.0010000'}], rows)
        self.assertEqual(cases[0]['name'], cases[1]['name'])
        self.assertIn(cases[0]['id'], console.getvalue())
        self.assertNotIn(cases[1]['id'], console.getvalue())
        self.assertNotIn('PRIVATE-CANARY', console.getvalue())
        self.refuses('execution_incomplete', progress.poll, final=True)
        self.refuses('trx_missing', self.owner.validate_results, output, cases)

    def test_failed_protocol_retains_only_closed_source_codes_and_bounded_line_numbers(self):
        cases = [self.case(1), self.case(2)]
        stream = io.StringIO()
        progress = self.progress(self.root / 'failed-location.diag', 'LearnStack.Tests.Integration', cases, stream)
        first = self.execution_result(cases[0]); first['Outcome'] = 2
        first['ErrorMessage'] = 'PRIVATE-CANARY provider text'
        first['ErrorStackTrace'] = ('PRIVATE-CANARY /private/path/Provider.cs:line 55\n'
            '/private/path/BackgroundJobRuntimeFacadeTests.ControlFailures.cs:line 74\n'
            '/private/path/BackgroundJobRuntimeFacadeTests.ControlFailures.cs:line 74\n'
            r'C:\private\BackgroundJobRuntimeFacadeTests.CapacityNoStart.cs:line 109' + '\n'
            '/private/path/BackgroundJobRuntimeFacadeTests.ControlFailures.cs:line 10001\n')
        second = self.execution_result(cases[1]); second['Outcome'] = 2
        second['ErrorStackTrace'] = {'unexpected': 'PRIVATE-CANARY'}
        with contextlib.redirect_stdout(io.StringIO()):
            progress.results({'NewTestResults': [first, second]})
        self.assertEqual([{'assembly': cases[0]['assembly'], 'id': cases[0]['id'], 'locations': [
            {'source': 'facade_capacity_no_start', 'line': 109}, {'source': 'facade_control_failure', 'line': 74}]},
            {'assembly': cases[1]['assembly'], 'id': cases[1]['id'], 'locations': []}], progress.failure_locations)
        self.assertNotIn('PRIVATE-CANARY', json.dumps(progress.failure_locations))
        self.assertNotIn('private', json.dumps(progress.failure_locations))
        rows = [json.loads(line) for line in stream.getvalue().splitlines()]
        self.assertTrue(all(set(row) == {'assembly', 'id', 'name', 'outcome', 'duration'}
                            and row['outcome'] == 'Failed' for row in rows))
        expected = {path.name for path in (ROOT / 'backend/tests/LearnStack.Tests.Integration/Database').glob(
            'BackgroundJobRuntimeFacadeTests*.cs')} | {'PublicAdmissionRenderingTests.cs'}
        self.assertEqual(expected, set(self.owner.FAILURE_SOURCES))
        self.assertEqual(len(expected), len(set(self.owner.FAILURE_SOURCES.values())))
        for filename, code in self.owner.FAILURE_SOURCES.items():
            self.assertTrue((ROOT / 'backend/tests/LearnStack.Tests.Integration/Database' / filename).is_file())
            progress.failure_locations.clear()
            item = self.execution_result(cases[0]); item['Outcome'] = 2
            item['ErrorStackTrace'] = '/private/path/' + filename + ':line 123'
            progress.seen.clear()
            with contextlib.redirect_stdout(io.StringIO()):
                progress.results({'NewTestResults': [item]})
            self.assertEqual([{'source': code, 'line': 123}], progress.failure_locations[0]['locations'])

    def test_public_failure_details_export_only_registered_test_owned_stages(self):
        case = self.case(1)
        case['name'] = ('LearnStack.Tests.Integration.Database.PublicAdmissionRenderingTests.'
                        'Native_admission_preserves_wire_failures_request_lifetimes_and_browser_fallback')
        parse = self.owner.closed_failure_details
        for stage in self.owner.PUBLIC_FAILURE_STAGES:
            text = 'PRIVATE-CANARY Public admission fixture failed during ' + stage + '.'
            self.assertEqual({'stage': stage}, parse(case, {'ErrorMessage': text}))
        for detail in self.owner.PUBLIC_FAILURE_DETAILS:
            text = 'Public admission fixture failed during browser 429 ' + detail + '.'
            self.assertEqual({'stage': 'browser 429', 'detail': detail}, parse(case, {'ErrorMessage': text}))
        for text in (None, {}, 'Public admission fixture failed during PRIVATE-CANARY.',
                     'Public admission fixture failed during browser 429 PRIVATE-CANARY.',
                     'x' * 65536 + 'Public admission fixture failed during production build.'):
            self.assertIsNone(parse(case, {'ErrorMessage': text}))
        self.assertIsNone(parse(self.case(2), {'ErrorMessage': 'Public admission fixture failed during production build.'}))
        for outcome in (1, 2):
            stream = io.StringIO()
            progress = self.progress(self.root / ('public-' + str(outcome) + '.diag'), 'LearnStack.Tests.Integration', [case], stream)
            item = self.execution_result(case); item['Outcome'] = outcome
            item['ErrorMessage'] = 'Public admission fixture failed during production build.'
            with contextlib.redirect_stdout(io.StringIO()):
                progress.results({'NewTestResults': [item]})
            self.assertEqual(bool(progress.failure_locations), outcome == 2)
            self.assertNotIn('production build', stream.getvalue())

    def test_keycloak_startup_vocabulary_matches_the_existing_fixture_source(self):
        directory = ROOT / 'backend/tests/LearnStack.Tests.Integration/TenancyIdentity'
        source = (directory / 'KeycloakFixture.cs').read_text()
        initialize = source.split('public async Task InitializeAsync()', 1)[1].split(
            'internal async Task<KeycloakAdminClient>', 1)[0]
        self.assertEqual(set(re.findall(r'\bstage = "([a-z_]+)";', initialize)),
                         self.owner.KEYCLOAK_FAILURE_STAGES)
        self.assertIn('var reason = StartupFailureReason(exception);', initialize)
        reasons = source.split('internal static string StartupFailureReason(Exception exception)', 1)[1].split('};', 1)[0]
        self.assertEqual(set(re.findall(r'=> "([a-z_]+)"', reasons)), self.owner.KEYCLOAK_FAILURE_REASONS)
        self.assertEqual(set(re.findall(r'\bcleanup = "([a-z_]+)";', initialize)),
                         self.owner.KEYCLOAK_FAILURE_CLEANUP)
        self.assertIn('"Keycloak fixture startup failed at " + stage + "/" + reason', initialize)
        self.assertIn('"; owned cleanup " + cleanup + "."', initialize)
        self.assertEqual({'TenantRealmApiTests', 'TenantRealmIssuanceTests', 'TenantRealmProfileProtectionTests',
                          'TenantRealmReconciliationTests', 'TenantRealmSelfServiceTests'},
                         self.owner.KEYCLOAK_FAILURE_CLASSES)
        for name in self.owner.KEYCLOAK_FAILURE_CLASSES:
            test = (directory / (name + '.cs')).read_text()
            self.assertIn('namespace LearnStack.Tests.Integration.TenancyIdentity;', test)
            self.assertIn('class ' + name, test)
            self.assertIn('KeycloakFixture', test)

    def test_keycloak_startup_details_require_named_cases_and_one_complete_bounded_sentence(self):
        parse = self.owner.closed_failure_details
        prefix = 'LearnStack.Tests.Integration.TenancyIdentity.'
        sentence = ('Keycloak fixture startup failed at provider_start/unsupported_operation; '
                    'owned cleanup completed.')
        case = self.case(1, prefix + 'TenantRealmIssuanceTests.Proof(row: True)')
        for name in self.owner.KEYCLOAK_FAILURE_CLASSES:
            named = {**case, 'name': prefix + name + '.Proof(row: True)'}
            for stage in self.owner.KEYCLOAK_FAILURE_STAGES:
                for reason in self.owner.KEYCLOAK_FAILURE_REASONS:
                    for cleanup in self.owner.KEYCLOAK_FAILURE_CLEANUP:
                        message = ('System.InvalidOperationException : Keycloak fixture startup failed at '
                                   + stage + '/' + reason + '; owned cleanup ' + cleanup + '.\nPRIVATE-CANARY')
                        self.assertEqual({'stage': stage, 'reason': reason, 'cleanup': cleanup},
                                         parse(named, {'ErrorMessage': message}))
        boundary = sentence + 'x' * (65536 - len(sentence))
        self.assertEqual({'stage': 'provider_start', 'reason': 'unsupported_operation', 'cleanup': 'completed'},
                         parse(case, {'ErrorMessage': boundary}))
        for message in (None, {}, sentence.replace('provider_start/', 'PRIVATE-CANARY/'),
                        sentence.replace('/unsupported_operation', '/PRIVATE-CANARY'),
                        sentence.replace('cleanup completed', 'cleanup PRIVATE-CANARY'),
                        sentence.replace('provider_start/', 'provider_start\n/'),
                        sentence.replace('; owned cleanup ', '; owned cleanup\x00 '),
                        sentence[:-1], sentence + '\n' + sentence,
                        sentence + '\n' + sentence.replace('provider_start/', 'unknown/'),
                        'The following constructor parameters did not have matching fixture data: PRIVATE-CANARY',
                        boundary + 'x', 'x' * 65536 + sentence):
            with self.subTest(message_type=type(message).__name__):
                self.assertIsNone(parse(case, {'ErrorMessage': message}))
        for changed in ({**case, 'assembly': 'learnstack.tests.unit'},
                        {**case, 'name': prefix + 'OtherTests.Proof'},
                        {**case, 'name': prefix + 'TenantRealmIssuanceTestsOther.Proof'},
                        {**case, 'name': 'LearnStack.Tests.Integration.Database.TenantRealmIssuanceTests.Proof'}):
            self.assertIsNone(parse(changed, {'ErrorMessage': sentence}))
        self.assertIsNone(parse(case, {'ErrorMessage': 'unrelated failure', 'ErrorStackTrace': sentence}))

    def test_keycloak_fixture_results_keep_only_closed_measurements_and_truthful_sanitized_failures(self):
        sentence = ('Keycloak fixture startup failed at provider_start/unsupported_operation; '
                    'owned cleanup completed.')
        messages = [sentence, sentence, sentence.replace('/unsupported_operation', '/PRIVATE-CANARY'),
                    sentence + '\n' + sentence, sentence + 'x' * 65536,
                    'The following constructor parameters did not have matching fixture data: PRIVATE-CANARY']
        cases = [self.case(index + 1, 'LearnStack.Tests.Integration.TenancyIdentity.'
                           'TenantRealmIssuanceTests.Proof(row: ' + str(index) + ')')
                 for index in range(len(messages))]
        stream, console = io.StringIO(), io.StringIO()
        progress = self.progress(self.root / 'keycloak.diag', 'LearnStack.Tests.Integration', cases, stream)
        results = []
        for index, (case, message) in enumerate(zip(cases, messages)):
            result = self.execution_result(case, outcome=1 if index == 1 else 2)
            result['ErrorMessage'] = 'System.InvalidOperationException : ' + message + '\nPRIVATE-CANARY'
            result['ErrorStackTrace'] = '/private/PRIVATE-CANARY/KeycloakFixture.cs:line 141'
            results.append(result)
        with contextlib.redirect_stdout(console):
            progress.results({'NewTestResults': results})
        expected = {'assembly': cases[0]['assembly'], 'id': cases[0]['id'], 'locations': [],
                    'details': {'stage': 'provider_start', 'reason': 'unsupported_operation', 'cleanup': 'completed'}}
        self.assertEqual([expected] + [{'assembly': case['assembly'], 'id': case['id'], 'locations': []}
                                     for case in cases[2:]], progress.failure_locations)
        measurements = self.root / 'measurements.json'
        self.owner.write_json(measurements, {'projects': [{'failureLocations': progress.failure_locations}]})
        self.assertEqual(expected, json.loads(measurements.read_text())['projects'][0]['failureLocations'][0])
        rows = [json.loads(line) for line in stream.getvalue().splitlines()]
        self.assertEqual(console.getvalue(), stream.getvalue())
        self.assertEqual([{**case, 'outcome': 'Passed' if index == 1 else 'Failed',
                           'duration': '00:00:00.0010000'} for index, case in enumerate(cases)], rows)
        self.assertFalse(progress.completed, 'Failure details never supply a completion witness')
        for text in (stream.getvalue(), console.getvalue(), measurements.read_text()):
            for private in ('PRIVATE-CANARY', '/private/', 'System.InvalidOperationException', sentence):
                self.assertNotIn(private, text)
        self.assertNotIn('provider_start', stream.getvalue())
        output = self.root / 'output'
        path = self.trx(output / 'results', cases[:1], outcome='Failed', raw_canary=True)
        tree = ET.parse(path)
        raw_output = tree.find('t:Results/t:UnitTestResult/t:Output', self.owner.NS)
        tag = lambda name: '{' + self.owner.NS['t'] + '}' + name
        ET.SubElement(ET.SubElement(raw_output, tag('ErrorInfo')), tag('Message')).text = sentence
        tree.write(path, encoding='utf-8')
        retained = self.owner.trx_cases(path, sanitize=True)
        self.assertEqual('Failed', retained[0]['outcome'])
        self.assertEqual([], ET.parse(path).findall('.//t:Output', self.owner.NS))
        self.assertNotIn('PRIVATE-CANARY', path.read_text())
        self.assertNotIn('provider_start', path.read_text())
        self.refuses('results_incomplete', self.owner.validate_results, output, cases[:1])

    def test_outage_failure_details_keep_only_consistent_bounded_timing_and_closed_pool_facts(self):
        case = self.case(1)
        case['name'] = ('LearnStack.Tests.Integration.Database.BackgroundJobRuntimeFacadeTests.'
            'Actual_common_database_outage_drains_all_pools_and_recovery_keeps_the_old_held_pool_closed_despite_its_spare_slot')
        parse = self.owner.closed_failure_details
        prefix = ('Native post-pause heartbeat premise failed;elapsed=00:01:30.1234500;'
                  'pauseAt=00:00:05;sincePause=00:01:25.1234500;runtime=PRIVATE-CANARY;pools=')
        snapshot = ('HangfireGenerationSnapshot { HandlePublished = True, Captured = True, '
            'WaitEntered = True, NormalChildrenJoined = False, OuterJoined = False, CleanupComplete = False, '
            'Draining = False, HeartbeatFault = False, Reason = None }')
        heartbeat = ';heartbeat=HangfireHeartbeatDiagnostic { Entries = 1, Attempts = 2, Phase = Invoking }'
        message = prefix + ';'.join(pool + ':generation=' + snapshot + heartbeat + ';work=PRIVATE-CANARY;owner=PRIVATE-CANARY'
                                   for pool in ('control', 'external', 'transactional'))
        value = parse(case, {'ErrorMessage': message})
        self.assertEqual(90123, value['elapsedMilliseconds'])
        self.assertEqual(5000, value['pauseMilliseconds'])
        self.assertEqual(85123, value['sincePauseMilliseconds'])
        self.assertEqual(['control', 'external', 'transactional'], [item['pool'] for item in value['pools']])
        self.assertTrue(all(item['Captured'] is True and item['HeartbeatFault'] is False
                            and item['reason'] == 'None' for item in value['pools']))
        self.assertTrue(all(item['heartbeat'] == {'entries': 1, 'attempts': 2, 'phase': 'Invoking'}
                            for item in value['pools']))
        for phase in ('Created', 'Acquiring', 'PhysicalOpening', 'Disposing', 'Returned'):
            changed = parse(case, {'ErrorMessage': message.replace('Phase = Invoking', 'Phase = ' + phase)})
            self.assertTrue(all(item['heartbeat']['phase'] == phase for item in changed['pools']))
        for changed in (message.replace(heartbeat, ''), message.replace('Entries = 1', 'Entries = -1'),
                        message.replace('Attempts = 2', 'Attempts = 10001'),
                        message.replace('Entries = 1', 'Entries = 100000'),
                        message.replace('Phase = Invoking', 'Phase = PRIVATE-CANARY')):
            unknown = parse(case, {'ErrorMessage': changed})
            self.assertEqual(3, len(unknown['pools']))
            self.assertTrue(all('heartbeat' not in item for item in unknown['pools']))
        self.assertNotIn('PRIVATE-CANARY', json.dumps(value))
        for changed in (message.replace('00:01:30.1234500', '00:03:30.1234500'),
                        message.replace('sincePause=00:01:25.1234500', 'sincePause=00:01:20.1234500'),
                        message.replace('elapsed=00:01:30.1234500', 'elapsed=-00:01:30.1234500')):
            self.assertIsNone(parse(case, {'ErrorMessage': changed}))
        unknown = parse(case, {'ErrorMessage': message.replace('Reason = None', 'Reason = PRIVATE-CANARY')})
        self.assertEqual([], unknown['pools'])
        nonwire = parse(case, {'ErrorMessage': message.replace('Reason = None }',
            'Reason = None, MetadataHeartbeatTimestamp = 0 }')})
        self.assertEqual([], nonwire['pools'], 'Internal record members are not part of the actual ToString wire shape')
        self.assertIsNone(parse(self.case(2), {'ErrorMessage': message}))

    def test_execution_protocol_requires_unique_expected_ids_and_finite_non_skip_outcomes(self):
        case = self.case(1)
        for mutation in ('missing', 'wrong', 'duplicate', 'skip', 'none', 'notfound', 'unknown', 'boolean'):
            with self.subTest(mutation=mutation):
                stream = io.StringIO()
                progress = self.progress(self.root / ('unused-' + mutation + '.diag'),
                                         'LearnStack.Tests.Integration', [case], stream)
                result = self.execution_result(case)
                if mutation == 'missing':
                    result['TestCase'].pop('Id')
                elif mutation == 'wrong':
                    result['TestCase']['Id'] = self.case(99)['id']
                elif mutation != 'duplicate':
                    result['Outcome'] = {'skip': 3, 'none': 0, 'notfound': 4,
                                         'unknown': 9, 'boolean': True}[mutation]
                payload = {'NewTestResults': [result, result] if mutation == 'duplicate' else [result]}
                with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(self.owner.Refusal):
                    progress.line(self.execution_message('TestExecution.StatsChange', payload).encode())
                self.assertNotIn('PRIVATE-CANARY', stream.getvalue())

    def test_execution_completion_tail_uses_actual_ids_and_refuses_missing_results(self):
        cases = self.population()['ordinary']
        diagnostic = self.root / 'tail.diag'
        stream = io.StringIO()
        progress = self.progress(diagnostic, 'LearnStack.Tests.Integration', cases, stream)
        diagnostic.write_text(self.execution_complete(cases, tail=cases))
        with contextlib.redirect_stdout(io.StringIO()):
            progress.poll(final=True)
        self.assertEqual([case['id'] for case in cases], [json.loads(line)['id'] for line in stream.getvalue().splitlines()])
        incomplete = self.progress(self.root / 'missing.diag',
                                   'LearnStack.Tests.Integration', cases, io.StringIO())
        with contextlib.redirect_stdout(io.StringIO()):
            self.refuses('execution_incomplete', incomplete.line,
                         self.execution_complete(cases[:1], tail=cases[:1]).encode())

    def test_rotated_execution_preserves_old_tail_partial_line_and_exact_case_ids(self):
        cases = self.population()['ordinary']
        path = self.root / 'rotate.diag'
        stream = io.StringIO()
        progress = self.progress(path, 'LearnStack.Tests.Integration', cases, stream)
        self.assertEqual(0o600, path.stat().st_mode & 0o777)
        second = self.execution_event(cases[1:])
        path.write_text(self.execution_event(cases[:1]) + second[:100])
        with contextlib.redirect_stdout(io.StringIO()):
            progress.poll()
            path.rename(progress.backup)
            with progress.backup.open('a') as old:
                old.write(second[100:150])
            path.write_text(second[150:] + self.execution_complete(cases))
            progress.poll(final=True)
        self.assertEqual([case['id'] for case in cases], [json.loads(x)['id'] for x in stream.getvalue().splitlines()])
        self.assertEqual(1, progress.generations)
        self.assertNotIn('PRIVATE-CANARY', stream.getvalue())
        progress.close()
        self.assertTrue(progress.reader.closed)

    def test_rotation_gap_is_deferred_while_live_but_never_counts_as_final_completion(self):
        case = self.case(1)
        path = self.root / 'gap.diag'
        progress = self.progress(path, 'LearnStack.Tests.Integration', [case], io.StringIO())
        path.write_text(self.execution_event([case]))
        with contextlib.redirect_stdout(io.StringIO()):
            progress.poll()
            path.rename(progress.backup)
            progress.poll()
            self.assertFalse(progress.completed)
            path.write_text(self.execution_complete([case]))
            progress.poll(final=True)
        final = self.progress(self.root / 'final-gap.diag', 'LearnStack.Tests.Integration', [case], io.StringIO())
        final.path.rename(final.backup)
        self.refuses('execution_incomplete', final.poll, final=True)

    def test_unknown_or_changed_diagnostic_generations_refuse_instead_of_skipping(self):
        case = self.case(1)
        for mutation in ('double', 'missing_backup', 'wrong_backup', 'symlink', 'truncate'):
            path = self.root / (mutation + '.diag')
            progress = self.progress(path, 'LearnStack.Tests.Integration', [case], io.StringIO())
            path.write_text(self.execution_event([case]))
            with contextlib.redirect_stdout(io.StringIO()): progress.poll()
            if mutation == 'truncate':
                path.write_text('')
            else:
                path.rename(progress.backup)
                path.write_text('')
                if mutation == 'double':
                    path.replace(progress.backup)
                    path.write_text('')
                elif mutation == 'missing_backup':
                    progress.backup.rename(progress.backup.with_suffix('.unknown'))
                elif mutation == 'wrong_backup':
                    progress.backup.unlink()
                    progress.backup.write_text('wrong generation')
                elif mutation == 'symlink':
                    path.unlink()
                    path.symlink_to(progress.backup)
            self.refuses('execution_diagnostic_changed', progress.poll)
        for preexisting in ('current', 'backup'):
            path = self.root / (preexisting + '-preexisting.diag')
            target = path if preexisting == 'current' else path.with_name(path.stem + '.bak' + path.suffix)
            target.touch()
            self.refuses('execution_diagnostic_changed', self.owner.ExecutionProgress, path,
                         'LearnStack.Tests.Integration', [case], io.StringIO())

    def test_rotation_reopen_race_and_cumulative_bounds_stay_red(self):
        case = self.case(1)
        path = self.root / 'race.diag'
        progress = self.progress(path, 'LearnStack.Tests.Integration', [case], io.StringIO())
        path.rename(progress.backup)
        path.write_text('')
        original = os.open
        opened = []
        def racing_open(candidate, flags, *args):
            fd = original(candidate, flags, *args)
            if candidate == path:
                opened.append(fd)
                path.replace(progress.backup)
                path.write_text('')
            return fd
        with mock.patch.object(self.owner.os, 'open', side_effect=racing_open):
            self.refuses('execution_diagnostic_changed', progress.poll)
        self.assertEqual(1, len(opened))
        with self.assertRaises(OSError): os.fstat(opened[0])
        bound = self.progress(self.root / 'bytes.diag', 'LearnStack.Tests.Integration', [case], io.StringIO())
        with mock.patch.object(self.owner, 'MAX_BYTES', 2048):
            bound.path.write_bytes(b'x' * 1400 + b'\n')
            bound.poll()
            bound.path.rename(bound.backup)
            bound.path.write_bytes(b'x' * 1400 + b'\n')
            self.refuses('execution_diagnostic_bound', bound.poll)
        repeated = self.progress(self.root / 'repeated.diag', 'LearnStack.Tests.Integration', [case], io.StringIO())
        for _ in range(16):
            repeated.path.replace(repeated.backup)
            repeated.path.touch()
            repeated.poll()
        repeated.path.replace(repeated.backup)
        repeated.path.touch()
        self.refuses('execution_diagnostic_bound', repeated.poll)

    def test_workload_closes_stream_descriptor_when_actual_launcher_refuses(self):
        private, output = self.root / 'private-workload', self.root / 'output-workload'
        private.mkdir(); output.mkdir()
        spec = importlib.util.spec_from_file_location('ci_stream_fixtures', ROOT / 'scripts/test-ci-test-shards.py')
        fixtures = importlib.util.module_from_spec(spec); spec.loader.exec_module(fixtures)
        fixture = fixtures.ShardControls(); fixture.plan = self.owner.shard_owner().load_plan()
        manifest = fixture.manifest()
        expected = self.owner.shard_owner().assign(manifest, fixture.plan, '1')['owners']['ordinary-0']
        captured = []
        def refused(*args, **kwargs):
            captured.append(args[-1])
            raise self.owner.Refusal('launcher_join_failed')
        with mock.patch.object(self.owner, 'cgroup_path', return_value='/owned/work'), \
             mock.patch.object(self.owner, 'runtime_toolchain', return_value={}), \
             mock.patch.object(self.owner, 'discover_selectors', return_value=manifest), \
             mock.patch.object(self.owner, 'selected_discovery', return_value=expected), \
             mock.patch.object(self.owner, 'test_projects', return_value=[('fixture', 'LearnStack.Tests.Integration')]), \
             mock.patch.object(self.owner, 'dotnet', side_effect=refused):
            self.refuses('launcher_join_failed', self.owner.workload, 'ordinary-0', {'runAttempt': '1'}, private, output,
                         time.monotonic() + 3, '/owned/work')
        self.assertEqual(1, len(captured))
        self.assertTrue(captured[0].reader.closed)

    def test_populated_and_missing_cgroup_never_count_as_empty(self):
        group = Path(self.owner.CGROUP) / 'work'
        group.mkdir()
        self.refuses('input_unreadable', self.owner.populated, group)
        (group / 'cgroup.events').write_text('populated 1\nfrozen 0\n')
        self.assertTrue(self.owner.populated(group))
        (group / 'cgroup.events').write_text('populated 0\nfrozen 0\n')
        self.assertFalse(self.owner.populated(group))
        (group / 'cgroup.events').write_text('frozen 0\n')
        self.refuses('cgroup_events_invalid', self.owner.populated, group)

    def test_actual_worker_join_precedes_existing_empty_subtree_witness(self):
        group = Path(self.owner.CGROUP) / 'work'
        group.mkdir()
        (group / 'cgroup.events').write_text('populated 1\n')
        (group / 'cgroup.kill').write_text('')
        pid = os.fork()
        if pid == 0:
            os.setsid()
            time.sleep(0.04)
            (group / 'cgroup.events').write_text('populated 0\n')
            os._exit(0)
        status, forced = self.owner.join_workload(pid, group, time.monotonic() + 2, time.monotonic() + 3)
        self.assertEqual(0, os.waitstatus_to_exitcode(status))
        self.assertFalse(forced)
        with self.assertRaises(ChildProcessError): os.waitpid(pid, os.WNOHANG)
        self.assertEqual('', (group / 'cgroup.kill').read_text())

    def test_live_setsid_descendant_and_deadline_remain_forced_after_actual_join(self):
        for expired in (False, True):
            group = Path(self.owner.CGROUP) / ('expired' if expired else 'live')
            group.mkdir()
            (group / 'cgroup.events').write_text('populated 1\n')
            (group / 'cgroup.kill').write_text('')
            descendant = os.fork()
            if descendant == 0:
                os.setsid()
                end = time.monotonic() + 2
                while not (group / 'cgroup.kill').read_text() and time.monotonic() < end:
                    time.sleep(0.01)
                # Model the kernel's complete event snapshot. In-place truncation
                # exposes an impossible empty file to the concurrent owner read.
                (group / 'next.events').write_text('populated 0\n')
                (group / 'next.events').replace(group / 'cgroup.events')
                os._exit(0)
            worker = os.fork()
            if worker == 0:
                os._exit(0)
            try:
                status, forced = self.owner.join_workload(worker, group,
                    time.monotonic() + (-1 if expired else 2), time.monotonic() + 3)
                self.assertEqual(0, os.waitstatus_to_exitcode(status))
                self.assertTrue(forced)
                self.assertEqual('1', (group / 'cgroup.kill').read_text())
            finally:
                self.assertEqual(descendant, os.waitpid(descendant, 0)[0])
            with self.assertRaises(ChildProcessError): os.waitpid(worker, os.WNOHANG)

    def test_docker_all_states_and_forced_cleanup_cannot_be_success(self):
        for state in ('created', 'running', 'paused', 'restarting', 'removing', 'exited', 'dead'):
            self.configure(docker='a' * 64 + ' ' + state + '\n')
            self.assertEqual({'a' * 64: state}, self.owner.docker_census(time.monotonic() + 2))
        self.configure(docker='a' * 64 + ' unknown\n')
        self.refuses('docker_state_unknown', self.owner.docker_census, time.monotonic() + 2)
        self.configure(docker='')
        self.assertTrue(self.owner.docker_complete(time.monotonic() + 2, forced=False))
        self.assertFalse(self.owner.docker_complete(time.monotonic() + 2, forced=True))
        self.assertTrue(all('--all' in call['argv'] for call in self.calls() if call['name'] == 'docker'))

    def test_crash_inventory_refuses_with_only_closed_scope_and_name_class(self):
        temporary = self.root / 'runner-temp'
        temporary.mkdir()
        with mock.patch.dict(os.environ, {'RUNNER_TEMP': str(temporary)}):
            for path, scope, kind in (
                (str(self.owner.ROOT / 'PRIVATE-CANARY' / 'core.123'), 'workspace', 'numbered_core'),
                (str(temporary / 'PRIVATE-CANARY.dmp'), 'runner_temp', 'dmp'),
                ('/var/crash/PRIVATE-CANARY.crash', 'apport', 'crash'),
                ('/var/lib/systemd/coredump/PRIVATE-CANARY.core', 'systemd', 'core'),
                ('/var/lib/docker/PRIVATE-CANARY.dump', 'docker', 'dump'),
                ('/var/lib/docker/network/PRIVATE-CANARY.dump', 'docker_network', 'dump'),
                ('/var/lib/docker/buildkit/PRIVATE-CANARY.dump', 'docker_buildkit', 'dump'),
                ('/var/lib/docker/overlay2/PRIVATE-CANARY.dump', 'docker_overlay', 'dump'),
                ('/var/lib/docker/containerd/PRIVATE-CANARY.dump', 'docker_containerd', 'dump'),
                ('/var/lib/docker/containers/PRIVATE-CANARY.dump', 'docker_containers', 'dump'),
                ('/var/lib/docker/volumes/PRIVATE-CANARY.dump', 'docker_volumes', 'dump'),
                ('/tmp/PRIVATE-CANARY/core', 'tmp', 'core'),
                ('/var/tmp/PRIVATE-CANARY.dmp', 'var_tmp', 'dmp'),
                ('/unclassified/PRIVATE-CANARY.unknown', 'unknown', 'unknown'),
            ):
                with self.subTest(scope=scope, kind=kind):
                    self.configure(crash=path + '\0')
                    provenance = '_image_metadata_unavailable' if scope == 'docker_overlay' else ''
                    self.refuses('unexpected_crash_artifact_' + scope + '_' + kind + '_unknown' + provenance,
                                 self.owner.crash_scan, time.monotonic() + 2)
            self.configure(crash='PRIVATE-CANARY without a delimiter')
            self.refuses('unexpected_crash_artifact_unknown_unknown_unknown',
                         self.owner.crash_scan, time.monotonic() + 2)
            for description, label in (
                ('PostgreSQL custom database dump - v1.15-0 PRIVATE-CANARY', 'postgresql_backup'),
                ('ELF 64-bit LSB core file PRIVATE-CANARY', 'elf_core'),
                ('Mini DuMP crash report PRIVATE-CANARY', 'minidump'),
                ('ASCII text PRIVATE-CANARY', 'text'),
                ('gzip compressed data PRIVATE-CANARY', 'compressed'),
                ('Zstandard compressed data PRIVATE-CANARY', 'zstd'),
                ('SQLite 3.x database PRIVATE-CANARY', 'sqlite'),
                ('empty\n', 'empty'),
                ('data\n', 'binary_data'),
                ('PRIVATE-CANARY', 'unknown'),
            ):
                self.configure(crash='/var/lib/docker/PRIVATE-CANARY.dump\0', file_description=description)
                self.refuses('unexpected_crash_artifact_docker_dump_' + label,
                             self.owner.crash_scan, time.monotonic() + 2)
            self.configure(status={'file': 1})
            self.refuses('unexpected_crash_artifact_docker_dump_probe_refused',
                         self.owner.crash_scan, time.monotonic() + 2)
            self.configure(crash='')
            self.owner.crash_scan(time.monotonic() + 2)

    def test_image_provenance_prunes_only_verified_directories_after_complete_rerun(self):
        layer = '/var/lib/docker/overlay2/' + 'b' * 64 + '/diff'
        parent = '/var/lib/docker/overlay2/' + 'c' * 64 + '/diff'
        link = '/var/lib/docker/overlay2/l/' + 'D' * 26
        candidate = layer + '/PRIVATE-CANARY.dump'
        image = 'sha256:' + 'a' * 64
        record = {'id': image, 'driver': {'Name': 'overlay2', 'Data': {'UpperDir': layer, 'LowerDir': link}}}
        self.configure(image_info=json.dumps({'driver': 'overlay2', 'root': '/var/lib/docker'}),
                       image_ids=image + '\n' + image + '\n', image_inspect=json.dumps(record) + '\n',
                       realpaths={link: parent}, crash=candidate + '\0', file_description='data\n')
        temporary = self.root / 'runner-temp'
        temporary.mkdir()
        with mock.patch.dict(os.environ, {'RUNNER_TEMP': str(temporary)}):
            for path, label in ((candidate, 'match'), (parent + '/PRIVATE-CANARY.dump', 'match'),
                                (layer + '-prefix/PRIVATE-CANARY.dump', 'no_match'),
                                ('/var/lib/docker/overlay2/' + 'e' * 64 + '/diff/PRIVATE-CANARY.dump', 'no_match')):
                with self.subTest(path=path):
                    self.configure(crash=path + '\0')
                    if label == 'match':
                        self.assertEqual({'verifiedImageDirectories': 2}, self.owner.crash_scan(time.monotonic() + 3))
                    else:
                        self.refuses('unexpected_crash_artifact_docker_overlay_dump_binary_data_image_metadata_no_match',
                                     self.owner.crash_scan, time.monotonic() + 3)
            docker_calls = [call['argv'] for call in self.calls() if call['name'] == 'docker']
            self.assertTrue(docker_calls)
            self.assertTrue(all(args[:2] == ['--host', 'unix:///var/run/docker.sock'] for args in docker_calls))
            self.assertTrue(all(args[2] == 'info' or args[2:4] in (['image', 'ls'], ['image', 'inspect'])
                                for args in docker_calls))
            self.assertTrue(all(image in args for args in docker_calls if args[2:4] == ['image', 'inspect']))
            pruned = [call['argv'] for call in self.calls() if call['name'] == 'find' and '-prune' in call['argv']]
            self.assertTrue(pruned)
            for args in pruned:
                self.assertEqual([layer, parent], [args[i + 1] for i, arg in enumerate(args) if arg == '-path'])
            self.configure(crash=candidate + '\0')
            for unsafe in ('/var/lib/docker/overlay2/' + 'e' * 64 + '/diff/PRIVATE-CANARY.dump',
                           layer + '-prefix/PRIVATE-CANARY.dump', '/var/lib/docker/buildkit/PRIVATE-CANARY.dump',
                           '/var/lib/docker/volumes/PRIVATE-CANARY.dump', '/var/crash/PRIVATE-CANARY.dump',
                           str(temporary / 'PRIVATE-CANARY.dump'), str(self.owner.ROOT / 'PRIVATE-CANARY.dump')):
                self.configure(crash_after_prune=unsafe + '\0')
                with self.assertRaises(self.owner.Refusal) as caught:
                    self.owner.crash_scan(time.monotonic() + 3)
                self.assertTrue(caught.exception.reason.startswith('unexpected_crash_artifact_'))
                self.assertNotIn('PRIVATE-CANARY', caught.exception.reason)

    def test_image_provenance_missing_malformed_changed_or_unsupported_is_unavailable(self):
        layer = '/var/lib/docker/overlay2/' + 'b' * 64 + '/diff'
        candidate = layer + '/PRIVATE-CANARY.dump'
        image = 'sha256:' + 'a' * 64
        record = {'id': image, 'driver': {'Name': 'overlay2', 'Data': {'UpperDir': layer}}}
        valid = {'image_info': json.dumps({'driver': 'overlay2', 'root': '/var/lib/docker'}),
                 'image_ids': image + '\n', 'image_inspect': json.dumps(record) + '\n',
                 'status': {}, 'realpaths': {}, 'kinds': {}}
        changes = [
            {'image_info': ''}, {'image_info': '[]'},
            {'image_info': json.dumps({'driver': 'overlayfs', 'root': '/var/lib/docker'})},
            {'image_info': json.dumps({'driver': 'overlay2', 'root': '/private/other'})},
            {'image_ids': 'short\n'}, {'image_ids': image + '\ninvalid\n'},
            {'image_inspect': ''}, {'image_inspect': '[]'},
            {'image_inspect': json.dumps({**record, 'id': 'sha256:' + 'f' * 64})},
            {'image_inspect': json.dumps({**record, 'driver': None})},
            {'image_inspect': json.dumps({**record, 'driver': {'Name': 'overlay2', 'Data': {'UpperDir': '/tmp'}}})},
            {'realpaths': {layer: '/tmp/escaped'}}, {'kinds': {layer: 'regular file'}},
            {'status': {'docker': 1}}, {'status': {'realpath': 1}}, {'status': {'stat': 1}},
        ]
        for change in changes:
            with self.subTest(change=change):
                self.configure(**{**valid, **change})
                self.refuses('image_metadata_unavailable', self.owner.image_layer_snapshot,
                             candidate, time.monotonic() + 3)
        self.configure(**valid)
        original = self.owner.command
        lists = 0
        def changed(argv, *args, **kwargs):
            nonlocal lists
            if argv[0] == self.owner.DOCKER and argv[3:5] == ['image', 'ls']:
                lists += 1
                if lists == 2:
                    return b''
            return original(argv, *args, **kwargs)
        with mock.patch.object(self.owner, 'command', side_effect=changed):
            self.refuses('image_metadata_unavailable', self.owner.image_layer_snapshot,
                         candidate, time.monotonic() + 3)
        canonical_reads = 0
        def canonical_changed(argv, *args, **kwargs):
            nonlocal canonical_reads
            value = original(argv, *args, **kwargs)
            if self.owner.REALPATH in argv:
                canonical_reads += 1
                if canonical_reads == 2:
                    return value.replace(b'/diff', b'/changed')
            return value
        with mock.patch.object(self.owner, 'command', side_effect=canonical_changed):
            self.refuses('image_metadata_unavailable', self.owner.image_layer_snapshot,
                         candidate, time.monotonic() + 3)
        self.configure(**{**valid, 'image_ids': ''})
        self.refuses('image_metadata_no_match', self.owner.image_layer_snapshot,
                     candidate, time.monotonic() + 3)

    def test_real_find_prunes_exact_image_diff_and_still_finds_later_writable_artifact(self):
        virtual = '/var/lib/docker/overlay2/'
        layer, writable = virtual + 'b' * 64 + '/diff', virtual + 'e' * 64 + '/diff'
        tree = self.root / 'docker-tree'
        def actual(path):
            return tree / path.removeprefix('/var/lib/docker/')
        for path in (layer, writable, layer + '-prefix', virtual + 'b' * 64 + '/merged',
                     virtual + 'b' * 64 + '/work', '/var/lib/docker/volumes/owned'):
            actual(path).mkdir(parents=True)
        image = actual(layer) / 'image.dump'
        image.write_text('immutable image fixture')
        self.configure(real_find=True, real_find_root=str(tree))
        roots = [layer, writable]
        first = self.owner.find_crash_candidate(roots, time.monotonic() + 3)
        self.assertEqual(os.fsencode(image) + b'\0', first)
        self.assertEqual(b'', self.owner.find_crash_candidate(roots, time.monotonic() + 3, (layer,)))
        for path in (writable, layer + '-prefix', virtual + 'b' * 64 + '/merged',
                     virtual + 'b' * 64 + '/work', '/var/lib/docker/volumes/owned'):
            dirty = actual(path) / 'unexpected.dump'
            dirty.write_text('dirty control')
            self.assertEqual(os.fsencode(dirty) + b'\0', self.owner.find_crash_candidate(
                [layer, path], time.monotonic() + 3, (layer,)))
            dirty.unlink()
        self.refuses('crash_prune_invalid', self.owner.find_crash_candidate, roots,
                     time.monotonic() + 3, (virtual + 'b' * 64,))
        self.refuses('crash_prune_invalid', self.owner.find_crash_candidate, roots,
                     time.monotonic() + 3, (layer,) * 513)
        self.refuses('crash_prune_bound', self.owner.find_crash_candidate, ['x' * (128 * 1024)],
                     time.monotonic() + 3, (layer,))

    def test_image_metadata_change_after_pruned_scan_cannot_be_clean(self):
        layer = '/var/lib/docker/overlay2/' + 'b' * 64 + '/diff'
        candidate = layer + '/PRIVATE-CANARY.dump'
        image = 'sha256:' + 'a' * 64
        record = {'id': image, 'driver': {'Name': 'overlay2', 'Data': {'UpperDir': layer}}}
        self.configure(image_info=json.dumps({'driver': 'overlay2', 'root': '/var/lib/docker'}),
                       image_ids=image + '\n', image_inspect=json.dumps(record), crash=candidate + '\0')
        original, lists = self.owner.command, 0
        def changed(argv, *args, **kwargs):
            nonlocal lists
            if argv[0] == self.owner.DOCKER and argv[3:5] == ['image', 'ls']:
                lists += 1
                if lists == 3:
                    return b''
            return original(argv, *args, **kwargs)
        with mock.patch.dict(os.environ, {'RUNNER_TEMP': str(self.root)}), \
             mock.patch.object(self.owner, 'command', side_effect=changed):
            self.refuses('crash_image_metadata_changed', self.owner.crash_scan, time.monotonic() + 3)

    def test_discovery_uses_unique_ids_and_rejects_partial_aborted_and_duplicate_batches(self):
        cases = self.population()['all']
        raw = [{'Id': case['id'], 'DisplayName': case['name'], 'Source': '/private/LearnStack.Tests.Integration.dll',
                'Properties': [{'Value': 'PRIVATE-CANARY'}]} for case in cases]
        completed = {'TotalTests': len(raw), 'IsAborted': False, 'LastDiscoveredTests': raw[2:],
                     'FullyDiscoveredSources': ['/private/LearnStack.Tests.Integration.dll'],
                     'PartiallyDiscoveredSources': [], 'NotDiscoveredSources': [], 'SkippedDiscoverySources': []}
        path = self.root / 'private.diag'
        marker = 'prefix TestRequestSender.OnDiscoveryMessageReceived: Received message: '
        def write(payload=completed, first=raw[:2]):
            path.write_text(marker + json.dumps({'MessageType': 'TestDiscovery.TestFound', 'Payload': first}) + '\n'
                            + marker + json.dumps({'MessageType': 'TestDiscovery.Completed', 'Payload': payload}) + '\n')
        write()
        actual = self.owner.discovery_log(path, 'LearnStack.Tests.Integration')
        self.assertEqual(set(map(self.owner.key, cases)), set(map(self.owner.key, actual)))
        self.assertNotIn('PRIVATE-CANARY', json.dumps(actual))
        for change in ({'IsAborted': True}, {'TotalTests': 99}, {'PartiallyDiscoveredSources': ['incomplete']}):
            write({**completed, **change})
            with self.assertRaises(self.owner.Refusal): self.owner.discovery_log(path, 'LearnStack.Tests.Integration')
        write({**completed, 'LastDiscoveredTests': raw[:2]})
        self.refuses('duplicate_discovery_identity', self.owner.discovery_log, path, 'LearnStack.Tests.Integration')

    def test_partition_rejects_missing_duplicate_overlapping_and_unclassified_rows(self):
        clean = self.population()
        self.owner.validate_partition(clean)
        for part in ('all', 'native', 'ordinary'):
            dirty = json.loads(json.dumps(clean))
            dirty[part].pop()
            with self.assertRaises(self.owner.Refusal): self.owner.validate_partition(dirty)
        dirty = json.loads(json.dumps(clean))
        dirty['ordinary'].append(dirty['native'][0])
        self.refuses('partition_incomplete', self.owner.validate_partition, dirty)
        dirty = json.loads(json.dumps(clean))
        dirty['all'][0]['name'] = dirty['native'][0]['name'] = 'Future.Unclassified.Native.Proof'
        self.refuses('native_class_unclassified', self.owner.validate_partition, dirty)

    def test_source_census_requires_explicit_classification_and_every_test_project(self):
        tests = self.root / 'backend/tests'
        tests.mkdir()
        project = tests / 'LearnStack.Tests.Example.csproj'
        project.write_text('<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>')
        solution = self.root / 'backend/LearnStack.slnx'
        solution.write_text('<Solution><Project Path="tests/LearnStack.Tests.Example.csproj" /></Solution>')
        self.assertEqual([('tests/LearnStack.Tests.Example.csproj', 'LearnStack.Tests.Example')],
                         list(self.owner.test_projects()))
        project.write_text('<Project />')
        with self.assertRaises(self.owner.Refusal) as caught:
            list(self.owner.test_projects())
        self.assertEqual('test_project_classification_missing', caught.exception.reason)
        project.write_text('<Project><IsTestProject>true</IsTestProject></Project>')
        (tests / 'LearnStack.Tests.Omitted.csproj').write_text('<Project><IsTestProject>true</IsTestProject></Project>')
        with self.assertRaises(self.owner.Refusal) as caught:
            list(self.owner.test_projects())
        self.assertEqual('test_project_missing_from_solution', caught.exception.reason)

    def test_trx_sanitizes_raw_output_and_preserves_colliding_names_unique_ids(self):
        cases = self.population()['ordinary']
        path = self.trx(self.root / 'results', cases, raw_canary=True)
        actual = self.owner.trx_cases(path, sanitize=True)
        self.assertEqual(Counter(map(self.owner.key, cases)), Counter(map(self.owner.key, actual)))
        self.assertNotIn(b'PRIVATE-CANARY', path.read_bytes())
        self.assertEqual(2, len(actual))
        self.assertEqual(actual[0]['name'], actual[1]['name'])

    def test_trx_rejects_contradictory_counters_before_sanitizing_and_keeps_truthful_failure(self):
        cases = self.population()['ordinary']
        path = self.trx(self.root / 'counter-check', cases, raw_canary=True)
        clean = path.read_bytes()
        for name, value in (('skipped', '1'), ('passed', '0'), ('failed', '1'), ('notExecuted', '1'),
                            ('aborted', '1'), ('error', '1'), ('inProgress', '1'), ('passed', '-1'),
                            ('passed', '1.0'), ('passed', 'unknown')):
            root = ET.fromstring(clean)
            root.find('t:ResultSummary/t:Counters', self.owner.NS).set(name, value)
            ET.ElementTree(root).write(path, encoding='utf-8')
            self.refuses('trx_counters_invalid', self.owner.trx_cases, path, sanitize=True)
            self.assertIn(b'PRIVATE-CANARY', path.read_bytes())
        failed = self.trx(self.root / 'failed-counter-check', cases, outcome='Failed',
                          summary='Failed', raw_canary=True)
        self.assertTrue(all(row['outcome'] == 'Failed' for row in self.owner.trx_cases(failed, sanitize=True)))
        self.assertNotIn(b'PRIVATE-CANARY', failed.read_bytes())
        self.assertEqual('2', ET.parse(failed).find('t:ResultSummary/t:Counters', self.owner.NS).get('failed'))

    def test_export_keeps_raw_files_private_on_invalid_input_or_expired_budget(self):
        raw_results = self.root / 'private-trx'
        output = self.root / 'export'
        (output / 'results').mkdir(parents=True)
        raw = self.trx(raw_results, self.population()['ordinary'], raw_canary=True)
        self.refuses('deadline_exhausted', self.owner.export_results, raw_results, output,
                     time.monotonic() - 1)
        self.assertEqual([], list((output / 'results').iterdir()))
        self.assertIn(b'PRIVATE-CANARY', raw.read_bytes())
        raw.write_text('<invalid-PRIVATE-CANARY>')
        with self.assertRaises(ET.ParseError):
            self.owner.export_results(raw_results, output, time.monotonic() + 2)
        self.assertEqual([], list((output / 'results').iterdir()))
        raw = self.trx(raw_results, self.population()['ordinary'], raw_canary=True)
        self.owner.export_results(raw_results, output, time.monotonic() + 2)
        self.assertFalse(raw.exists())
        exported = output / 'results' / raw.name
        self.assertNotIn(b'PRIVATE-CANARY', exported.read_bytes())
        self.assertEqual(2, len(self.owner.trx_cases(exported)))
        self.refuses('deadline_exhausted', self.owner.export_hashes, output, time.monotonic() - 1)

    def test_native_topology_retains_only_closed_facts_before_private_input(self):
        stream = io.StringIO()
        seen = set()
        reason = ('supervisor_topology_membership_internal_yes_members_2_database_yes_supervisor_no'
                  '_running_no_oom_no_exit_refused_preflight_collector_pipe')
        for suffix in (reason, reason + '_PRIVATE_CANARY', reason.replace('members_2', 'members_7')):
            self.owner.native_refusal('The isolated generation phase fixture refused: ' + suffix + ':docker', stream, seen)
        closed_topology = reason.rsplit('_preflight_', 1)[0]
        self.owner.native_refusal('The isolated generation phase fixture refused: ' + closed_topology, stream, seen)
        self.owner.native_refusal('The isolated generation phase fixture refused: ' + closed_topology + '_private_extra', stream, seen)
        self.owner.native_refusal('The isolated Linux fatal probe refused: collector_pipe credential=PRIVATE_CANARY', stream, seen)
        self.assertEqual([{'reason': reason}, {'reason': closed_topology}, {'reason': 'collector_pipe'}],
                         [json.loads(line) for line in stream.getvalue().splitlines()])
        self.assertNotIn('PRIVATE_CANARY', stream.getvalue())

    def test_results_reject_skips_missing_trx_and_missing_completed_case(self):
        output = self.root / 'results-check'
        cases = self.population()['ordinary']
        self.refuses('trx_missing', self.owner.validate_results, output, cases)
        self.trx(output / 'results', cases, outcome='NotExecuted')
        self.refuses('results_incomplete', self.owner.validate_results, output, cases)
        self.trx(output / 'results', cases)
        output.joinpath('completed-cases.jsonl').write_text(json.dumps(
            {**cases[0], 'outcome': 'Passed', 'duration': '00:00:00.0010000'}) + '\n')
        self.refuses('completed_evidence_incomplete', self.owner.validate_results, output, cases)

    def test_failed_workload_diagnostic_exposes_only_closed_fact_and_never_completion(self):
        private = self.root / 'private'
        private.mkdir()
        self.assertEqual('workload_diagnostic_unavailable', self.owner.workload_failure(private))
        path = private / 'workload.json'
        for reason, expected in (('execution_diagnostic_bound', 'execution_diagnostic_bound'),
                                 ('completed_case_unexpected', 'completed_case_unexpected'),
                                 ('PRIVATE-CANARY provider message', 'workload_refused'),
                                 ({'private': 'PRIVATE-CANARY'}, 'workload_refused')):
            self.owner.write_json(path, {'protocol': 1, 'success': False, 'error': reason})
            self.assertEqual(expected, self.owner.workload_failure(private))
        for value in ({'protocol': 1, 'success': True}, [], None, {'protocol': 99, 'success': False}):
            self.owner.write_json(path, value)
            self.assertEqual('workload_diagnostic_unavailable', self.owner.workload_failure(private))
        path.write_text('PRIVATE-CANARY invalid JSON')
        self.assertEqual('workload_diagnostic_unavailable', self.owner.workload_failure(private))

    def test_internal_workload_fault_keeps_only_closed_owner_stage_and_exception_class(self):
        private = self.root / 'private-fault'
        private.mkdir()
        try:
            self.owner.case_identity({'DisplayName': 'PRIVATE-CANARY', 'Id': self.case(1)['id'],
                                      'Source': None}, 'LearnStack.Tests.Integration')
        except TypeError as failure:
            reason = self.owner.workload_fault(failure)
        else:
            self.fail('The invalid protocol source must actually fail')
        self.assertEqual('workload_internal_identity_type', reason)
        self.owner.write_json(private / 'workload.json', {'protocol': 1, 'success': False, 'error': reason})
        self.assertEqual(reason, self.owner.workload_failure(private))
        for failure, kind in ((FileNotFoundError('PRIVATE-CANARY'), 'missing_file'),
                              (PermissionError('PRIVATE-CANARY'), 'permission'),
                              (OSError('PRIVATE-CANARY'), 'io'), (RuntimeError('PRIVATE-CANARY'), 'runtime'),
                              (Exception('PRIVATE-CANARY'), 'other')):
            self.assertEqual('workload_internal_other_' + kind, self.owner.workload_fault(failure))

    def test_malformed_ownership_still_writes_failed_completion_receipt(self):
        private, output = self.root / 'private', self.root / 'output'
        (self.bin / 'cgroup/cgroup.controllers').touch()
        fields = {'SubState': 'exited', 'ActiveState': 'active', 'ExecMainCode': '1',
                  'ExecMainExitTimestampMonotonic': '1'}
        def launch(*args):
            self.owner.write_json(private / 'ownership.json', [])
        with contextlib.ExitStack() as stack:
            for name, value in (
                ('identity', {}), ('locations', ('owned.service', private, output)),
                ('deadlines', (time.monotonic() + 3, time.monotonic() + 6)),
                ('lower_core', None), ('docker_census', {}), ('command', b''),
                ('qualify_shard_vm', {}), ('observe_vm_memory', 1024),
                ('unit_properties', fields), ('docker_complete', False),
            ):
                stack.enter_context(mock.patch.object(self.owner, name, return_value=value))
            stack.enter_context(mock.patch.object(self.owner, 'launch_unit', side_effect=launch))
            stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
            self.assertEqual(1, self.owner.execute('ordinary-0'))
        result = self.owner.read_json(output / 'result.json')
        self.assertEqual('owned_completion_missing', result['error'])
        self.assertFalse(result['success'])
        self.assertFalse(result['ownershipComplete'])

    def test_unit_command_uses_nonroot_fixed_delegate_handshake_and_private_output(self):
        unit = 'learnstack-ci-42-1-native.service'
        private = self.root / 'private'
        private.mkdir(mode=0o700)
        fields = {'LoadState': 'loaded', 'ActiveState': 'active', 'SubState': 'running', 'Result': 'success',
                  'ControlGroup': '/system.slice/' + unit, 'User': str(os.getuid()), 'Group': str(os.getgid()),
                  'Delegate': 'yes', 'DelegateSubgroup': 'supervisor', 'Type': 'exec', 'RemainAfterExit': 'yes',
                  'ExecMainCode': '0', 'ExecMainStatus': '0', 'ExecMainExitTimestampMonotonic': '0'}
        self.configure(unit=fields)
        self.owner.launch_unit('native', {}, private, unit, time.monotonic() + 3)
        call = next(call for call in self.calls() if call['name'] == 'systemd-run')
        for expected in ('--service-type=exec', '--remain-after-exit', '--expand-environment=no',
                         '--property=Delegate=yes', '--property=DelegateSubgroup=supervisor',
                         '--property=LimitCORE=0', '--property=KillMode=control-group'):
            self.assertIn(expected, call['argv'])
        self.assertNotIn('--wait', call['argv'])
        self.assertNotIn('--pipe', call['argv'])
        self.assertEqual(0o600, (private / 'unit-private.log').stat().st_mode & 0o777)
        self.assertFalse(any('TOKEN=' in value or 'PRIVATE_CANARY' in value for value in call['argv']))

    def test_workflow_has_complete_partition_aggregate_fixed_exports_and_owned_budget(self):
        source = (ROOT / '.github/workflows/ci.yml').read_text()
        ordinary = source.split('  backend-integration-tests:\n', 1)[1].split('\n  # ───', 1)[0]
        native = source.split('  backend-native:\n', 1)[1].split('\n  # ───', 1)[0]
        aggregate = source.split('  backend-integration:\n', 1)[1].split('\n  # ───', 1)[0]
        for mode, block in (('ordinary-${{ matrix.index }}', ordinary), ('native-shard', native)):
            self.assertIn('timeout-minutes: 20', block)
            self.assertLess(block.index('LEARNSTACK_CI_STARTED_AT='), block.index('uses: actions/checkout@'))
            self.assertIn('python3 scripts/ci-native-tests.py ' + mode, block)
            self.assertIn('python3 scripts/ci-native-tests.py upload-budget', block)
            self.assertIn('fromJSON(steps.artifact-budget.outputs.minutes)', block)
            self.assertIn('--disable-build-servers', block)
            self.assertIn('results/*.trx', block)
            self.assertNotIn('toJSON(github)', block)
            self.assertNotIn('unit-private.log', block)
        self.assertIn('runs-on: ubuntu-24.04', native)
        self.assertIn('name: backend integration (Testcontainers)', aggregate)
        self.assertIn('if: always()', aggregate)
        self.assertIn('needs: [backend-base, backend-integration-tests, backend-native]', aggregate)
        self.assertIn('python3 scripts/ci-test-shards.py aggregate-integration', aggregate)
        self.assertIn('actions: read', aggregate)
        self.assertLess(self.owner.RESERVE_SECONDS, self.owner.JOB_SECONDS)
        self.assertEqual(self.owner.RESERVE_SECONDS, self.owner.TAIL_SECONDS + self.owner.UPLOAD_SECONDS)


if __name__ == '__main__':
    unittest.main()
