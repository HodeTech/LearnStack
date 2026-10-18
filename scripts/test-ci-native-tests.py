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
    print(config.get('docker', ''), end='')
elif name == 'find':
    print(config.get('crash', ''), end='')
elif name == 'dotnet':
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
    os.setsid()
    proc = base / 'proc' / str(os.getpid())
    proc.mkdir(parents=True)
    proc.joinpath('cmdline').write_bytes(b'dotnet\0testhost.dll\0')
    proc.joinpath('cgroup').write_text('0::' + config.get('work', '/owned/work') + '\n')
    base.joinpath('session.json').write_text(json.dumps({'pid':os.getpid(), 'sid':os.getsid(0)}))
    try:
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
        self.configure()
        fixture = FIXTURE.replace('PYTHON', sys.executable, 1)
        replacements = {'PROC': str(self.bin / 'proc'), 'CGROUP': str(self.bin / 'cgroup')}
        for name in ('DOTNET', 'SUDO', 'SYSTEMD_RUN', 'SYSTEMCTL', 'DOCKER', 'GIT', 'FIND'):
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
        self.temporary.cleanup()

    def configure(self, **values):
        self.config.update(values)
        (self.bin / 'fixture.json').write_text(json.dumps(self.config))

    def calls(self):
        path = self.bin / 'calls.jsonl'
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

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
                      executed=str(len(cases) if outcome != 'NotExecuted' else 0))
        path = output / 'owned.trx'
        ET.ElementTree(root).write(path, encoding='utf-8')
        return path

    def write_artifact(self, mode, context, population):
        output = self.root / 'artifacts/ci-aggregate' / mode
        output.mkdir(parents=True)
        self.trx(output / 'results', population[mode])
        self.owner.write_json(output / 'discovery.json', {'protocol': 1, 'identity': context, 'population': population})
        output.joinpath('completed-cases.jsonl').write_text(''.join(json.dumps(
            {'name': case['name'], 'outcome': 'Passed', 'duration': '< 1 ms'}) + '\n' for case in population[mode]))
        if mode == 'native':
            output.joinpath('native-refusals.jsonl').write_text('')
            self.owner.write_json(output / 'provisioning.json', {
                'protocol': 1, 'scope': 'native_ci_setup_not_child_qualification', 'collectorAfter': 'supported_file',
                'softCore': 'zero', 'hardCore': 'zero', 'sourceSha': context['sourceSha'],
                'runId': context['runId'], 'runAttempt': context['runAttempt'], 'job': 'backend-native'})
        self.owner.write_json(output / 'result.json', {
            'protocol': 1, 'identity': context, 'partition': mode, 'job': self.owner.JOBS[mode],
            'success': True, 'complete': True, 'ownershipComplete': True, 'dockerAbsent': True,
            'forcedCleanup': False, 'error': None, 'cases': len(population[mode]),
            'artifacts': self.owner.export_hashes(output)})
        return output

    def test_cli_is_closed_and_budget_query_is_read_only(self):
        for args in ([], ['native', '--command', '/bin/true'], ['--help'], ['_native-leaf', '/tmp'], ['arbitrary']):
            result = subprocess.run([sys.executable, self.copy, *args], capture_output=True, text=True, check=False)
            self.assertEqual(1, result.returncode)
            self.assertEqual('mode_refused', json.loads(result.stdout)['error'])
        result = subprocess.run([sys.executable, self.copy, 'upload-budget'], capture_output=True, text=True, check=False)
        self.assertEqual(0, result.returncode)
        self.assertEqual('minutes=1\n', result.stdout)
        self.assertEqual([], self.calls())

    def test_unqualified_leaf_never_invokes_host_executables(self):
        result = subprocess.run([sys.executable, self.copy, '_native-leaf'], env={}, capture_output=True, text=True)
        self.assertEqual(1, result.returncode)
        self.assertEqual('unsupported_context', json.loads(result.stdout)['error'])
        self.assertEqual([], self.calls())

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
        name = self.case(1)['name']
        self.configure(console=['raw PRIVATE-CANARY', '  Passed ' + name + ' [< 1 ms]'])
        evidence, console = io.StringIO(), io.StringIO()
        seen = Counter()
        with contextlib.redirect_stdout(console):
            status = self.owner.dotnet(['test'], time.monotonic() + 3, '/owned/work',
                lambda value: self.owner.completed_line(value, Counter({name: 1}), seen, evidence))
        self.assertEqual(0, status)
        self.assertEqual({'name': name, 'outcome': 'Passed', 'duration': '< 1 ms'}, json.loads(evidence.getvalue()))
        self.assertNotIn('PRIVATE-CANARY', console.getvalue())
        session = json.loads((self.bin / 'session.json').read_text())
        self.assertEqual(session['pid'], session['sid'], 'The actual subprocess changed session')

    def test_actual_testhost_outside_the_work_cgroup_is_refused(self):
        self.configure(work='/unrelated/session')
        self.refuses('testhost_outside_owned_subtree', self.owner.dotnet, ['test'],
                     time.monotonic() + 3, '/owned/work', lambda _line: None)

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
                (group / 'cgroup.events').write_text('populated 0\n')
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
        self.owner.native_refusal('The isolated Linux fatal probe refused: collector_pipe credential=PRIVATE_CANARY', stream, seen)
        self.assertEqual([{'reason': reason}, {'reason': 'collector_pipe'}],
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
            {'name': cases[0]['name'], 'outcome': 'Passed', 'duration': '1 ms'}) + '\n')
        self.refuses('completed_evidence_incomplete', self.owner.validate_results, output, cases)

    def test_aggregate_requires_both_jobs_complete_matching_artifacts_and_current_identity(self):
        context = {'sourceSha': 'a' * 40, 'runId': '42', 'runAttempt': '1', 'repository': 'HodeTech/LearnStack',
                   'workflowRef': 'reviewed-source', 'workflowSha': 'a' * 40}
        outputs = {mode: self.write_artifact(mode, context, self.population()) for mode in self.owner.JOBS}
        old = {key: os.environ.get(key) for key in ('LEARNSTACK_NATIVE_RESULT', 'LEARNSTACK_ORDINARY_RESULT')}
        try:
            os.environ.update({'LEARNSTACK_NATIVE_RESULT': 'success', 'LEARNSTACK_ORDINARY_RESULT': 'success'})
            with contextlib.redirect_stdout(io.StringIO()): self.assertEqual(0, self.owner.validate_aggregate(context))
            for bad in ('failure', 'cancelled', 'skipped', 'timed_out', ''):
                for variable in old:
                    os.environ[variable] = bad
                    self.refuses('execution_job_not_successful', self.owner.validate_aggregate, context)
                    os.environ[variable] = 'success'
            self.refuses('execution_receipt_incomplete', self.owner.validate_aggregate, {**context, 'sourceSha': 'b' * 40})
            for mode, output in outputs.items():
                result = self.owner.read_json(output / 'result.json')
                self.owner.write_json(output / 'result.json', {**result, 'forcedCleanup': True})
                self.refuses('execution_receipt_incomplete', self.owner.validate_aggregate, context)
                self.owner.write_json(output / 'result.json', result)
            output = outputs['ordinary']
            (output / 'completed-cases.jsonl').write_text('')
            self.refuses('artifact_hash_mismatch', self.owner.validate_aggregate, context)
        finally:
            for key, value in old.items():
                if value is None: os.environ.pop(key, None)
                else: os.environ[key] = value

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
        backend = source.split('  backend:\n', 1)[1].split('\n  # ───', 1)[0]
        self.assertIn('--filter "Requires!=Docker"', backend)
        for mode, block in (('ordinary', ordinary), ('native', native)):
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
        self.assertIn('needs: [backend-integration-tests, backend-native]', aggregate)
        self.assertIn('LEARNSTACK_NATIVE_RESULT: ${{ needs.backend-native.result }}', aggregate)
        self.assertIn('LEARNSTACK_ORDINARY_RESULT: ${{ needs.backend-integration-tests.result }}', aggregate)
        self.assertLess(self.owner.RESERVE_SECONDS, self.owner.JOB_SECONDS)
        self.assertEqual(self.owner.RESERVE_SECONDS, self.owner.TAIL_SECONDS + self.owner.UPLOAD_SECONDS)


if __name__ == '__main__':
    unittest.main()
