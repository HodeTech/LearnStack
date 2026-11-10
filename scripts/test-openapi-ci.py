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
        # Finish fixture-owned maintenance before TemporaryDirectory removes it.
        self.git('config', 'maintenance.autoDetach', 'false')
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
                '--tool', str(self.root / 'tool'), '--output', str(self.root / 'reports')]), \
                patch.object(ci.subprocess, 'run', side_effect=run), \
                patch.object(ci.transition, 'execution_identity', return_value={}):
            # This fixture isolates verified Git selection from the comparator;
            # TransitionControls supplies actual input files and tool identities.
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

    def test_fixture_maintenance_finishes_before_the_commit_returns(self):
        with tempfile.TemporaryDirectory(prefix='learnstack-openapi-maintenance-') as temporary:
            evidence = Path(temporary)
            configuration = evidence / 'gitconfig'
            configuration.write_text(
                '[maintenance]\n autoDetach = true\n strategy = gc\n'
                '[maintenance "commit-graph"]\n enabled = true\n auto = -1\n'
                '[maintenance "gc"]\n enabled = false\n'
                '[gc]\n autoDetach = true\n'
                '[commit]\n gpgSign = false\n')
            trace = evidence / 'trace.jsonl'
            environment = {key: value for key, value in os.environ.items() if not key.startswith('GIT_')}
            environment.update(GIT_CONFIG_GLOBAL=str(configuration), GIT_CONFIG_NOSYSTEM='1',
                GIT_TRACE2_EVENT=str(trace))
            with patch.dict(os.environ, environment, clear=True):
                self.git('-c', 'core.hooksPath=/dev/null', 'commit', '--allow-empty', '-qm', 'Maintenance control')
            events = [json.loads(line) for line in trace.read_text().splitlines()]
            starts = [event for event in events if event.get('event') == 'start']
            parent = starts[0]['sid']
            maintenance = next(event for event in starts if 'maintenance' in event['argv'])
            graphs = {event['sid'] for event in starts if 'commit-graph' in event['argv']}
            self.assertEqual(1, len(graphs), 'Force real maintenance, not an idle no-op')
            parent_exit = next(index for index, event in enumerate(events)
                if event.get('event') == 'exit' and event['sid'] == parent)
            graph_exit = next(index for index, event in enumerate(events)
                if event.get('event') == 'exit' and event['sid'] in graphs)
            self.assertLess(graph_exit, parent_exit, 'Actual maintenance must finish before commit returns')
            self.assertEqual(0, events[graph_exit]['code'], 'The forced graph write must succeed')
            self.assertIn('--no-detach', maintenance['argv'])
            self.assertTrue((self.root / '.git/objects/info/commit-graphs/commit-graph-chain').is_file())

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




