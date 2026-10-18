#!/usr/bin/env python3
"""Execute the actual ADR-0063 plan/selector owner; no host mutation or model copy."""
import copy
import contextlib
import errno
import importlib.util
import json
import io
from pathlib import Path
import tempfile
import time
import unittest
from unittest import mock
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts' / file)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


OWNER = module('ci_shards_controls', 'ci-test-shards.py')
LAUNCHER = module('ci_selector_discovery_controls', 'ci-native-tests.py')
PROVISIONER = module('ci_shard_runtime_controls', 'ci-native-provisioning.py')


class ShardControls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.plan = OWNER.load_plan()

    def case(self, assembly, method, n, name=None):
        return {'assembly': assembly, 'method': method, 'name': name or method,
                'id': '00000000-0000-4000-8000-' + f'{n:012x}'}

    def manifest(self):
        assembly = 'learnstack.tests.integration'
        fixture = self.plan['sharedFixtureClasses'][0]
        ordinary = [self.case(assembly, fixture + '.Method' + str(n // 2), n + 100,
                    fixture + '.Method' + str(n // 2) + '(row: ' + str(n % 2) + ')') for n in range(96)]
        architecture = [self.case('learnstack.tests.architecture', 'Example.Architecture.Method' + str(n),
                                  n + 500) for n in range(24)]
        base = [self.case('learnstack.tests.unit', 'Example.Unit.Method', 1)]
        native = [self.case(assembly, 'Example.Native.Method', 2)]
        return {'assemblies': ['learnstack.tests.architecture', assembly, 'learnstack.tests.unit'],
                'allTests': base + architecture + native + ordinary, 'nonDocker': base + architecture,
                'all': native + ordinary, 'native': native, 'ordinary': ordinary}

    def refuses(self, reason, function, *args):
        with self.assertRaises(OWNER.Refusal) as caught:
            function(*args)
        self.assertEqual(reason, caught.exception.reason)

    def test_current_plan_and_both_complete_assignments_have_exact_owners(self):
        manifest = self.manifest()
        for attempt in ('1', '2'):
            assignment = OWNER.assign(manifest, self.plan, attempt)
            self.assertEqual(2 + sum(self.plan['counts'].values()), len(assignment['owners']))
            self.assertTrue(all(assignment['owners'].values()))
            all_cases = [OWNER.case_key(case) for cases in assignment['owners'].values() for case in cases]
            self.assertEqual(len(all_cases), len(set(all_cases)))
            self.assertEqual(set(map(OWNER.case_key, manifest['allTests'])), set(all_cases))
            self.assertEqual('A' if attempt == '1' else 'B', assignment['variant'])

    def test_theory_rows_and_equal_selector_in_different_assemblies_are_not_split(self):
        manifest = self.manifest()
        manifest['nonDocker'][0]['method'] = manifest['ordinary'][0]['method']
        assignment = OWNER.assign(manifest, self.plan, '1')['owners']
        seen = {}
        for owner, cases in assignment.items():
            for case in cases:
                key = OWNER.method_key(case)
                self.assertIn(seen.get(key, owner), (owner,))
                seen[key] = owner
        for owner, cases in assignment.items():
            if owner.startswith('ordinary-'):
                self.assertEqual(0, len(cases) % 2)

    def test_assignment_is_deterministic_and_same_counts_do_not_imply_same_layout(self):
        manifest = self.manifest()
        first = OWNER.assign(manifest, self.plan, '1')
        reordered = copy.deepcopy(manifest)
        for part in OWNER.PARTS:
            reordered[part].reverse()
        second = OWNER.assign(reordered, self.plan, '1')
        self.assertEqual(first['owners'], second['owners'])
        self.assertEqual(first['planDigest'], second['planDigest'])
        self.assertEqual(first['manifestDigest'], second['manifestDigest'])
        self.assertTrue(OWNER.different_layouts(manifest, self.plan))
        self.assertNotEqual(first['owners'], OWNER.assign(manifest, self.plan, '2')['owners'])

    def test_one_selector_cannot_cross_original_trait_partitions(self):
        for first, second in (('nonDocker', 'ordinary'), ('native', 'ordinary'), ('nonDocker', 'native')):
            manifest = self.manifest()
            case, other = manifest[first][0], manifest[second][0]
            case['assembly'], case['method'] = other['assembly'], other['method']
            for attempt in ('1', '2'):
                self.refuses('selector_partition_conflict', OWNER.assign, manifest, self.plan, attempt)

    def test_estimates_cannot_hide_new_cases_or_move_native_or_project_base(self):
        manifest = self.manifest()
        new = self.case('new.tests.project', 'New.Project.Method', 9999)
        manifest['assemblies'].append('new.tests.project')
        manifest['allTests'].append(new)
        manifest['nonDocker'].append(new)
        assignment = OWNER.assign(manifest, self.plan, '1')
        self.assertIn(new, assignment['owners']['base'])
        self.assertEqual(manifest['native'], assignment['owners']['native'])
        self.assertTrue(assignment['unknownWeights'])
        changed = copy.deepcopy(self.plan)
        changed['defaultMethodMs'] = 1
        self.assertEqual(set(map(OWNER.case_key, manifest['allTests'])),
                         {OWNER.case_key(c) for rows in OWNER.assign(manifest, changed, '1')['owners'].values()
                          for c in rows})

    def test_missing_population_foreign_and_duplicate_identity_are_rejected(self):
        clean = self.manifest()
        for mutate, reason in ((lambda m: m['ordinary'].pop(), 'population_union_invalid'),
                (lambda m: m['allTests'].append(copy.deepcopy(m['allTests'][0])), 'duplicate_case_identity'),
                (lambda m: m['assemblies'].remove('learnstack.tests.unit'), 'case_assembly_unowned')):
            dirty = copy.deepcopy(clean); mutate(dirty)
            self.refuses(reason, OWNER.validate_manifest, dirty)
        self.assertTrue(OWNER.validate_manifest(clean))

    def test_display_names_are_neither_identity_nor_method_authority(self):
        manifest = self.manifest()
        for case in manifest['allTests']:
            case['name'] = 'same display name'
        self.assertTrue(OWNER.validate_manifest(manifest))
        raw = {'DisplayName': 'Display pretends to be a different method',
               'Id': manifest['ordinary'][0]['id'], 'Source': 'LearnStack.Tests.Integration.dll',
               'FullyQualifiedName': manifest['ordinary'][0]['method']}
        actual = LAUNCHER.case_identity(raw, 'LearnStack.Tests.Integration', selectors=True)
        self.assertEqual(raw['FullyQualifiedName'], actual['method'])
        raw.pop('FullyQualifiedName')
        with self.assertRaises(LAUNCHER.Refusal):
            LAUNCHER.case_identity(raw, 'LearnStack.Tests.Integration', selectors=True)

    def test_real_discovery_parser_retains_actual_fqn_and_all_rows(self):
        cases = self.manifest()['ordinary'][:2]
        raw = [{'DisplayName': c['name'], 'Id': c['id'], 'FullyQualifiedName': c['method'],
                'Source': 'LearnStack.Tests.Integration.dll'} for c in cases]
        payload = {'MessageType': 'TestDiscovery.Completed', 'Payload': {'IsAborted': False,
                   'TotalTests': 2, 'LastDiscoveredTests': raw,
                   'FullyDiscoveredSources': ['LearnStack.Tests.Integration.dll'],
                   'PartiallyDiscoveredSources': [], 'NotDiscoveredSources': [], 'SkippedDiscoverySources': []}}
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'discovery.diag'
            path.write_text('TestRequestSender.OnDiscoveryMessageReceived: Received message: ' + json.dumps(payload))
            selected = LAUNCHER.discovery_log(path, 'LearnStack.Tests.Integration', selectors=True)
            OWNER.selected_equal(cases, selected)
            payload['Payload']['LastDiscoveredTests'][0].pop('FullyQualifiedName')
            path.write_text('TestRequestSender.OnDiscoveryMessageReceived: Received message: ' + json.dumps(payload))
            with self.assertRaises(LAUNCHER.Refusal):
                LAUNCHER.discovery_log(path, 'LearnStack.Tests.Integration', selectors=True)

    def test_case_insensitive_alias_and_shell_filter_grammar_are_refused(self):
        manifest = self.manifest()
        manifest['ordinary'][1]['method'] = manifest['ordinary'][0]['method'].swapcase()
        self.refuses('selector_case_alias', OWNER.validate_manifest, manifest)
        for bad in ('Class.Method|FullyQualifiedName=Other', 'Class.Method)', 'Class.Method\0',
                    'Class.Method$(touch)', 'Class.Method&Requires!=Docker'):
            self.refuses('selector_unsupported', OWNER.validate_selector, 'tests', bad)

    def test_bounds_are_finite_and_topology_cannot_auto_expand(self):
        for value in (3, 7, True):
            dirty = copy.deepcopy(self.plan); dirty['counts']['architecture'] = value
            self.refuses('plan_topology_invalid', OWNER.validate_plan, dirty)
        for value in (7, 13, True):
            dirty = copy.deepcopy(self.plan); dirty['counts']['ordinary'] = value
            self.refuses('plan_topology_invalid', OWNER.validate_plan, dirty)
        dirty = copy.deepcopy(self.plan); dirty['bounds']['filterBytes'] *= 2
        self.refuses('plan_bounds_invalid', OWNER.validate_plan, dirty)
        for attempt in ('0', '', '01', '-1', '1;echo', None):
            self.refuses('attempt_invalid', OWNER.variant, attempt)

    def test_filter_has_whole_method_clauses_and_selection_requires_actual_ids(self):
        manifest = self.manifest()
        cases = OWNER.assign(manifest, self.plan, '1')['owners']['ordinary-0']
        expression = OWNER.filter_for('ordinary-0', 'learnstack.tests.integration', cases, self.plan)
        self.assertIn('Requires=Docker&NativeCrashProof!=true', expression)
        self.assertEqual(len({c['method'] for c in cases}), expression.count('FullyQualifiedName='))
        OWNER.selected_equal(cases, list(reversed(cases)))
        self.refuses('selected_population_mismatch', OWNER.selected_equal, cases, cases[:-1])
        foreign = copy.deepcopy(cases); foreign[0]['id'] = self.manifest()['native'][0]['id']
        self.refuses('selected_population_mismatch', OWNER.selected_equal, cases, foreign)
        OWNER.validate_argv(['dotnet', '--filter', expression], self.plan)
        self.refuses('argv_bound', OWNER.validate_argv, ['dotnet', 'x' * 65536], self.plan)
        self.refuses('argv_bound', OWNER.validate_argv, ['dotnet', '\0'], self.plan)

    def test_filter_and_argv_accept_the_boundary_and_refuse_one_more_byte(self):
        cases = [self.case('tests', 'Class.' + 'M' * 1900 + str(i), i) for i in range(16)]
        cases.append(self.case('tests', 'Class.Boundary', 99))
        expression = OWNER.filter_for('ordinary-0', 'tests', cases, self.plan)
        cases[-1]['method'] += 'x' * (32768 - len(expression.encode()))
        self.assertEqual(32768, len(OWNER.filter_for('ordinary-0', 'tests', cases, self.plan).encode()))
        cases[-1]['method'] += 'x'
        self.refuses('filter_bound', OWNER.filter_for, 'ordinary-0', 'tests', cases, self.plan)
        OWNER.validate_argv(['dotnet', 'x' * (65536 - len('dotnet') - 2)], self.plan)
        self.refuses('argv_bound', OWNER.validate_argv,
                     ['dotnet', 'x' * (65536 - len('dotnet') - 1)], self.plan)

    def test_oversize_filter_refuses_instead_of_splitting_rows(self):
        cases = [self.case('tests', 'Class.' + 'M' * 1000 + str(i), i) for i in range(40)]
        self.refuses('filter_bound', OWNER.filter_for, 'ordinary-0', 'tests', cases, self.plan)

    def test_complete_aggregate_refuses_missing_extra_and_wrong_owner_despite_equal_union(self):
        manifest = self.manifest()
        owners = OWNER.assign(manifest, self.plan, '1')['owners']
        for scope in ('backend', 'integration'):
            subset = {k: v for k, v in owners.items() if (k == 'base' or k.startswith('architecture-'))
                      == (scope == 'backend')}
            OWNER.validate_union(manifest, self.plan, '1', subset, scope)
            dirty = copy.deepcopy(subset); dirty.pop(next(iter(dirty)))
            self.refuses('aggregate_owners_incomplete', OWNER.validate_union,
                         manifest, self.plan, '1', dirty, scope)
            dirty = copy.deepcopy(subset); dirty['foreign-0'] = []
            self.refuses('aggregate_owners_incomplete', OWNER.validate_union,
                         manifest, self.plan, '1', dirty, scope)
            first, second = [k for k in subset if '-' in k][:2]
            dirty = copy.deepcopy(subset); dirty[first], dirty[second] = dirty[second], dirty[first]
            self.refuses('selected_population_mismatch', OWNER.validate_union,
                         manifest, self.plan, '1', dirty, scope)

    def test_stale_other_attempt_cannot_stand_in_for_current_assignment(self):
        manifest = self.manifest()
        owners = OWNER.assign(manifest, self.plan, '1')['owners']
        backend = {k: v for k, v in owners.items() if k == 'base' or k.startswith('architecture-')}
        self.refuses('selected_population_mismatch', OWNER.validate_union,
                     manifest, self.plan, '2', backend, 'backend')

    def test_bin_renumbering_is_not_an_independence_witness(self):
        manifest = self.manifest()
        owners = OWNER.assign(manifest, self.plan, '1')['owners']
        renamed = {}
        for name, rows in owners.items():
            if '-' in name:
                scope, _, index = name.rpartition('-')
                name = scope + '-' + str((int(index) + 1) % self.plan['counts'][scope])
            renamed[name] = rows
        with mock.patch.object(OWNER, 'assign', side_effect=lambda m, p, a:
                               {'owners': owners if a == '1' else renamed}):
            self.refuses('assignment_not_distinct', OWNER.different_layouts, manifest, self.plan)

    def test_assignment_controls_detect_a_removed_owner_equality_predicate(self):
        # The same whole test must go red if only exact owner equality is blind:
        # its wrong-owner control preserves the global union, so counts cannot
        # independently rescue the missing predicate.
        with mock.patch.object(OWNER, 'selected_equal', return_value=None):
            with self.assertRaises(AssertionError):
                self.test_complete_aggregate_refuses_missing_extra_and_wrong_owner_despite_equal_union()

    def test_equal_weights_use_ordinal_keys_not_key_length(self):
        manifest = self.manifest()
        rows = manifest['ordinary']
        expected = sorted({c['method'] for c in rows})
        actual = OWNER.assign(manifest, self.plan, '1')['owners']
        count = self.plan['counts']['ordinary']
        for index in range(count):
            self.assertEqual(set(expected[index::count]),
                             {c['method'] for c in actual['ordinary-' + str(index)]})

    def test_duplicate_json_weights_and_false_type_provenance_are_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'plan.json'; path.write_text('{"schemaVersion":1,"schemaVersion":1}')
            self.refuses('duplicate_json_key', OWNER.load_plan, path)
        dirty = copy.deepcopy(self.plan); dirty['weights'].append(copy.deepcopy(dirty['weights'][0]))
        self.refuses('weight_invalid', OWNER.validate_plan, dirty)
        dirty = copy.deepcopy(self.plan); dirty['defaultMethodMs'] = False
        self.refuses('plan_estimate_invalid', OWNER.validate_plan, dirty)
        dirty = copy.deepcopy(self.plan); next(iter(dirty['sources'].values()))['artifactSha256'] = 'not-a-hash'
        self.refuses('weight_source_invalid', OWNER.validate_plan, dirty)

    def result_fixture(self, output, expected, actual):
        (output / 'results').mkdir(exist_ok=True)
        tag = lambda name: '{' + LAUNCHER.NS['t'] + '}' + name
        root = ET.Element(tag('TestRun'))
        results = ET.SubElement(root, tag('Results'))
        definitions = ET.SubElement(root, tag('TestDefinitions'))
        row = {**expected, 'outcome': 'Passed', 'duration': '00:00:00.0010000'}
        ET.SubElement(results, tag('UnitTestResult'), testId=actual['id'], testName=actual['name'],
                      outcome=row['outcome'], duration=row['duration'])
        ET.SubElement(definitions, tag('UnitTest'), id=actual['id'], storage=actual['assembly'] + '.dll')
        summary = ET.SubElement(root, tag('ResultSummary'), outcome='Completed')
        ET.SubElement(summary, tag('Counters'), total='1', executed='1', passed='1', failed='0', notExecuted='0')
        path = output / 'results/actual.trx'
        ET.ElementTree(root).write(path, encoding='utf-8')
        row.pop('method')
        (output / 'completed-cases.jsonl').write_text(json.dumps(row) + '\n')
        return path

    def test_trx_names_accept_only_actual_expected_forward_utf16_encoding(self):
        name = 'Example.Method(input: "🎼😀é<& literal \\ud83d\\ude00")'
        case = self.case('tests', 'Example.Method', 1, name)
        escaped = 'Example.Method(input: "\\ud83c\\udfbc\\ud83d\\ude00é<& literal \\ud83d\\ude00")'
        self.assertEqual(escaped, LAUNCHER.trx_xml_name(name))
        self.assertEqual('a\\u0001b\\ufffec', LAUNCHER.trx_xml_name('a\x01b\ufffec'))
        for actual_name in (name, escaped):
            with tempfile.TemporaryDirectory() as tmp:
                output = Path(tmp)
                path = self.result_fixture(output, case, {**case, 'name': actual_name})
                original = path.read_bytes()
                self.assertEqual(1, LAUNCHER.validate_results(output, [case]))
                self.assertEqual(original, path.read_bytes())

    def test_trx_encoding_does_not_relax_names_ids_or_completed_stream(self):
        case = self.case('tests', 'Example.Method', 1, 'Example.Method(😀😀)')
        dirty_names = ('Example.Method(😁😀)', 'Example.Method(\\ud83d\\ude00😀)',
                       'Example.Method(\\uD83D\\uDE00\\uD83D\\uDE00)', 'Other.Method(😀😀)')
        changes = [{'name': name} for name in dirty_names] + [{'assembly': 'other'}, {'id': 'foreign'}]
        for change in changes:
            with tempfile.TemporaryDirectory() as tmp:
                output = Path(tmp)
                self.result_fixture(output, case, {**case, **change})
                with self.assertRaises(LAUNCHER.Refusal) as caught:
                    LAUNCHER.validate_results(output, [case])
                self.assertEqual('trx_case_name_mismatch', caught.exception.reason)
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp)
            self.result_fixture(output, case, {**case, 'name': LAUNCHER.trx_xml_name(case['name'])})
            path = output / 'completed-cases.jsonl'
            row = json.loads(path.read_text()); row['name'] = LAUNCHER.trx_xml_name(case['name'])
            path.write_text(json.dumps(row) + '\n')
            with self.assertRaises(LAUNCHER.Refusal) as caught:
                LAUNCHER.validate_results(output, [case])
            self.assertEqual('completed_evidence_incomplete', caught.exception.reason)

    def test_membership_exit_race_is_limited_to_candidate_cmdline(self):
        with tempfile.TemporaryDirectory() as tmp:
            proc = Path(tmp); task = proc / '123'; task.mkdir()
            (task / 'cmdline').write_bytes(b'dotnet\0testhost.dll\0')
            (task / 'cgroup').write_text('0::/owned/work\n')
            original = Path.read_bytes
            def failing(path):
                if path.name == self.failed_file:
                    raise OSError(self.failed_errno, 'PRIVATE-CANARY')
                return original(path)
            with mock.patch.object(LAUNCHER, 'PROC', str(proc)):
                self.assertEqual(1, LAUNCHER.testhost_membership('/owned/work'))
                (task / 'cgroup').write_text('0::/foreign/work\n')
                with self.assertRaises(LAUNCHER.Refusal) as caught:
                    LAUNCHER.testhost_membership('/owned/work')
                self.assertEqual('testhost_outside_owned_subtree', caught.exception.reason)
                (task / 'cgroup').write_text('0::/owned/work\n')
                self.failed_file = 'cmdline'
                for self.failed_errno in (errno.ENOENT, errno.ESRCH):
                    with mock.patch.object(Path, 'read_bytes', failing):
                        self.assertEqual(0, LAUNCHER.testhost_membership('/owned/work'))
                for self.failed_errno, reason in ((errno.EIO, 'io'), (errno.EACCES, 'permission')):
                    with mock.patch.object(Path, 'read_bytes', failing):
                        with self.assertRaises(LAUNCHER.Refusal) as caught:
                            LAUNCHER.testhost_membership('/owned/work')
                    self.assertEqual('membership_cmdline_' + reason, caught.exception.reason)
                with mock.patch.object(LAUNCHER, 'cgroup_path', side_effect=LAUNCHER.Refusal('input_unreadable')):
                    with self.assertRaises(LAUNCHER.Refusal) as caught:
                        LAUNCHER.testhost_membership('/owned/work')
                self.assertEqual('membership_cgroup_unreadable', caught.exception.reason)
                with mock.patch.object(Path, 'iterdir', side_effect=ProcessLookupError(errno.ESRCH, 'PRIVATE-CANARY')):
                    with self.assertRaises(LAUNCHER.Refusal) as caught:
                        LAUNCHER.testhost_membership('/owned/work')
                self.assertEqual('membership_enumeration_missing_task', caught.exception.reason)

    def test_nondocker_vm_qualification_never_observes_docker(self):
        calls = []
        root = ROOT
        class Budget:
            def command(self, argv, reason):
                calls.append(argv)
                if argv[0] == PROVISIONER.DOCKER:
                    return 0, b''
                return {tuple([PROVISIONER.GIT, 'rev-parse', '--verify', 'HEAD']): (0, b'a' * 40 + b'\n'),
                        tuple([PROVISIONER.GIT, 'rev-parse', '--show-toplevel']): (0, (str(root) + '\n').encode()),
                        tuple([PROVISIONER.UNAME, '-s', '-r', '-m']): (0, b'Linux 6.8.0 x86_64\n'),
                        tuple([PROVISIONER.ID, '-u']): (0, b'1001\n'),
                        tuple([PROVISIONER.NPROC]): (0, b'4\n'),
                        tuple([PROVISIONER.VIRT, '--container']): (1, b'none\n'),
                        tuple([PROVISIONER.VIRT, '--vm']): (0, b'microsoft\n')}[tuple(argv)]
            def read(self, path, reason, limit=4096):
                return ('ID=ubuntu\nVERSION_ID="24.04"\n' if path == PROVISIONER.OS_RELEASE
                        else '0::/init.scope\n')
        with mock.patch.dict('os.environ', {'GITHUB_SHA': 'a' * 40}), \
                mock.patch.object(PROVISIONER, 'CONTAINER_MARKERS', ()):
            self.assertEqual(4, PROVISIONER.qualify_runtime(Budget(), root, docker=False)['cpus'])
            self.assertFalse(any(argv[0] == PROVISIONER.DOCKER for argv in calls))
            PROVISIONER.qualify_runtime(Budget(), root)
            self.assertEqual(1, sum(argv[0] == PROVISIONER.DOCKER for argv in calls))
        for mode in LAUNCHER.SHARD_JOBS:
            self.assertEqual(mode == 'native-shard' or mode.startswith('ordinary-'), LAUNCHER.docker_mode(mode))
            self.assertEqual(mode == 'native-shard', LAUNCHER.native_mode(mode))

    def test_actual_adapter_evidence_binds_resolved_path_version_and_bytes(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); private = root / 'private'; private.mkdir()
            project = 'tests/LearnStack.Tests.Example/LearnStack.Tests.Example.csproj'
            binary = (root / 'backend' / project).parent / 'bin/Release/net10.0/xunit.runner.visualstudio.testadapter.dll'
            binary.parent.mkdir(parents=True); binary.write_bytes(b'actual-binary-fixture')
            diagnostic = private / 'example-execution.diag'
            host = private / 'example-execution.host.123.diag'
            original = ("Resolved assembly: xunit.runner.visualstudio.testadapter, from path: " + str(binary) + '\n'
                        "inside assembly 'xunit.runner.visualstudio.testadapter, Version=2.8.2.0, Culture=neutral'\n")
            host.write_text(original)
            with mock.patch.object(LAUNCHER, 'ROOT', root):
                evidence = LAUNCHER.adapter_evidence(private, diagnostic, project)
                self.assertEqual('2.8.2.0', evidence['assemblyVersion'])
                self.assertEqual(OWNER.hashlib.sha256(binary.read_bytes()).hexdigest(), evidence['sha256'])
                for dirty in (original.replace(str(binary), str(root / 'foreign.dll')),
                              original + original.replace('2.8.2.0', '2.8.1.0'), ''):
                    host.write_text(dirty)
                    with self.assertRaises(LAUNCHER.Refusal):
                        LAUNCHER.adapter_evidence(private, diagnostic, project)

    def test_actual_nondocker_parent_never_calls_docker_or_native_authorities(self):
        context = {'runId': '42', 'runAttempt': '1', 'sourceSha': 'a' * 40}
        for mode in ('base', 'architecture-0'):
            with tempfile.TemporaryDirectory() as tmp, contextlib.ExitStack() as stack:
                root = Path(tmp); private = root / 'private'; output = root / 'output'
                fake_cgroup = root / 'cgroup'; fake_cgroup.mkdir(); (fake_cgroup / 'cgroup.controllers').touch()
                manifest = self.manifest()
                selected = OWNER.assign(manifest, self.plan, '1')['owners'][mode]
                commands = mock.Mock(return_value=b'')
                def launch(*_):
                    LAUNCHER.write_json(private / 'ownership.json', {'protocol': 1, 'workloadJoined': True,
                        'workPopulated': False, 'witnessBeforeUnitExit': True, 'workloadStatus': 0,
                        'forcedTermination': False})
                    LAUNCHER.write_json(private / 'workload.json', {'success': True})
                    LAUNCHER.write_json(output / 'discovery.json', {'identity': context, 'population': manifest})
                for name, replacement in {'identity': lambda _: context,
                    'deadlines': lambda: (time.monotonic() + 5, time.monotonic() + 10),
                    'lower_core': lambda: None, 'locations': lambda *_: ('owned.service', private, output),
                    'CGROUP': str(fake_cgroup), 'DOTNET': '/usr/bin/true', 'SHARDS': OWNER,
                    'qualify_shard_vm': lambda *_: {'cpus': 4}, 'launch_unit': launch,
                    'observe_vm_memory': lambda: 1024,
                    'command': commands,
                    'unit_properties': lambda *_: {'SubState': 'exited', 'ExecMainCode': '1',
                        'ExecMainStatus': '0', 'ExecMainExitTimestampMonotonic': '123', 'Result': 'success'},
                    'validate_results': lambda _out, cases: len(cases), 'export_hashes': lambda *_: {},
                    'docker_census': mock.Mock(side_effect=AssertionError('Docker must not be observed')),
                    'docker_complete': mock.Mock(side_effect=AssertionError('Docker must not be cleaned')),
                    'crash_scan': mock.Mock(side_effect=AssertionError('Crash inventory forbidden')),
                    'prevalidate_native': mock.Mock(side_effect=AssertionError('Native authority forbidden'))}.items():
                    stack.enter_context(mock.patch.object(LAUNCHER, name, replacement))
                stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
                self.assertEqual(0, LAUNCHER.execute(mode))
                result = LAUNCHER.read_json(output / 'result.json')
                self.assertTrue(result['ownershipComplete'])
                self.assertEqual(1024, result['observedVmMemoryPeakBytes'])
                self.assertIsNone(result['dockerAbsent'])
                self.assertFalse(result['authority']['docker'])
                self.assertEqual(len(selected), result['cases'])
                failed_private, failed_output = root / 'failed', root / 'failed-output'
                commands.reset_mock()
                with mock.patch.object(LAUNCHER, 'launch_unit', side_effect=LAUNCHER.Refusal(
                        'unit_startup_resource_controllers_unavailable')), \
                        mock.patch.object(LAUNCHER, 'locations', return_value=(
                            'failed.service', failed_private, failed_output)):
                    self.assertEqual(1, LAUNCHER.execute(mode))
                failed = LAUNCHER.read_json(failed_output / 'result.json')
                self.assertEqual('unit_startup_resource_controllers_unavailable', failed['error'])
                self.assertFalse(failed['ownershipComplete'])
                self.assertFalse((failed_private / 'launch').exists())
                self.assertTrue(any(call.args[0][:4] == [LAUNCHER.SUDO, '-n', LAUNCHER.SYSTEMCTL, 'stop']
                                    and call.args[0][4] == 'failed.service' for call in commands.call_args_list))
                # Making only the Docker classification blind must break this
                # same parent path before any fixture workload can rescue it.
                with mock.patch.object(LAUNCHER, 'docker_mode', return_value=True), \
                        mock.patch.object(LAUNCHER, 'locations', return_value=('other.service', root / 'other', root / 'other-out')):
                    with self.assertRaises(AssertionError):
                        LAUNCHER.execute(mode)

    def test_actual_shard_workload_rediscovery_precedes_execution_and_retains_exact_results(self):
        manifest = self.manifest(); context = {'runAttempt': '1'}
        expected = OWNER.assign(manifest, self.plan, '1')['owners']['base']
        assembly, project = 'LearnStack.Tests.Unit', 'tests/LearnStack.Tests.Unit/LearnStack.Tests.Unit.csproj'
        for dirty in (False, True):
            with tempfile.TemporaryDirectory() as tmp, contextlib.ExitStack() as stack:
                root = Path(tmp); private = root / 'private'; output = root / 'output'
                private.mkdir(); output.mkdir(); (output / 'results').mkdir()
                def execute(_argv, _deadline, _work, _consume, progress):
                    self.assertEqual(1, selection.call_count)
                    row = expected[0]
                    payload = {'TestRunCompleteArgs': {'IsCanceled': False, 'IsAborted': False, 'Error': None,
                        'TestRunStatistics': {'ExecutedTests': 1, 'Stats': {'Passed': 1}}},
                        'LastRunTests': {'NewTestResults': [{'TestCase': {'Id': row['id'], 'DisplayName': row['name'],
                            'Source': assembly + '.dll'}, 'Outcome': 1, 'Duration': '00:00:00.0010000'}]}}
                    progress.line(progress.marker + json.dumps({'MessageType': 'TestExecution.Completed',
                                                                'Payload': payload}).encode())
                    prepared = root / 'prepared'; prepared.mkdir()
                    self.result_fixture(prepared, row, row).replace(private / 'trx/actual.trx')
                    return 0
                actual = copy.deepcopy(expected)
                if dirty:
                    actual[0]['id'] = '00000000-0000-4000-8000-000000009999'
                selection = mock.Mock(return_value=actual)
                execution = mock.Mock(side_effect=execute)
                for name, value in {'runtime_toolchain': lambda *_: {'sdk': '10.0.302'},
                    'base_gates': lambda *_: {'format': True}, 'discover_selectors': lambda *_: manifest,
                    'test_projects': lambda: iter([(project, assembly)]), 'selected_discovery': selection,
                    'shard_dotnet': execution, 'adapter_evidence': lambda *_: {'assemblyVersion': '2.8.2.0'}}.items():
                    stack.enter_context(mock.patch.object(LAUNCHER, name, value))
                stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
                if dirty:
                    with self.assertRaises(LAUNCHER.Refusal) as caught:
                        LAUNCHER.shard_workload('base', context, private, output, time.monotonic() + 5, '/owned')
                    self.assertEqual('selected_population_mismatch', caught.exception.reason)
                    execution.assert_not_called()
                else:
                    LAUNCHER.shard_workload('base', context, private, output, time.monotonic() + 5, '/owned')
                    self.assertEqual(1, LAUNCHER.validate_results(output, expected))
                    self.assertTrue(LAUNCHER.read_json(private / 'workload.json')['success'])
                    assignment = LAUNCHER.read_json(output / 'assignment.json')
                    self.assertEqual(expected, assignment['selected'])
                    measurement = LAUNCHER.read_json(output / 'measurements.json')
                    self.assertIsNone(measurement['fixtureStartupSeconds'])
                    self.assertEqual(1, len(measurement['projects']))

    def test_all_shard_dotnet_arguments_are_bounded_before_spawn(self):
        with mock.patch.object(LAUNCHER, 'dotnet', return_value=0) as spawn:
            with self.assertRaises(LAUNCHER.Refusal) as caught:
                LAUNCHER.shard_dotnet(['x' * 65536], time.monotonic() + 5, '/owned', lambda _: None)
            self.assertEqual('argv_bound', caught.exception.reason)
            spawn.assert_not_called()
            self.assertEqual(0, LAUNCHER.shard_dotnet(['--version'], time.monotonic() + 5,
                                                    '/owned', lambda _: None, expect_testhost=False))
            self.assertFalse(spawn.call_args.kwargs['expect_testhost'])

    def test_accounting_enables_only_cpu_and_memory_on_the_owned_unit(self):
        with tempfile.TemporaryDirectory() as tmp:
            group = Path(tmp); (group / 'cgroup.controllers').write_text('cpu memory pids\n')
            control = group / 'cgroup.subtree_control'; control.write_text('')
            original = Path.write_text; calls = []
            def kernel_write(path, value, **kwargs):
                self.assertEqual(control, path)
                self.assertEqual('+cpu +memory', value)
                calls.append(value)
                return original(path, 'cpu memory', **kwargs)
            with mock.patch.object(Path, 'write_text', kernel_write):
                LAUNCHER.enable_work_accounting(group)
            self.assertEqual(['+cpu +memory'], calls)
            (group / 'cgroup.controllers').write_text('pids\n'); calls.clear()
            with mock.patch.object(Path, 'write_text', kernel_write):
                with self.assertRaises(LAUNCHER.Refusal):
                    LAUNCHER.enable_work_accounting(group)
            self.assertEqual([], calls)

    def test_resource_receipt_cannot_invent_container_or_fixture_measurements(self):
        with tempfile.TemporaryDirectory() as tmp:
            work = Path(tmp)
            (work / 'cpu.stat').write_text('usage_usec 123\nuser_usec 100\nsystem_usec 23\n')
            peak = work / 'memory.peak'; peak.write_text('456\n')
            result = LAUNCHER.work_resources(work)
            self.assertEqual(456, result['peakMemoryBytes'])
            self.assertEqual(123, result['cpuUsageMicroseconds'])
            self.assertIsNone(result['dockerPeakMemoryBytes'])
            self.assertIsNone(result['fixtureStartupCount'])
            for value in ('unknown', '-1', '9' * 21):
                peak.write_text(value)
                with self.assertRaises(LAUNCHER.Refusal):
                    LAUNCHER.work_resources(work)

    def test_sdk_and_console_receipt_uses_the_queried_sdk_and_actual_bytes(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); sdk = root / 'sdk/10.0.302'; sdk.mkdir(parents=True)
            console = sdk / 'vstest.console.dll'; console.write_bytes(b'actual-console-fixture')
            deps = sdk / 'vstest.console.deps.json'
            deps.write_text(json.dumps({'libraries': {'vstest.console/18.6.0': {}}}))
            def query(argv, _deadline, _work, consume, **kwargs):
                self.assertEqual(['--version'], argv)
                self.assertFalse(kwargs['expect_testhost'])
                consume('10.0.302'); return 0
            with mock.patch.object(LAUNCHER, 'DOTNET', str(root / 'dotnet')), \
                    mock.patch.object(LAUNCHER, 'shard_dotnet', side_effect=query):
                result = LAUNCHER.runtime_toolchain(time.monotonic() + 5, '/owned')
                self.assertEqual('10.0.302', result['sdk'])
                self.assertEqual('18.6.0', result['console'])
                self.assertEqual(OWNER.hashlib.sha256(console.read_bytes()).hexdigest(), result['consoleSha256'])
                deps.write_text(json.dumps({'libraries': {'vstest.console/18.6.0': {}, 'vstest.console/18.5.0': {}}}))
                with self.assertRaises(LAUNCHER.Refusal) as caught:
                    LAUNCHER.runtime_toolchain(time.monotonic() + 5, '/owned')
                self.assertEqual('console_identity_invalid', caught.exception.reason)

    def test_vm_memory_samples_are_bounded_observations_not_a_container_peak(self):
        with tempfile.TemporaryDirectory() as tmp, mock.patch.object(LAUNCHER, 'PROC', tmp):
            path = Path(tmp) / 'meminfo'
            path.write_text('MemTotal:       4096 kB\nMemAvailable:   1024 kB\n')
            self.assertEqual(3072 * 1024, LAUNCHER.observe_vm_memory())
            for text in ('MemTotal: 1 kB\nMemAvailable: 2 kB\n', 'MemTotal: 1 kB\n',
                         'MemTotal: 1 kB\nMemTotal: 2 kB\nMemAvailable: 0 kB\n'):
                path.write_text(text)
                with self.assertRaises(LAUNCHER.Refusal):
                    LAUNCHER.observe_vm_memory()

    def test_leaf_accounting_refusal_publishes_closed_startup_failure_before_fork(self):
        with tempfile.TemporaryDirectory() as tmp, contextlib.ExitStack() as stack:
            root = Path(tmp); private = root / 'private'; private.mkdir(mode=0o700)
            group = root / 'cgroup/system.slice/owned.service'; group.mkdir(parents=True)
            (group / 'cgroup.controllers').write_text('pids\n')
            fork = mock.Mock(side_effect=AssertionError('No workload may start'))
            for name, value in {'identity': lambda _: {}, 'lower_core': lambda: None,
                'deadlines': lambda: (time.monotonic() + 60, time.monotonic() + 90),
                'locations': lambda *_: ('owned.service', private, root / 'output'),
                'cgroup_path': lambda _: '/system.slice/owned.service/supervisor',
                'CGROUP': str(root / 'cgroup')}.items():
                stack.enter_context(mock.patch.object(LAUNCHER, name, value))
            stack.enter_context(mock.patch.object(LAUNCHER.os, 'fork', fork))
            with self.assertRaises(LAUNCHER.Refusal) as caught:
                LAUNCHER.leaf('base')
            self.assertEqual('resource_controllers_unavailable', caught.exception.reason)
            self.assertEqual({'protocol': 1, 'reason': 'resource_controllers_unavailable'},
                             LAUNCHER.read_json(private / 'startup-failure.json'))
            self.assertFalse((private / 'ready.json').exists())
            fork.assert_not_called()

    def test_parent_detects_terminal_startup_without_consuming_execution_deadline(self):
        with tempfile.TemporaryDirectory() as tmp:
            private = Path(tmp)
            LAUNCHER.write_json(private / 'startup-failure.json', {'protocol': 1,
                                'reason': 'resource_controllers_unavailable'})
            with mock.patch.object(LAUNCHER, 'command', return_value=b''), \
                    mock.patch.object(LAUNCHER, 'unit_properties', return_value={
                        'ActiveState': 'failed', 'SubState': 'failed'}) as observed, \
                    mock.patch.object(LAUNCHER.time, 'sleep', side_effect=AssertionError('Must refuse before sleeping')):
                with self.assertRaises(LAUNCHER.Refusal) as caught:
                    LAUNCHER.launch_unit('base', {}, private, 'owned.service', time.monotonic() + 1000)
                self.assertEqual('unit_startup_resource_controllers_unavailable', caught.exception.reason)
                self.assertEqual(1, observed.call_count)
                self.assertFalse((private / 'launch').exists())
            for reason in ('PRIVATE-CANARY provider text', ['resource_controllers_unavailable']):
                LAUNCHER.write_json(private / 'startup-failure.json', {'protocol': 1, 'reason': reason})
                self.assertEqual('unit_startup_failed', LAUNCHER.startup_failure(private))

    def test_live_unready_startup_has_its_own_finite_budget(self):
        with tempfile.TemporaryDirectory() as tmp, \
                mock.patch.object(LAUNCHER, 'command', return_value=b''), \
                mock.patch.object(LAUNCHER, 'unit_properties', return_value={
                    'ActiveState': 'active', 'SubState': 'running'}), \
                mock.patch.object(LAUNCHER.time, 'sleep', return_value=None), \
                mock.patch.object(LAUNCHER.time, 'monotonic', side_effect=[0, 10, 20, 30]):
            with self.assertRaises(LAUNCHER.Refusal) as caught:
                LAUNCHER.launch_unit('base', {}, Path(tmp), 'owned.service', 1000)
            self.assertEqual('unit_startup_timeout', caught.exception.reason)


if __name__ == '__main__':
    unittest.main()
