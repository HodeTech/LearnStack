#!/usr/bin/env python3
"""Planted controls against the real workflow/API/archive/aggregate owner."""
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile
import time
import unittest
from unittest import mock
import xml.etree.ElementTree as ET

from importlib.util import module_from_spec, spec_from_file_location
ROOT = Path(__file__).resolve().parents[1]


def module(name, filename):
    spec = spec_from_file_location(name, ROOT / 'scripts' / filename)
    result = module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


RESULTS = module('ci_results_controls', 'ci-test-shard-results.py')
PLAN = RESULTS.PLAN
FIXTURES = module('ci_plan_control_fixtures', 'test-ci-test-shards.py')


class AggregateControls(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.plan = PLAN.load_plan()
        cls.documents = {'.github/workflows/ci.yml': RESULTS.yaml_document(ROOT / '.github/workflows/ci.yml')}
        cls.fixture = FIXTURES.ShardControls()
        cls.fixture.plan = cls.plan

    def context(self, attempt='1'):
        return {'repository': 'HodeTech/LearnStack', 'sourceSha': 'a' * 40, 'headSha': 'b' * 40,
                'workflowSha': 'a' * 40, 'workflowRef': 'HodeTech/LearnStack/.github/workflows/ci.yml@refs/pull/32/merge',
                'eventName': 'pull_request', 'runId': '123', 'runAttempt': attempt}

    def refuses(self, reason, function, *args):
        with self.assertRaises((RESULTS.Refusal, RESULTS.OWNER.Refusal)) as caught:
            function(*args)
        self.assertEqual(reason, caught.exception.reason)

    def job(self, owner, n=1, context=None):
        context = context or self.context()
        return {'id': n, 'name': RESULTS.producer_name(owner), 'run_id': 123, 'run_attempt': int(context['runAttempt']),
                'head_sha': context['headSha'], 'status': 'completed', 'conclusion': 'success',
                'created_at': '2026-10-19T00:00:00Z', 'started_at': '2026-10-19T00:00:01Z',
                'completed_at': '2026-10-19T00:01:00Z', 'steps': [
                    {'name': name, 'status': 'completed', 'conclusion': 'success'}
                    for name in (RESULTS.BUILD_STEP, RESULTS.EXECUTE_STEP, RESULTS.UPLOAD_STEP)]}

    def jobs(self, scope, context=None):
        owners = ['base'] + ['architecture-' + str(i) for i in range(self.plan['counts']['architecture'])]
        if scope == 'integration':
            owners = ['native'] + ['ordinary-' + str(i) for i in range(self.plan['counts']['ordinary'])]
        return [self.job(owner, n + 1, context) for n, owner in enumerate(owners)]

    def metadata(self, owner='base', context=None):
        context = context or self.context()
        return {'total_count': 1, 'artifacts': [{'id': 12345, 'name': RESULTS.artifact_name(context, owner),
            'expired': False, 'digest': 'sha256:' + 'c' * 64, 'size_in_bytes': 123,
            'created_at': '2026-10-19T00:00:10Z', 'updated_at': '2026-10-19T00:00:50Z',
            'workflow_run': {'id': 123, 'head_sha': context['headSha']}}]}

    def write(self, output, name, value):
        RESULTS.OWNER.write_json(output / name, value)

    def outputs(self, temporary, scope, attempt='1'):
        context, manifest = self.context(attempt), self.fixture.manifest()
        assignment = PLAN.assign(manifest, self.plan, attempt)
        result = {}
        for owner, cases in assignment['owners'].items():
            if (owner == 'base' or owner.startswith('architecture-')) != (scope == 'backend'):
                continue
            path = Path(temporary) / owner; path.mkdir()
            (path / 'results').mkdir()
            self.write(path, 'discovery.json', {'protocol': 1, 'identity': context, 'population': manifest})
            self.write(path, 'assignment.json', {'identity': context, 'owner': owner, 'variant': assignment['variant'],
                'counts': self.plan['counts'], 'planDigest': assignment['planDigest'], 'manifestDigest': assignment['manifestDigest'],
                'assignmentDigest': PLAN.digest(assignment['owners']), 'unknownWeights': assignment['unknownWeights'], 'selected': cases})
            completed = []
            projects = []
            for assembly in sorted({case['assembly'] for case in cases}):
                selected = [case for case in cases if case['assembly'] == assembly]
                ns = RESULTS.OWNER.NS['t']; tag = lambda name: '{' + ns + '}' + name
                root = ET.Element(tag('TestRun'))
                definitions, rows = ET.SubElement(root, tag('TestDefinitions')), ET.SubElement(root, tag('Results'))
                for case in selected:
                    ET.SubElement(definitions, tag('UnitTest'), {'id': case['id'], 'storage': assembly + '.dll'})
                    row = {'assembly': assembly, 'id': case['id'], 'name': case['name'], 'outcome': 'Passed', 'duration': '00:00:00.001'}
                    completed.append(row)
                    ET.SubElement(rows, tag('UnitTestResult'), {'testId': case['id'], 'testName': case['name'],
                        'outcome': 'Passed', 'duration': row['duration']})
                summary = ET.SubElement(root, tag('ResultSummary'), {'outcome': 'Completed'})
                ET.SubElement(summary, tag('Counters'), {key: str(value) for key, value in {
                    'total': len(selected), 'executed': len(selected), 'passed': len(selected), 'failed': 0, 'notExecuted': 0}.items()})
                ET.ElementTree(root).write(path / 'results' / (assembly + '.trx'), encoding='utf-8')
                projects.append({'assembly': assembly, 'cases': len(selected), 'adapter': {
                    'assemblyVersion': '2.8.2.0', 'sha256': 'e' * 64}, 'firstResultSeconds': 0.1, 'testProcessSeconds': 1})
            (path / 'completed-cases.jsonl').write_text(''.join(json.dumps(row) + '\n' for row in completed))
            self.write(path, 'resources.json', {'scope': 'owned_work_cgroup_excludes_docker_daemon',
                'peakMemoryBytes': 1000000, 'cpuUsageMicroseconds': 200000, 'cpuUserMicroseconds': 150000,
                'cpuSystemMicroseconds': 50000, 'dockerPeakMemoryBytes': None, 'fixtureStartupCount': None})
            self.write(path, 'measurements.json', {'toolchain': {'sdk': '10.0.302', 'console': '18.6.0',
                'consoleSha256': 'd' * 64, 'consoleDepsSha256': 'f' * 64}, 'projects': projects,
                'fixturesStarted': None, 'fixtureStartupSeconds': None,
                **{key: 1 for key in ('toolchainObservationSeconds', 'nonTestGateSeconds', 'discoveryAndSelectionSeconds',
                    'resultExportAndValidationSeconds')}, 'workloadSeconds': 10,
                'nonTestGates': {'format': True, 'distribution': True, 'replacementControls': True} if owner == 'base' else {}})
            if owner == 'base':
                self.write(path, 'distribution.json', {'baseline': 'control'})
                self.write(path, 'replacementControls.json', {'baseline': 'control'})
            native = owner == 'native'; docker = native or owner.startswith('ordinary-')
            if native:
                self.write(path, 'provisioning.json', {'protocol': 1, 'scope': 'native_ci_setup_not_child_qualification',
                    'collectorAfter': 'supported_file', 'softCore': 'zero', 'hardCore': 'zero',
                    'sourceSha': context['sourceSha'], 'runId': '123', 'runAttempt': attempt, 'job': 'backend-native'})
                (path / 'native-refusals.jsonl').write_bytes(b'')
            receipt = {'protocol': 1, 'identity': context, 'partition': 'native-shard' if native else owner,
                'job': RESULTS.JOB_IDS[owner.split('-')[0]], 'success': True, 'complete': True,
                'ownershipComplete': True, 'cleanupOwnershipComplete': True, 'error': None, 'forcedCleanup': False,
                'dockerAbsent': True if docker else None, 'authority': {'process': 'owned_systemd_cgroup',
                    'docker': docker, 'collector': native}, 'cases': len(cases),
                'preOwnerSeconds': 100, 'setupSeconds': 3, 'executionSeconds': 20, 'cleanupSeconds': 2,
                'observedVmMemoryPeakBytes': 100000, 'vmMemorySamples': 10}
            if native:
                receipt.update({key: {'verifiedImageDirectories': 0} for key in ('crashInventoryBefore', 'crashInventoryAfter')})
            receipt['artifacts'] = RESULTS.OWNER.export_hashes(path)
            self.write(path, 'result.json', receipt)
            result[owner] = path
        return result

    def rehash(self, path):
        receipt = RESULTS.read_json(path / 'result.json')
        receipt['artifacts'] = RESULTS.OWNER.export_hashes(path)
        self.write(path, 'result.json', receipt)

    def test_real_current_workflow_topology_and_both_full_synthetic_unions(self):
        self.assertTrue(RESULTS.source_topology(self.plan))
        for scope in ('backend', 'integration'):
            for attempt in ('1', '2'):
                with tempfile.TemporaryDirectory() as temporary:
                    paths = self.outputs(temporary, scope, attempt)
                    result = RESULTS.validate_outputs(paths, self.context(attempt), self.plan, scope)
                    self.assertTrue(result['success'])
                    self.assertEqual('A' if attempt == '1' else 'B', result['variant'])
                    self.assertTrue(RESULTS.verify_jobs(self.jobs(scope, self.context(attempt)), self.context(attempt), self.plan, scope))

    def test_yaml_parser_refuses_duplicates_aliases_tags_multiple_documents_and_complex_keys(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'workflow.yml'
            for text in ('jobs: {}\njobs: {}', 'jobs: &j {}\ncopy: *j', 'jobs: !!ruby/object:X {}',
                         'jobs: {}\n---\njobs: {}', 'jobs:\n  ? [a, b]\n  : {}'):
                path.write_text(text)
                self.refuses('workflow_yaml_refused', RESULTS.yaml_document, path)
            path.write_text('jobs: {}')
            self.assertEqual({'jobs': {}}, RESULTS.yaml_document(path))

    def test_topology_rejects_wrong_counts_authority_gates_permissions_and_canonical_collisions(self):
        mutations = [
            ('workflow_matrix_invalid', lambda jobs: jobs['backend-architecture']['strategy']['matrix']['index'].pop()),
            ('execution_topology_invalid', lambda jobs: jobs['backend-base'].update(permissions={'actions': 'read'})),
            ('execution_topology_invalid', lambda jobs: jobs['backend-native'].update(container='ubuntu')),
            ('execution_gate_invalid', lambda jobs: next(step for step in jobs['backend-base']['steps']
                if step.get('name') == RESULTS.BUILD_STEP).update(run='true')),
            ('execution_gate_invalid', lambda jobs: next(step for step in jobs['backend-base']['steps']
                if step.get('name') == RESULTS.EXECUTE_STEP).update({'if': 'success()'})),
            ('aggregate_topology_invalid', lambda jobs: jobs['backend'].update({'if': 'success()'})),
            ('aggregate_topology_invalid', lambda jobs: jobs['backend-integration'].update(needs=['backend-native'])),
            ('aggregate_topology_invalid', lambda jobs: jobs['backend'].update({'timeout-minutes': 6})),
            ('producer_name_invalid', lambda jobs: jobs['backend-architecture'].update(name=RESULTS.REQUIRED['backend'])),
        ]
        for reason, mutate in mutations:
            documents = copy.deepcopy(self.documents)
            mutate(documents['.github/workflows/ci.yml']['jobs'])
            self.refuses(reason, RESULTS.workflow_topology, documents, self.plan)
        documents = copy.deepcopy(self.documents)
        documents['.github/workflows/other.yml'] = {'jobs': {'copy': {'name': RESULTS.REQUIRED['backend']}}}
        self.refuses('required_name_collision', RESULTS.workflow_topology, documents, self.plan)
        documents['.github/workflows/other.yml']['jobs']['copy']['name'] = '${{ matrix.name }}'
        self.refuses('unqualified_job_name', RESULTS.workflow_topology, documents, self.plan)

    def test_attempt_api_refuses_stale_missing_skipped_failed_duplicate_or_oversized_jobs(self):
        context = self.context('2'); clean = self.jobs('backend', context)
        self.assertTrue(RESULTS.verify_jobs(clean, context, self.plan, 'backend'))
        self.refuses('current_attempt_producer_missing', RESULTS.verify_jobs, clean[:-1], context, self.plan, 'backend')
        for key, value, reason in [('run_attempt', 1, 'job_api_identity_mismatch'),
            ('head_sha', 'a' * 40, 'job_api_identity_mismatch'), ('conclusion', 'skipped', 'producer_not_successful'),
            ('conclusion', 'failure', 'producer_not_successful'), ('status', 'in_progress', 'producer_not_successful'),
            ('completed_at', '2026-10-19T00:21:00Z', 'producer_ceiling_exceeded')]:
            dirty = copy.deepcopy(clean); dirty[0][key] = value
            self.refuses(reason, RESULTS.verify_jobs, dirty, context, self.plan, 'backend')
        dirty = copy.deepcopy(clean); dirty[0]['steps'][0]['conclusion'] = 'skipped'
        self.refuses('producer_gate_not_successful', RESULTS.verify_jobs, dirty, context, self.plan, 'backend')
        dirty = copy.deepcopy(clean); other = copy.deepcopy(dirty[0]); other['id'] = 100; dirty.append(other)
        self.refuses('job_api_duplicate_producer', RESULTS.verify_jobs, dirty, context, self.plan, 'backend')

    def test_run_head_and_actual_merge_checkout_are_distinct_and_related(self):
        context = self.context()
        run = {'id': 123, 'run_attempt': 1, 'head_sha': context['headSha'], 'path': '.github/workflows/ci.yml',
               'event': 'pull_request', 'name': 'ci', 'repository': {'full_name': 'HodeTech/LearnStack'},
               'pull_requests': [{'head': {'sha': context['headSha']}}]}
        RESULTS.verify_run(run, context)
        run['head_sha'] = context['sourceSha']
        self.refuses('run_api_identity_mismatch', RESULTS.verify_run, run, context)
        owner = RESULTS.OWNER
        with mock.patch.dict(os.environ, {'GITHUB_EVENT_NAME': 'pull_request', 'LEARNSTACK_CI_HEAD_SHA': context['headSha']}), \
                mock.patch.object(owner, 'command', return_value=(
                    'tree ' + 'c' * 40 + '\nparent ' + 'd' * 40 + '\nparent ' + context['headSha'] + '\n\nmessage').encode()) as command:
            self.assertEqual(context, owner.source_context({key: value for key, value in context.items() if key not in ('headSha', 'eventName')}))
            self.assertEqual(['cat-file', '-p', 'HEAD'], command.call_args.args[0][1:])

    def test_artifact_api_requires_unique_current_attempt_id_digest_and_job_window(self):
        context = self.context(); job = self.job('base'); clean = self.metadata()
        self.assertEqual(12345, RESULTS.verify_artifact(clean, context, 'base', job)['id'])
        for key, value, reason in [('expired', True, 'artifact_api_invalid'),
            ('digest', 'sha256:provider text', 'artifact_api_invalid'),
            ('name', 'ci-proof-123-0-base', 'artifact_api_invalid'),
            ('created_at', '2026-10-18T00:00:00Z', 'artifact_job_window_mismatch')]:
            dirty = copy.deepcopy(clean); dirty['artifacts'][0][key] = value
            self.refuses(reason, RESULTS.verify_artifact, dirty, context, 'base', job)
        dirty = copy.deepcopy(clean); dirty['artifacts'].append(dirty['artifacts'][0]); dirty['total_count'] = 2
        self.refuses('artifact_api_not_unique', RESULTS.verify_artifact, dirty, context, 'base', job)

    def test_archive_id_digest_and_allowlist_precede_parsing_without_extraction_fallback(self):
        import zipfile
        def archive(entries):
            stream = io.BytesIO()
            with zipfile.ZipFile(stream, 'w') as value:
                for name, raw in entries: value.writestr(name, raw)
            return stream.getvalue()
        clean = archive([('result.json', '{}'), ('results/a.trx', '<x/>')])
        with tempfile.TemporaryDirectory() as temporary:
            artifact = {'digest': 'sha256:' + hashlib.sha256(clean).hexdigest()}
            RESULTS.unpack_archive(clean, artifact, Path(temporary) / 'clean')
            self.refuses('artifact_api_digest_mismatch', RESULTS.unpack_archive, clean + b'changed', artifact, Path(temporary) / 'bad')
            for index, name in enumerate(('../escape', '/tmp/escape', 'results/../result.json', 'unit-private.log', 'results/a\\b.trx')):
                raw = archive([(name, '{}')]); artifact = {'digest': 'sha256:' + hashlib.sha256(raw).hexdigest()}
                self.refuses('artifact_zip_entry_refused', RESULTS.unpack_archive, raw, artifact, Path(temporary) / str(index))
            link = zipfile.ZipInfo('result.json'); link.create_system = 3; link.external_attr = (stat.S_IFLNK | 0o777) << 16
            raw = archive([(link, '/private')]); artifact = {'digest': 'sha256:' + hashlib.sha256(raw).hexdigest()}
            self.refuses('artifact_zip_entry_refused', RESULTS.unpack_archive, raw, artifact, Path(temporary) / 'link')

    def test_complete_receipts_refuse_omissions_wrong_assignment_and_forged_success(self):
        with tempfile.TemporaryDirectory() as temporary:
            outputs = self.outputs(temporary, 'backend'); base = outputs['base']; context = self.context()
            self.assertTrue(RESULTS.validate_outputs(outputs, context, self.plan, 'backend')['success'])
            self.refuses('aggregate_owners_incomplete', RESULTS.validate_outputs, {'base': base}, context, self.plan, 'backend')
            for key, value, reason in [('identity', self.context('2'), 'execution_receipt_incomplete'),
                ('forcedCleanup', True, 'execution_receipt_incomplete'), ('ownershipComplete', False, 'execution_receipt_incomplete'),
                ('cases', 999, 'result_count_mismatch'), ('vmMemorySamples', 0, 'vm_resource_evidence_missing')]:
                clean = RESULTS.read_json(base / 'result.json'); dirty = copy.deepcopy(clean); dirty[key] = value
                self.write(base, 'result.json', dirty)
                self.refuses(reason, RESULTS.validate_outputs, outputs, context, self.plan, 'backend')
                self.write(base, 'result.json', clean)
            clean = RESULTS.read_json(base / 'assignment.json'); dirty = copy.deepcopy(clean); dirty['selected'] = []
            self.write(base, 'assignment.json', dirty); self.rehash(base)
            self.refuses('selected_population_mismatch', RESULTS.validate_outputs, outputs, context, self.plan, 'backend')

    def test_actual_trx_and_completed_fields_are_rechecked_even_with_matching_hashes(self):
        for changed in ('completed', 'trx', 'missing'):
            with tempfile.TemporaryDirectory() as temporary:
                paths = self.outputs(temporary, 'backend'); base = paths['base']
                if changed == 'completed':
                    path = base / 'completed-cases.jsonl'; row = json.loads(path.read_text()); row['duration'] = '00:00:01'
                    path.write_text(json.dumps(row) + '\n'); reason = 'completed_evidence_incomplete'
                elif changed == 'trx':
                    path = next((base / 'results').glob('*.trx')); value = path.read_text().replace('outcome="Passed"', 'outcome="NotExecuted"')
                    path.write_text(value); reason = 'trx_counters_invalid'
                else:
                    (base / 'completed-cases.jsonl').write_bytes(b''); reason = 'completed_evidence_incomplete'
                self.rehash(base)
                self.refuses(reason, RESULTS.validate_outputs, paths, self.context(), self.plan, 'backend')

    def test_native_scope_provisioning_crash_witness_and_no_dump_refusals_remain_mandatory(self):
        with tempfile.TemporaryDirectory() as temporary:
            paths = self.outputs(temporary, 'integration'); native = paths['native']
            self.assertTrue(RESULTS.validate_outputs(paths, self.context(), self.plan, 'integration')['success'])
            (native / 'native-refusals.jsonl').write_text('{"error":"refused"}\n'); self.rehash(native)
            self.refuses('native_evidence_incomplete', RESULTS.validate_outputs, paths, self.context(), self.plan, 'integration')

    def test_toolchain_gate_and_resource_evidence_cannot_be_replaced_by_worker_hash_consensus(self):
        for mode in ('adapter', 'gates', 'resource', 'sdk'):
            with tempfile.TemporaryDirectory() as temporary:
                paths = self.outputs(temporary, 'backend'); base = paths['base']; reason = None
                if mode == 'resource':
                    data = RESULTS.read_json(base / 'resources.json'); data['dockerPeakMemoryBytes'] = 0
                    self.write(base, 'resources.json', data); reason = 'resource_evidence_invalid'
                else:
                    data = RESULTS.read_json(base / 'measurements.json')
                    if mode == 'adapter': data['projects'][0]['adapter']['sha256'] = 'not-a-hash'; reason = 'toolchain_identity_invalid'
                    if mode == 'gates': data['nonTestGates']['format'] = False; reason = 'base_gates_incomplete'
                    if mode == 'sdk': data['toolchain']['sdk'] = '10.0.999'; reason = 'cross_job_toolchain_mismatch'
                    self.write(base, 'measurements.json', data)
                self.rehash(base)
                self.refuses(reason, RESULTS.validate_outputs, paths, self.context(), self.plan, 'backend')

    def test_api_uses_fixed_endpoint_token_in_environment_and_finite_deadline(self):
        with mock.patch.dict(os.environ, {'GH_TOKEN': 'PRIVATE-CANARY', 'GITHUB_ACTIONS': 'true'}):
            api = RESULTS.Api(time.monotonic() + 20)
            def run(argv, **kwargs):
                self.assertNotIn('PRIVATE-CANARY', repr(argv)); self.assertNotIn('shell', kwargs)
                self.assertEqual('PRIVATE-CANARY', kwargs['env']['GH_TOKEN'])
                self.assertLessEqual(kwargs['timeout'], 20)
                kwargs['stdout'].write(b'{"id":123}')
                return subprocess.CompletedProcess(argv, 0)
            with mock.patch.object(RESULTS.subprocess, 'run', side_effect=run):
                self.assertEqual({'id': 123}, api.fetch('actions/runs/123/attempts/1'))
            self.refuses('api_path_refused', api.fetch, 'https://attacker.invalid/secret')
            with mock.patch.object(RESULTS.subprocess, 'run', side_effect=subprocess.TimeoutExpired('gh', 20)):
                self.refuses('actions_api_unavailable', api.fetch, 'actions/runs/123/attempts/1')

    def test_actual_aggregate_downloads_ids_checks_zip_and_exports_only_complete_current_attempt(self):
        import zipfile
        context = self.context()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); fixtures = root / 'fixtures'; fixtures.mkdir()
            paths = self.outputs(fixtures, 'backend')
            metadata, archives = {}, {}
            for index, (owner, path) in enumerate(paths.items()):
                stream = io.BytesIO()
                with zipfile.ZipFile(stream, 'w') as archive:
                    for file in path.rglob('*'):
                        if file.is_file(): archive.write(file, file.relative_to(path).as_posix())
                archives[1000 + index] = stream.getvalue()
                entry = self.metadata(owner)
                entry['artifacts'][0].update(id=1000 + index, size_in_bytes=len(stream.getvalue()),
                    digest='sha256:' + hashlib.sha256(stream.getvalue()).hexdigest())
                metadata[RESULTS.artifact_name(context, owner)] = entry
            source = root / '.github/workflows'; source.mkdir(parents=True)
            (source / 'ci.yml').write_bytes((ROOT / '.github/workflows/ci.yml').read_bytes())
            jobs = self.jobs('backend'); calls = []
            class FixtureApi(RESULTS.Api):
                def __init__(self, deadline): self.deadline = deadline
                def fetch(self, suffix, *, binary=False):
                    calls.append(suffix)
                    if '/jobs?' in suffix: return {'total_count': len(jobs), 'jobs': jobs}
                    if '/artifacts?' in suffix: return metadata[suffix.split('&name=', 1)[1]]
                    if suffix.endswith('/zip'): return archives[int(suffix.split('/')[-2])]
                    return {'id': 123, 'run_attempt': 1, 'head_sha': context['headSha'],
                        'path': '.github/workflows/ci.yml', 'event': 'pull_request', 'name': 'ci',
                        'repository': {'full_name': context['repository']},
                        'pull_requests': [{'head': {'sha': context['headSha']}}]}
            with mock.patch.object(RESULTS, 'ROOT', root), mock.patch.object(RESULTS, 'Api', FixtureApi), \
                    mock.patch.object(RESULTS.OWNER, 'shard_context', return_value=context), \
                    mock.patch.dict(os.environ, {'LEARNSTACK_CI_STARTED_AT': str(int(time.time()))}), \
                    mock.patch('sys.stdout', io.StringIO()):
                self.assertEqual(0, RESULTS.aggregate('backend'))
                receipt = RESULTS.read_json(root / 'artifacts/ci-aggregate-backend/aggregate.json')
                self.assertEqual(set(paths), set(receipt['producers']))
                self.assertEqual(len(paths), len([call for call in calls if call.endswith('/zip')]))
                self.assertTrue(all(item['artifactId'] in archives for item in receipt['producers'].values()))
                jobs[0]['conclusion'] = 'skipped'
                self.refuses('producer_not_successful', RESULTS.aggregate, 'backend')


if __name__ == '__main__':
    unittest.main()
