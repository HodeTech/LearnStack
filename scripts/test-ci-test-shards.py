#!/usr/bin/env python3
"""Execute the actual ADR-0063 plan/selector owner; no host mutation or model copy."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
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


if __name__ == '__main__':
    unittest.main()