class TransitionControls(unittest.TestCase):
    """Synthetic Git/tool identities exercise admission, not actual tool qualification."""
    git = SnapshotControls.git
    save = SnapshotControls.save
    tearDown = SnapshotControls.tearDown

    def setUp(self):
        SnapshotControls.setUp(self)
        import copy
        self.copy = copy.deepcopy
        self.t = ci.transition
        self.real = Path(__file__).resolve().parents[1]
        for name in self.t.INPUTS:
            if name == self.t.TOOL:
                continue
            destination = self.root / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes((self.real / name).read_bytes())
        self.tool = self.root / 'tool'
        self.tool.write_bytes(b'Synthetic tool identity; never execute.\n')
        self.base = self.save(CURRENT)
        self.base_bytes = self.git('show', self.base + ':' + ci.SNAPSHOT).encode()
        revised = {**CURRENT, 'info': {**CURRENT['info'], 'title': 'Reviewed synthetic head'}}
        self.head_bytes = json.dumps(revised).encode()
        self.reports = {
            'raw': [{'id': 'synthetic-break', 'level': 3, 'path': '/api/v1/fixture', 'fingerprint': str(i)} for i in range(5)],
            'normalized': [{'id': 'synthetic-break', 'level': 3, 'path': '/api/v1/fixture', 'fingerprint': str(i)} for i in range(5)],
            'companion': [{'id': 'synthetic-companion', 'location': ['paths', str(i)]} for i in range(54)],
        }
        inputs = self.t.execution_identity(self.root, self.tool)
        self.record = {'schemaVersion': 1, 'adr': '0096', 'baseSha256': self.t.digest(self.base_bytes),
            'headSha256': self.t.digest(self.head_bytes),
            'findings': {k: {'count': len(v), 'sha256': self.t.digest(self.t.canonical_findings(v))} for k, v in self.reports.items()},
            'inputs': inputs}
        decision = (self.real / self.t.ADR).read_text()
        original = self.t.decision_tables(decision.encode())
        replacements = {original['Prior contract'][1]: self.record['baseSha256'],
                        original['Reviewed P03-5 contract'][1]: self.record['headSha256'],
                        original[self.t.TOOL][0]: inputs[self.t.TOOL]}
        for k, (label, _) in self.t.REPORTS.items():
            # Raw and normalized intentionally have the same complete finding identity.
            replacements[original[label][1]] = self.record['findings'][k]['sha256']
        for old, new in replacements.items():
            decision = decision.replace(old, new)
        self.decision = decision.encode()
        for name, data in [(self.t.ADR, self.decision), (self.t.CARRIER, json.dumps(self.record).encode())]:
            destination = self.root / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)
        self.head = self.save(revised)
        self.committed = {name: (self.root / name).read_bytes() for name in self.t.INPUTS if name != self.t.TOOL}
        self.committed.update({self.t.ADR: self.decision, self.t.CARRIER: json.dumps(self.record).encode()})
        self.paths = list(self.committed)
        self.comparison = self.root / 'isolated-comparison'
        self.write_comparison(self.comparison)
        self.before = self.t.execution_identity(self.root, self.tool)
        self.comparison_code = 1
        self.fixture_code = 0
        self.calls = []

    def write_json(self, path, value):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value))

    def write_metadata(self, output):
        severity = (self.root / 'backend/openapi/policy/severity.txt').read_bytes()
        self.write_json(output / 'policy.json', {'version': self.t.VERSION, 'severity': severity.decode(),
                                                'severitySha256': self.t.digest(severity)})

    def write_comparison(self, output):
        self.write_metadata(output)
        for name, (_, file) in self.t.REPORTS.items():
            self.write_json(output / file, self.reports[name])
        self.write_json(output / 'result.json', {'rawToolRefused': True, 'policyRefused': True})

    def write_controls(self, output):
        self.write_metadata(output)
        cases = json.loads((self.root / 'backend/openapi/policy/fixtures/cases.json').read_text())
        self.write_json(output / 'fixtures.json', [{'name': x['name'], 'breaking': x['breaking'], 'scope': x['scope']} for x in cases])

    def candidate(self):
        return self.t.candidate(self.root, self.committed.get, self.paths,
            self.base_bytes, self.head_bytes, self.tool, self.comparison, self.before)

    def process(self, command, **kwargs):
        if command[0] != sys.executable:
            return REAL_RUN(command, **kwargs)
        self.calls.append(command)
        output = Path(command[command.index('--output') + 1])
        if '--fixtures' in command:
            self.write_controls(output)
            return subprocess.CompletedProcess(command, self.fixture_code)
        self.write_comparison(output)
        return subprocess.CompletedProcess(command, self.comparison_code)

    def run_ci(self, *, name='reports', base=None, head=None):
        output = self.root / name
        with patch.object(sys, 'argv', ['openapi-ci.py', '--base', base or self.base, '--head', head or self.head,
                '--tool', str(self.tool), '--output', str(output)]), patch.object(ci.subprocess, 'run', side_effect=self.process):
            code = ci.main()
        return code, output

    def test_complete_verified_git_transition_preserves_raw_reports_and_records_separate_approval(self):
        code, output = self.run_ci()
        self.assertEqual(0, code)
        approval = json.loads((output / 'approval.json').read_text())
        self.assertEqual(('0096', 'approved-incompatibility', False, False),
                         (approval['adr'], approval['outcome'], approval['compatibility'], approval['runtimeBehaviorVerified']))
        self.assertEqual((self.base, self.head), (approval['baseCommit'], approval['headCommit']))
        self.assertTrue(json.loads((output / 'comparison/result.json').read_text())['policyRefused'])
        self.assertEqual(2, len(self.calls))
        self.assertNotIn('--fixtures', self.calls[0])
        self.assertIn('--fixtures', self.calls[1])

    def test_order_only_changes_are_allowed_but_no_finding_information_is_discarded(self):
        for name, (_, filename) in self.t.REPORTS.items():
            self.write_json(self.comparison / filename, [dict(reversed(list(x.items()))) for x in reversed(self.reports[name])])
        self.assertEqual(self.record, self.candidate())

    def test_same_pair_refuses_added_removed_changed_duplicated_or_extra_field_findings_in_every_channel(self):
        for name, (_, filename) in self.t.REPORTS.items():
            original = self.reports[name]
            variants = [original + [{'id': 'unreviewed'}], original[:-1], original + [original[0]],
                        [{**original[0], 'id': 'different'}, *original[1:]],
                        [{**original[0], 'extra': 'unreviewed'}, *original[1:]]]
            for value in variants:
                with self.subTest(channel=name, value=value[0]):
                    self.write_json(self.comparison / filename, value)
                    with self.assertRaises(ValueError):
                        self.candidate()
            self.write_json(self.comparison / filename, original)

    def test_missing_malformed_nonfinite_duplicate_key_or_nonlist_reports_refuse(self):
        for name, (_, filename) in self.t.REPORTS.items():
            path = self.comparison / filename
            for value in [b'{', b'null', b'{}', b'[{"id":1,"id":2}]', b'[{"level":NaN}]']:
                with self.subTest(channel=name, value=value):
                    path.write_bytes(value)
                    with self.assertRaises(ValueError):
                        self.candidate()
            path.unlink()
            with self.assertRaises(OSError):
                self.candidate()
            self.write_json(path, self.reports[name])

    def test_record_repointing_and_declared_identity_substitution_refuse(self):
        for key in ['baseSha256', 'headSha256', 'inputs', 'findings']:
            altered = self.copy(self.record)
            if key == 'inputs':
                altered[key][self.t.TOOL] = '0' * 64
            elif key == 'findings':
                altered[key]['companion']['sha256'] = '0' * 64
            else:
                changed = b'changed valid snapshot bytes'
                altered[key] = self.t.digest(changed)
                if key == 'baseSha256': self.base_bytes = changed
                else: self.head_bytes = changed
            with self.subTest(key=key):
                self.committed[self.t.CARRIER] = json.dumps(altered).encode()
                with self.assertRaises(ValueError):
                    self.candidate()
            self.base_bytes = self.git('show', self.base + ':' + ci.SNAPSHOT).encode()
            self.head_bytes = self.git('show', self.head + ':' + ci.SNAPSHOT).encode()
        self.committed[self.t.CARRIER] = json.dumps(self.record).encode()

    def test_nonaccepted_or_missing_decisions_and_duplicate_decision_rows_refuse(self):
        for state in ['Proposed', 'Superseded', 'Deprecated', 'Rejected']:
            with self.subTest(state=state):
                self.committed[self.t.ADR] = self.decision.replace(b'\nAccepted\n', ('\n' + state + '\n').encode(), 1)
                with self.assertRaises(ValueError):
                    self.candidate()
        self.committed[self.t.ADR] = self.decision.replace(b'# ADR-0096:', b'# ADR-9999:', 1)
        with self.assertRaises(ValueError):
            self.candidate()
        self.committed[self.t.ADR] = self.decision + ('\n| Raw tool | 5 | `' + self.record['findings']['raw']['sha256'] + '` |\n').encode()
        with self.assertRaises(ValueError):
            self.candidate()
        del self.committed[self.t.ADR]
        with self.assertRaises(ValueError):
            self.candidate()

    def test_carrier_schema_duplicate_keys_unknown_fields_and_extra_files_refuse(self):
        for value in [[], {}, {**self.record, 'unknown': True}, {**self.record, 'schemaVersion': True},
                      {**self.record, 'findings': {**self.record['findings'], 'raw': {'count': True, 'sha256': '0' * 64}}}]:
            self.committed[self.t.CARRIER] = json.dumps(value).encode()
            with self.assertRaises(ValueError):
                self.candidate()
        original = json.dumps(self.record).encode()
        self.committed[self.t.CARRIER] = original[:-1] + b',"adr":"0096"}'
        with self.assertRaises(ValueError):
            self.candidate()
        self.committed[self.t.CARRIER] = original
        self.paths.append('backend/openapi/policy/transition-0097.json')
        with self.assertRaises(ValueError):
            self.candidate()

    def test_changed_reversed_or_missing_carrier_never_approves(self):
        base, head = self.base_bytes, self.head_bytes
        for old, new in [(head, base), (base + b' ', head), (base, head + b' ')]:
            self.base_bytes, self.head_bytes = old, new
            self.assertIsNone(self.candidate())
        self.base_bytes, self.head_bytes = base, head
        del self.committed[self.t.CARRIER]
        self.assertIsNone(self.candidate())

    def test_changed_tool_policy_config_severity_or_fixture_sources_refuse(self):
        for name in self.t.INPUTS:
            path = self.tool if name == self.t.TOOL else self.root / name
            original = path.read_bytes()
            with self.subTest(input=name):
                path.write_bytes(original + b' ')
                with self.assertRaises(ValueError):
                    self.candidate()
            path.write_bytes(original)
        self.committed['scripts/openapi-policy.py'] += b' '
        with self.assertRaises(ValueError):
            self.candidate()

    def test_unavailable_identity_has_one_closed_reason_without_provider_text_or_paths(self):
        for failure in (OSError('PRIVATE-CANARY /private/input'),
                        ValueError('PRIVATE-CANARY invalid provider material')):
            with self.subTest(failure=type(failure).__name__), \
                    patch.object(self.t, 'file_bytes', side_effect=failure), \
                    self.assertRaises(ValueError) as caught:
                self.t.execution_identity(self.root, self.tool)
            self.assertEqual('ADR-0096 approval refused: execution identity unavailable', str(caught.exception))
            self.assertTrue(caught.exception.__suppress_context__)
        self.tool.unlink()
        with self.assertRaisesRegex(ValueError, '^ADR-0096 approval refused: execution identity unavailable$'):
            self.run_ci()
        self.assertFalse((self.root / 'reports/approval.json').exists())
        self.assertEqual([], self.calls, 'No comparison executes without identifiable inputs')

    def test_invalid_or_missing_refusal_and_policy_metadata_refuse(self):
        for value in [{'rawToolRefused': True, 'policyRefused': False}, {'rawToolRefused': 1, 'policyRefused': True}, {}]:
            self.write_json(self.comparison / 'result.json', value)
            with self.assertRaises(ValueError):
                self.candidate()
        self.write_comparison(self.comparison)
        self.write_json(self.comparison / 'policy.json', {'version': 'wrong', 'severity': '', 'severitySha256': '0' * 64})
        with self.assertRaises(ValueError):
            self.candidate()

    def test_control_failure_and_same_pair_comparison_failure_cannot_reuse_approval(self):
        for code in [2, -15]:
            self.comparison_code = code
            actual, output = self.run_ci(name='comparison-failed-' + str(code))
            self.assertEqual(code, actual)
            self.assertFalse((output / 'approval.json').exists())
            self.assertEqual(1, len(self.calls)); self.calls.clear()
        self.comparison_code = 1
        self.fixture_code = 2
        actual, output = self.run_ci(name='fixtures-failed')
        self.assertEqual(2, actual)
        self.assertFalse((output / 'approval.json').exists())

    def test_incomplete_duplicate_or_malformed_fresh_control_results_refuse(self):
        controls = self.root / 'fresh-controls'
        self.write_controls(controls)
        path = controls / 'fixtures.json'
        original = json.loads(path.read_text())
        for value in [None, [], original[:-1], [*original[:-1], original[0]],
                      [{**original[0], 'breaking': not original[0]['breaking']}, *original[1:]]]:
            self.write_json(path, value)
            with self.assertRaises(ValueError):
                self.t.validate_controls(self.root, self.tool, controls, self.record)
        path.unlink()
        with self.assertRaises(OSError):
            self.t.validate_controls(self.root, self.tool, controls, self.record)

    def test_bootstrap_cannot_approve_even_when_a_fixture_substitutes_the_known_bytes(self):
        with patch.object(ci, 'snapshot', side_effect=[None, self.head_bytes]), patch.object(ci, 'bootstrap', return_value=self.base_bytes):
            code, output = self.run_ci()
        self.assertEqual(1, code)
        self.assertFalse((output / 'approval.json').exists())
        self.assertEqual(1, len(self.calls))

    def test_failed_comparator_exit_one_with_partial_reports_is_not_completed_incompatibility(self):
        def partial(command, **kwargs):
            if command[0] != sys.executable:
                return REAL_RUN(command, **kwargs)
            output = Path(command[command.index('--output') + 1])
            self.write_metadata(output)
            self.write_json(output / 'raw-tool.json', self.reports['raw'])
            return subprocess.CompletedProcess(command, 1)
        with patch.object(self, 'process', side_effect=partial), self.assertRaises((OSError, ValueError)):
            self.run_ci()
        self.assertFalse((self.root / 'reports/approval.json').exists())

    def test_normal_success_still_uses_no_approval_and_output_must_be_fresh(self):
        self.comparison_code = 0
        code, output = self.run_ci(base=self.head)
        self.assertEqual(0, code)
        self.assertFalse((output / 'approval.json').exists())
        self.assertEqual(1, len(self.calls))
        with self.assertRaises(FileExistsError):
            self.run_ci(base=self.head)

    def test_exact_pair_cannot_claim_normal_success_without_the_reviewed_incompatibility(self):
        self.comparison_code = 0
        with self.assertRaises(ValueError):
            self.run_ci()
        self.assertFalse((self.root / 'reports/approval.json').exists())

    def test_direct_file_and_fixture_policy_modes_never_consume_transition_admission(self):
        spec = importlib.util.spec_from_file_location('unapproved_file_policy', self.real / 'scripts/openapi-policy.py')
        policy = importlib.util.module_from_spec(spec); spec.loader.exec_module(policy)
        for fixture in [False, True]:
            output = self.root / ('direct-fixture' if fixture else 'direct-file')
            arguments = ['policy', '--tool', str(self.tool), '--output', str(output)]
            arguments += ['--fixtures'] if fixture else ['--base', str(self.root / ci.SNAPSHOT), '--head', str(self.root / ci.SNAPSHOT)]
            with patch.object(sys, 'argv', arguments), patch.object(policy.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, self.t.VERSION)), \
                    patch.object(policy, 'fixtures'), patch.object(policy, 'compare', return_value=True), \
                    patch.object(self.t, 'candidate', side_effect=AssertionError('Forbidden admission path')):
                self.assertEqual(0 if fixture else 1, policy.main())
            self.assertFalse((output / 'approval.json').exists())


if __name__ == '__main__':
    unittest.main()
