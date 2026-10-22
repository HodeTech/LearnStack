#!/usr/bin/env python3
"""ADR-0063 attempt-specific API evidence, source topology and result validation.

Only the two aggregate jobs receive a read-only Actions token. ZIPs are fetched
by API artifact ID, hash-checked before bounded extraction into separate private
directories, and never supply commands, selectors or paths to the execution owner.
"""
import datetime
import hashlib
import importlib.util
import io
import json
import math
import os
from pathlib import Path
import re
import resource
import stat
import subprocess
import sys
import tempfile
import time
import zipfile

ROOT = Path(__file__).resolve().parents[1]
GH = '/usr/bin/gh'
RUBY = '/usr/bin/ruby'
MAX_ARCHIVE = 64 * 1024 * 1024
MAX_EXPANDED = 128 * 1024 * 1024
AGGREGATE_SECONDS = 5 * 60
UPLOAD_SECONDS = 60
REQUIRED = {
    'backend': 'backend (build + unit + arch + contract)',
    'backend-integration': 'backend integration (Testcontainers)',
    'frontend': 'frontend (typecheck + lint + build + test)',
    'secret-scan': 'secret scan (leakwatch)',
    'meta': 'meta (compose + commit hygiene + link audit)',
    'openapi-diff': 'openapi diff',
    'public-demo': 'public demo + lighthouse',
}
JOB_IDS = {'base': 'backend-base', 'native': 'backend-native',
           'architecture': 'backend-architecture', 'ordinary': 'backend-integration-tests'}
EXECUTE_STEP = 'Execute assigned cases under the bounded owner'
UPLOAD_STEP = 'Upload immutable attempt evidence'
BUILD_STEP = 'Build Release with warnings as errors'
UPLOAD_ACTION = 'actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02'
FIXTURE_IMAGES = ('quay.io/keycloak/keycloak:26.6.2', 'postgres:18.4-alpine')


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts' / filename)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


PLAN = module('ci_result_plan', 'ci-test-shards.py')
OWNER = module('ci_result_execution', 'ci-native-tests.py')
Refusal, require = PLAN.Refusal, PLAN.require


def prepare_fixture_images():
    # Pull outside fixture startup, while charging every second to the same
    # twenty-minute job. Never consume the execution owner's cleanup reserve.
    require(sys.platform == 'linux' and os.environ.get('GITHUB_ACTIONS') == 'true'
            and os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted'
            and os.environ.get('GITHUB_JOB') == 'backend-integration-tests', 'fixture_setup_context_invalid')
    started = os.environ.get('LEARNSTACK_CI_STARTED_AT', '')
    require(re.fullmatch(r'[1-9][0-9]*', started), 'fixture_setup_clock_missing')
    elapsed = time.time() - int(started)
    available = OWNER.JOB_SECONDS - OWNER.RESERVE_SECONDS - elapsed
    require(elapsed >= 0 and available > 0, 'fixture_setup_deadline_exhausted')
    beginning = time.monotonic()
    deadline = beginning + min(110, available)
    images = []
    for image in FIXTURE_IMAGES:
        image_started = time.monotonic()
        OWNER.command([OWNER.DOCKER, 'pull', image], deadline, 'fixture_image_pull_failed', cap=55)
        images.append({'image': image, 'pullSeconds': round(time.monotonic() - image_started, 3)})
    require(time.monotonic() < deadline, 'fixture_setup_deadline_exhausted')
    print(json.dumps({'protocol': 1, 'fixtureImagePreparationSeconds': round(time.monotonic() - beginning, 3),
                      'images': images}, sort_keys=True))
    return 0


def json_value(raw):
    try:
        return json.loads(raw, object_pairs_hook=PLAN.unique_object)
    except (ValueError, UnicodeError):
        raise Refusal('evidence_json_invalid') from None


def read_json(path):
    return json_value(OWNER.read_bytes(path))


def positive(value):
    return type(value) is int and value > 0


def timestamp(value):
    require(isinstance(value, str) and re.fullmatch(r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ', value),
            'api_timestamp_invalid')
    return datetime.datetime.strptime(value, '%Y-%m-%dT%H:%M:%SZ').replace(
        tzinfo=datetime.timezone.utc).timestamp()


def artifact_name(context, owner):
    return 'ci-proof-' + context['runId'] + '-' + context['runAttempt'] + '-' + owner


def producer_name(owner):
    return ('backend base tests and gates' if owner == 'base' else
            'backend native crash proof' if owner == 'native' else 'backend ' + owner)


# Psych is the runner's bundled Ruby standard library, not a new package or a
# partial YAML parser. Reject duplicate/complex keys, tags, aliases and anchors
# before safe_load can collapse or instantiate them. No Ruby input is evaluated.
YAML_READER = r'''
require 'yaml'
require 'json'
raw = STDIN.read(1048577)
raise 'bound' if raw.bytesize > 1048576
tree = Psych.parse_stream(raw)
count = 0
walk = lambda do |node|
  count += 1
  raise 'bound' if count > 50000
  raise 'alias' if node.is_a?(Psych::Nodes::Alias)
  raise 'tag' if node.respond_to?(:tag) && node.tag
  raise 'anchor' if node.respond_to?(:anchor) && node.anchor
  if node.is_a?(Psych::Nodes::Mapping)
    keys = node.children.each_slice(2).map do |key, value|
      raise 'key' unless key.is_a?(Psych::Nodes::Scalar)
      key.value
    end
    raise 'duplicate' unless keys.uniq.length == keys.length
  end
  (node.children || []).each { |child| walk.call(child) }
end
walk.call(tree)
raise 'documents' unless tree.children.length == 1
puts JSON.generate(YAML.safe_load(raw))
'''


def yaml_document(path):
    try:
        result = subprocess.run([RUBY, '-e', YAML_READER], input=OWNER.read_bytes(path, 1048576),
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=False, timeout=5,
            env={'PATH': '/usr/bin:/bin', 'LANG': 'C.UTF-8'})
    except (OSError, subprocess.TimeoutExpired):
        raise Refusal('workflow_parser_unavailable') from None
    require(result.returncode == 0 and len(result.stdout) <= 1048576, 'workflow_yaml_refused')
    value = json_value(result.stdout)
    require(isinstance(value, dict) and isinstance(value.get('jobs'), dict), 'workflow_shape_invalid')
    return value


def workflow_topology(documents, plan):
    require('.github/workflows/ci.yml' in documents, 'workflow_missing')
    workflow = documents['.github/workflows/ci.yml']
    jobs = workflow['jobs']
    require(workflow.get('permissions') == {'contents': 'read'}, 'workflow_permissions_invalid')
    require(set(jobs) == {*REQUIRED, *JOB_IDS.values()}, 'workflow_jobs_invalid')
    realm_node_gates(jobs['meta'])
    for scope, job_id in JOB_IDS.items():
        job = jobs[job_id]
        require(job.get('runs-on') == 'ubuntu-24.04' and job.get('timeout-minutes') == 20
                and not any(key in job for key in ('if', 'permissions', 'continue-on-error', 'container', 'services')),
                'execution_topology_invalid')
        if scope in ('architecture', 'ordinary'):
            require(job.get('strategy') == {'fail-fast': False, 'matrix': {
                'index': list(range(plan['counts'][scope]))}}, 'workflow_matrix_invalid')
            require(job.get('name') == 'backend ' + scope + '-${{ matrix.index }}', 'producer_name_invalid')
        else:
            require('strategy' not in job and job.get('name') == producer_name(scope), 'producer_name_invalid')
        steps = job.get('steps', [])
        require(steps and steps[0].get('name') == 'Start the whole-job clock before checkout'
                and steps[0].get('run') == 'echo "LEARNSTACK_CI_STARTED_AT=$(date +%s)" >> "$GITHUB_ENV"',
                'workflow_clock_invalid')
        names = [step.get('name') for step in steps]
        require(len(names) == len(set(names)) and all(names.count(name) == 1
                for name in (BUILD_STEP, EXECUTE_STEP, UPLOAD_STEP)), 'execution_gates_missing')
        if scope == 'ordinary':
            prepare_name = 'Prepare pinned provider fixture images within the whole-job budget'
            prepare = [step for step in steps if step.get('name') == prepare_name]
            require(len(prepare) == 1 and prepare[0].get('run') == 'python3 scripts/ci-test-shards.py prepare-fixtures'
                    and prepare[0].get('timeout-minutes') == 2 and 'if' not in prepare[0]
                    and names.index(prepare_name) < names.index(EXECUTE_STEP), 'fixture_image_gate_invalid')
        build, execute, upload = (steps[names.index(name)] for name in (BUILD_STEP, EXECUTE_STEP, UPLOAD_STEP))
        mode = 'native-shard' if scope == 'native' else scope
        mode += '-${{ matrix.index }}' if scope in ('architecture', 'ordinary') else ''
        require(build.get('run') == 'dotnet build LearnStack.slnx --no-restore --configuration Release --disable-build-servers'
                and build.get('working-directory') == 'backend' and build.get('env') == {'CI': 'true'}
                and 'if' not in build and 'if' not in execute
                and execute.get('run') == 'python3 scripts/ci-native-tests.py ' + mode
                and not any(step.get('continue-on-error') for step in steps), 'execution_gate_invalid')
        owner = 'native' if scope == 'native' else mode
        require(upload.get('uses') == UPLOAD_ACTION and upload.get('if') == 'always()'
                and upload.get('with', {}).get('name') == 'ci-proof-${{ github.run_id }}-${{ github.run_attempt }}-' + owner
                and upload.get('with', {}).get('if-no-files-found') == 'error', 'artifact_upload_invalid')
    for scope, job_id, needs in (
        ('backend', 'backend', ['backend-base', 'backend-architecture']),
        ('integration', 'backend-integration', ['backend-base', 'backend-integration-tests', 'backend-native'])):
        job = jobs[job_id]
        require(job.get('name') == REQUIRED[job_id] and job.get('if') == 'always()'
                and job.get('needs') == needs and job.get('timeout-minutes') == 5
                and job.get('permissions') == {'contents': 'read', 'actions': 'read'}
                and 'strategy' not in job and not job.get('continue-on-error'), 'aggregate_topology_invalid')
        calls = [step for step in job.get('steps', [])
                 if step.get('run') == 'python3 scripts/ci-test-shards.py aggregate-' + scope]
        require(len(calls) == 1 and calls[0].get('if') == 'always()'
                and calls[0].get('env', {}).get('GH_TOKEN') == '${{ github.token }}', 'aggregate_owner_missing')
    # Across all workflows, a second literal or computed use cannot shadow a
    # protected name. Only our two finite producer matrix names are computed.
    seen = []
    for path, document in documents.items():
        for job_id, job in document['jobs'].items():
            name = job.get('name', job_id)
            if path == '.github/workflows/ci.yml' and job_id in ('backend-architecture', 'backend-integration-tests'):
                continue
            require(isinstance(name, str) and '${{' not in name, 'unqualified_job_name')
            if name in REQUIRED.values():
                require(path == '.github/workflows/ci.yml' and REQUIRED.get(job_id) == name
                        and 'strategy' not in job, 'required_name_collision')
                seen.append(name)
    require(set(seen) == set(REQUIRED.values()) and len(seen) == 7, 'required_name_missing')
    return jobs


def realm_node_gates(job):
    """Keep the actual realm census and its counterexample owner mandatory."""
    require(not any(key in job for key in ('if', 'continue-on-error', 'needs')),
            'realm_node_gate_invalid')
    steps = job.get('steps', [])
    require(isinstance(steps, list) and all(isinstance(step, dict) for step in steps),
            'realm_node_gate_invalid')
    for name, command in (
        ('Development realm transport, ownership and convergence controls',
         'python3 scripts/run-realm-node-tests.py'),
        ('Realm Node executed-case and JUnit gate controls',
         'python3 scripts/test-realm-node-gate.py')):
        matches = [step for step in steps if step.get('name') == name or step.get('run') == command]
        require(len(matches) == 1 and matches[0].get('name') == name and matches[0].get('run') == command
                and not any(key in matches[0] for key in ('if', 'continue-on-error', 'uses')),
                'realm_node_gate_invalid')


def source_topology(plan):
    paths = sorted((ROOT / '.github/workflows').glob('*.y*ml'))
    require(0 < len(paths) <= 20, 'workflow_file_bound')
    return workflow_topology({path.relative_to(ROOT).as_posix(): yaml_document(path) for path in paths}, plan)


class Api:
    def __init__(self, deadline):
        self.deadline = deadline
        require(os.environ.get('GH_TOKEN') and os.environ.get('GITHUB_ACTIONS') == 'true', 'api_token_missing')

    def fetch(self, suffix, *, binary=False):
        require(re.fullmatch(r'actions/(runs/[1-9][0-9]*/attempts/[1-9][0-9]*(/jobs\?per_page=100&page=[1-9][0-9]*)?'
            r'|runs/[1-9][0-9]*/artifacts\?per_page=100&name=ci-proof-[A-Za-z0-9-]+'
            r'|artifacts/[1-9][0-9]*/zip)', suffix), 'api_path_refused')
        timeout = min(30, self.deadline - time.monotonic())
        require(timeout > 0, 'aggregate_deadline_exhausted')
        # A private file bounds stdout without retaining a token or provider
        # diagnostic. The existing GH_TOKEN is scoped by the aggregate job.
        with tempfile.TemporaryFile() as output:
            try:
                result = subprocess.run([GH, 'api', '--hostname', 'github.com',
                    'repos/HodeTech/LearnStack/' + suffix, '-H', 'X-GitHub-Api-Version: 2022-11-28'],
                    stdin=subprocess.DEVNULL, stdout=output, stderr=subprocess.DEVNULL,
                    timeout=timeout, check=False, env={'PATH': '/usr/bin:/bin', 'LANG': 'C.UTF-8',
                    'GH_TOKEN': os.environ['GH_TOKEN'], 'GH_CONFIG_DIR': '/nonexistent', 'GH_PROMPT_DISABLED': '1'},
                    preexec_fn=lambda: resource.setrlimit(resource.RLIMIT_FSIZE, (MAX_ARCHIVE, MAX_ARCHIVE)))
            except (OSError, subprocess.TimeoutExpired):
                raise Refusal('actions_api_unavailable') from None
            require(result.returncode == 0 and output.tell() <= MAX_ARCHIVE, 'actions_api_unavailable')
            output.seek(0)
            data = output.read(MAX_ARCHIVE + 1)
        require(time.monotonic() < self.deadline and len(data) <= MAX_ARCHIVE, 'aggregate_deadline_exhausted')
        return data if binary else json_value(data)

    def jobs(self, context):
        rows, total = [], None
        for page in range(1, 11):
            value = self.fetch('actions/runs/' + context['runId'] + '/attempts/' + context['runAttempt']
                               + '/jobs?per_page=100&page=' + str(page))
            require(isinstance(value, dict) and type(value.get('total_count')) is int
                    and isinstance(value.get('jobs'), list), 'jobs_api_shape_invalid')
            require(total in (None, value['total_count']) and 0 < value['total_count'] <= 1000,
                    'jobs_api_population_changed')
            total = value['total_count']
            rows.extend(value['jobs'])
            if len(rows) >= total:
                require(len(rows) == total, 'jobs_api_population_changed')
                return rows
        raise Refusal('jobs_api_bound')


def verify_run(run, context):
    repository = run.get('repository') if isinstance(run, dict) else None
    require(isinstance(run, dict) and run.get('id') == int(context['runId'])
            and run.get('run_attempt') == int(context['runAttempt'])
            and run.get('head_sha') == context['headSha'] and run.get('path') == '.github/workflows/ci.yml'
            and run.get('event') == context['eventName'] and run.get('name') == 'ci'
            and isinstance(repository, dict) and repository.get('full_name') == context['repository'],
            'run_api_identity_mismatch')
    if context['eventName'] == 'pull_request':
        pulls = run.get('pull_requests')
        number = re.fullmatch(r'HodeTech/LearnStack/\.github/workflows/ci\.yml@refs/pull/([1-9][0-9]*)/merge',
                              context['workflowRef'])
        # The REST pull-request projection tracks the current branch, not the
        # original event. run.head_sha is the immutable event identity; the raw
        # checked-out merge parent independently binds that to sourceSha.
        # GitHub omits this projection for fork PRs. Only the API's explicit
        # different fork repository permits that shape. OWNER.shard_context has
        # already bound the exact workflow ref and raw merge second parent;
        # run/head/attempt checks above remain mandatory for either projection.
        head_repository = run.get('head_repository')
        fork = (isinstance(head_repository, dict) and head_repository.get('fork') is True
                and isinstance(head_repository.get('full_name'), str)
                and re.fullmatch(r'[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}', head_repository['full_name'])
                and head_repository['full_name'].casefold() != context['repository'].casefold())
        require(number and isinstance(pulls, list) and (
            len(pulls) == 1 and isinstance(pulls[0], dict) and pulls[0].get('number') == int(number[1])
            or not pulls and fork), 'run_pull_identity_mismatch')


def verify_jobs(jobs, context, plan, scope):
    owners = ['base'] + ['architecture-' + str(i) for i in range(plan['counts']['architecture'])]
    if scope == 'integration':
        owners = ['base', 'native'] + ['ordinary-' + str(i) for i in range(plan['counts']['ordinary'])]
    require(scope in ('backend', 'integration') and isinstance(jobs, list), 'aggregate_scope_invalid')
    names = {producer_name(owner) for owner in owners}
    all_names = {*REQUIRED.values(), *names, producer_name('base'), producer_name('native'),
                 *('backend architecture-' + str(i) for i in range(plan['counts']['architecture'])),
                 *('backend ordinary-' + str(i) for i in range(plan['counts']['ordinary']))}
    ids, by_name = set(), {}
    for job in jobs:
        require(isinstance(job, dict) and positive(job.get('id')) and job['id'] not in ids
                and job.get('run_id') == int(context['runId'])
                and job.get('run_attempt') == int(context['runAttempt'])
                and job.get('head_sha') == context['headSha'] and job.get('name') in all_names,
                'job_api_identity_mismatch')
        ids.add(job['id'])
        require(job['name'] not in by_name, 'job_api_duplicate_producer')
        by_name[job['name']] = job
    require(names <= set(by_name), 'current_attempt_producer_missing')
    result = {}
    for owner in owners:
        job = by_name[producer_name(owner)]
        require(job.get('status') == 'completed' and job.get('conclusion') == 'success', 'producer_not_successful')
        start, end = timestamp(job.get('started_at')), timestamp(job.get('completed_at'))
        require(0 < end - start <= 20 * 60, 'producer_ceiling_exceeded')
        steps = job.get('steps', [])
        for name in (BUILD_STEP, EXECUTE_STEP, UPLOAD_STEP):
            matches = [step for step in steps if step.get('name') == name]
            successful = (len(matches) == 1 and matches[0].get('status') == 'completed'
                          and matches[0].get('conclusion') == 'success')
            if not successful:
                # Retain only finite gate/state words, never arbitrary API text.
                step = matches[0] if len(matches) == 1 else {}
                status, conclusion = step.get('status'), step.get('conclusion')
                print(json.dumps({'protocol': 1, 'diagnostic': 'producer_gate_refusal',
                    'owner': owner, 'gate': {BUILD_STEP: 'build', EXECUTE_STEP: 'execute', UPLOAD_STEP: 'upload'}[name],
                    'matches': 'one' if len(matches) == 1 else 'missing' if not matches else 'multiple',
                    'status': status if status in ('queued', 'in_progress', 'completed') else 'other',
                    'conclusion': conclusion if conclusion in ('success', 'failure', 'neutral', 'cancelled',
                        'skipped', 'timed_out', 'action_required', 'startup_failure', 'stale') else 'other'},
                    sort_keys=True), file=sys.stderr)
            require(successful, 'producer_gate_not_successful')
        result[owner] = job
    return result


def verify_artifact(metadata, context, owner, job):
    name = artifact_name(context, owner)
    require(isinstance(metadata, dict) and type(metadata.get('total_count')) is int
            and metadata['total_count'] >= 0 and isinstance(metadata.get('artifacts'), list)
            and len(metadata['artifacts']) == metadata['total_count'], 'artifact_api_invalid')
    # Re-running failed jobs can project old successful siblings into the new
    # jobs API response. Only an immutable artifact with this exact attempt's
    # name is evidence that the sibling actually executed this assignment.
    require(metadata['total_count'] != 0, 'artifact_api_missing')
    require(metadata['total_count'] == 1, 'artifact_api_not_unique')
    artifact = metadata['artifacts'][0]
    require(isinstance(artifact, dict) and positive(artifact.get('id'))
            and artifact.get('name') == name and artifact.get('expired') is False
            and positive(artifact.get('size_in_bytes')) and artifact['size_in_bytes'] <= MAX_ARCHIVE
            and isinstance(artifact.get('digest'), str) and re.fullmatch(r'sha256:[0-9a-f]{64}', artifact['digest']),
            'artifact_api_invalid')
    run = artifact.get('workflow_run', {})
    require(run.get('id') == int(context['runId']) and run.get('head_sha') == context['headSha'],
            'artifact_api_identity_mismatch')
    require(timestamp(job['started_at']) <= timestamp(artifact.get('created_at'))
            <= timestamp(artifact.get('updated_at')) <= timestamp(job['completed_at']), 'artifact_job_window_mismatch')
    return artifact


def unpack_archive(raw, artifact, output):
    require(len(raw) <= MAX_ARCHIVE and hashlib.sha256(raw).hexdigest() == artifact['digest'][7:],
            'artifact_api_digest_mismatch')
    allowed = {'result.json', 'discovery.json', 'assignment.json', 'measurements.json', 'resources.json',
               'distribution.json', 'replacementControls.json', 'environment.json', 'provisioning.json',
               'completed-cases.jsonl', 'native-refusals.jsonl'}
    try:
        with zipfile.ZipFile(io.BytesIO(raw)) as archive:
            entries = archive.infolist()
            require(0 < len(entries) <= 200 and len({entry.filename for entry in entries}) == len(entries)
                    and sum(entry.file_size for entry in entries) <= MAX_EXPANDED, 'artifact_zip_bound')
            for entry in entries:
                name = entry.filename
                require((name in allowed or re.fullmatch(r'results/[A-Za-z0-9_.-]{1,240}\.trx', name))
                        and not entry.is_dir() and not entry.flag_bits & 1
                        and stat.S_IFMT(entry.external_attr >> 16) in (0, stat.S_IFREG), 'artifact_zip_entry_refused')
                path = output / name
                path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
                with archive.open(entry) as source, path.open('xb') as target:
                    data = source.read(MAX_EXPANDED + 1)
                    require(len(data) == entry.file_size and len(data) <= MAX_EXPANDED, 'artifact_zip_bound')
                    target.write(data)
    except (zipfile.BadZipFile, RuntimeError, OSError):
        raise Refusal('artifact_zip_invalid') from None


def finite_seconds(value):
    return type(value) in (int, float) and math.isfinite(value) and 0 <= value <= 1200


def binary_identity(value, keys):
    require(isinstance(value, dict) and set(value) == set(keys), 'toolchain_identity_missing')
    require(all(isinstance(item, str) and re.fullmatch(r'[A-Za-z0-9_.+-]{1,150}', item)
                for key, item in value.items() if not key.casefold().endswith('sha256'))
            and all(isinstance(item, str) and re.fullmatch(r'[0-9a-f]{64}', item)
                    for key, item in value.items() if key.casefold().endswith('sha256')), 'toolchain_identity_invalid')


def verify_receipt(output, context, owner, plan, assignment):
    result = read_json(output / 'result.json')
    mode = 'native-shard' if owner == 'native' else owner
    scope = owner.split('-')[0]
    job_id = JOB_IDS.get(scope)
    require(isinstance(result, dict) and result.get('protocol') == 1 and result.get('identity') == context
            and result.get('partition') == mode and result.get('job') == job_id
            and all(result.get(key) is True for key in ('success', 'complete', 'ownershipComplete', 'cleanupOwnershipComplete'))
            and result.get('error') is None and result.get('forcedCleanup') is False, 'execution_receipt_incomplete')
    docker = scope in ('ordinary', 'native')
    require(result.get('dockerAbsent') is (True if docker else None)
            and result.get('authority') == {'process': 'owned_systemd_cgroup', 'docker': docker, 'collector': owner == 'native'},
            'execution_authority_mismatch')
    require(all(finite_seconds(result.get(key)) for key in ('preOwnerSeconds', 'setupSeconds', 'executionSeconds', 'cleanupSeconds'))
            and sum(result[key] for key in ('preOwnerSeconds', 'setupSeconds', 'executionSeconds', 'cleanupSeconds')) < 1200,
            'execution_intervals_invalid')
    require(positive(result.get('observedVmMemoryPeakBytes')) and positive(result.get('vmMemorySamples')),
            'vm_resource_evidence_missing')
    expected_files = {'discovery.json', 'assignment.json', 'measurements.json', 'resources.json', 'completed-cases.jsonl'}
    if owner == 'native':
        expected_files.update(('provisioning.json', 'native-refusals.jsonl'))
        provisioning = read_json(output / 'provisioning.json')
        require(provisioning.get('protocol') == 1 and provisioning.get('scope') == 'native_ci_setup_not_child_qualification'
                and provisioning.get('collectorAfter') == 'supported_file'
                and provisioning.get('softCore') == provisioning.get('hardCore') == 'zero'
                and provisioning.get('sourceSha') == context['sourceSha'] and provisioning.get('runId') == context['runId']
                and provisioning.get('runAttempt') == context['runAttempt'] and provisioning.get('job') == job_id,
                'provisioning_receipt_incomplete')
        require(OWNER.read_bytes(output / 'native-refusals.jsonl') == b''
                and all(isinstance(result.get(key), dict)
                        and set(result[key]) == {'verifiedImageDirectories'}
                        and type(result[key]['verifiedImageDirectories']) is int
                        and result[key]['verifiedImageDirectories'] >= 0
                        for key in ('crashInventoryBefore', 'crashInventoryAfter')), 'native_evidence_incomplete')
    if owner == 'base':
        expected_files.update(('distribution.json', 'replacementControls.json'))
    image = provisioning if owner == 'native' else result.get('runtime')
    require(isinstance(image, dict) and image.get('imageOS') == 'ubuntu24'
            and isinstance(image.get('imageVersion'), str)
            and re.fullmatch(r'[0-9]{8}\.[0-9]+\.[0-9]+', image['imageVersion'])
            and type(image.get('cpus')) is int and image['cpus'] > 1
            and isinstance(image.get('kernelRelease'), str)
            and re.fullmatch(r'[A-Za-z0-9._+-]{1,128}', image['kernelRelease'])
            and image.get('virtualization') in ('microsoft', 'kvm', 'qemu'), 'image_evidence_invalid')
    hashes = result.get('artifacts')
    require(isinstance(hashes, dict) and expected_files <= set(hashes)
            and hashes == OWNER.export_hashes(output), 'artifact_hash_mismatch')
    discovery = read_json(output / 'discovery.json')
    require(discovery.get('protocol') == 1 and discovery.get('identity') == context, 'discovery_identity_mismatch')
    manifest = discovery['population']
    require(PLAN.manifest_digest(manifest) == assignment['manifestDigest'], 'cross_job_discovery_mismatch')
    selected = assignment['owners'][owner]
    declared = read_json(output / 'assignment.json')
    require(declared.get('identity') == context and declared.get('owner') == owner
            and declared.get('variant') == assignment['variant'] and declared.get('counts') == plan['counts']
            and declared.get('planDigest') == assignment['planDigest'] and declared.get('manifestDigest') == assignment['manifestDigest']
            and declared.get('assignmentDigest') == PLAN.digest(assignment['owners'])
            and declared.get('unknownWeights') == [list(key) for key in assignment['unknownWeights']], 'assignment_identity_mismatch')
    PLAN.selected_equal(selected, declared.get('selected'))
    require(result.get('cases') == OWNER.validate_results(output, selected), 'result_count_mismatch')
    resources = read_json(output / 'resources.json')
    require(resources.get('scope') == 'owned_work_cgroup_excludes_docker_daemon'
            and all(type(resources.get(key)) is int and resources[key] >= 0 for key in (
                'peakMemoryBytes', 'cpuUsageMicroseconds', 'cpuUserMicroseconds', 'cpuSystemMicroseconds'))
            and resources['peakMemoryBytes'] > 0 and resources.get('dockerPeakMemoryBytes') is None
            and resources.get('fixtureStartupCount') is None, 'resource_evidence_invalid')
    measured = read_json(output / 'measurements.json')
    binary_identity(measured.get('toolchain'), ('sdk', 'console', 'consoleSha256', 'consoleDepsSha256'))
    for key in ('toolchainObservationSeconds', 'nonTestGateSeconds', 'discoveryAndSelectionSeconds',
                'resultExportAndValidationSeconds', 'workloadSeconds'):
        require(finite_seconds(measured.get(key)), 'measurement_intervals_invalid')
    require(measured.get('fixturesStarted') is None and measured.get('fixtureStartupSeconds') is None,
            'fixture_observation_invalid')
    projects = measured.get('projects')
    require(isinstance(projects, list) and len(projects) == len({case['assembly'] for case in selected})
            and len({item.get('assembly') for item in projects}) == len(projects), 'project_measurement_missing')
    adapters = {}
    for item in projects:
        assembly = item.get('assembly')
        require(item.get('cases') == len([case for case in selected if case['assembly'] == assembly])
                and positive(item['cases']) and finite_seconds(item.get('firstResultSeconds'))
                and finite_seconds(item.get('testProcessSeconds'))
                and item['firstResultSeconds'] <= item['testProcessSeconds']
                and item.get('failureLocations') == [], 'project_measurement_invalid')
        binary_identity(item.get('adapter'), ('assemblyVersion', 'sha256'))
        adapters[assembly] = item['adapter']
    gates = measured.get('nonTestGates')
    require(gates == ({'format': True, 'distribution': True, 'replacementControls': True} if owner == 'base' else {}),
            'base_gates_incomplete')
    return manifest, selected, {'toolchain': measured['toolchain'], 'adapters': adapters,
        'image': {key: image[key] for key in ('imageOS', 'imageVersion', 'cpus', 'kernelRelease', 'virtualization')}}


def validate_outputs(outputs, context, plan, scope):
    require(isinstance(outputs, dict) and 'base' in outputs, 'common_manifest_witness_missing')
    # Both protected results independently fetch the same uniquely named,
    # current-attempt immutable base artifact. Its full manifest/toolchain binds
    # the two otherwise disjoint scopes transitively across all twenty workers.
    manifest = read_json(outputs['base'] / 'discovery.json')['population']
    assignment = PLAN.assign(manifest, plan, context['runAttempt'])
    PLAN.different_layouts(manifest, plan)
    provided, identities, images = {}, {}, {}
    for owner, output in outputs.items():
        _, selected, identity = verify_receipt(output, context, owner, plan, assignment)
        if scope == 'backend' or owner != 'base':
            provided[owner] = selected
        require(not identities or identities['toolchain'] == identity['toolchain'], 'cross_job_toolchain_mismatch')
        identities['toolchain'] = identity['toolchain']
        # Each actual release is retained independently: equal labels or SDK
        # binaries do not attest equal weekly runner images.
        images[owner] = identity['image']
        for assembly, adapter in identity['adapters'].items():
            require(assembly not in identities or identities[assembly] == adapter, 'cross_job_adapter_mismatch')
            identities[assembly] = adapter
    PLAN.validate_union(manifest, plan, context['runAttempt'], provided, scope)
    return {'protocol': 1, 'identity': context, 'success': True, 'scope': scope,
            'variant': PLAN.variant(context['runAttempt']), 'planDigest': PLAN.digest(plan),
            'manifestDigest': assignment['manifestDigest'],
            'toolchain': identities, 'images': images, 'cases': sum(len(cases) for cases in provided.values())}


def aggregate(scope):
    # The process shares the same first-step clock as the five-minute job;
    # leave sixty seconds for the explicit report upload even on failure.
    started = os.environ.get('LEARNSTACK_CI_STARTED_AT', '')
    require(re.fullmatch(r'[1-9][0-9]*', started), 'aggregate_clock_missing')
    elapsed = time.time() - int(started)
    require(0 <= elapsed < AGGREGATE_SECONDS - UPLOAD_SECONDS, 'aggregate_deadline_exhausted')
    deadline = time.monotonic() + AGGREGATE_SECONDS - UPLOAD_SECONDS - elapsed
    job_id = 'backend' if scope == 'backend' else 'backend-integration'
    context = OWNER.shard_context(job_id)
    plan = PLAN.load_plan()
    source_topology(plan)
    api = Api(deadline)
    run = api.fetch('actions/runs/' + context['runId'] + '/attempts/' + context['runAttempt'])
    verify_run(run, context)
    jobs = verify_jobs(api.jobs(context), context, plan, scope)
    output = ROOT / 'artifacts' / ('ci-aggregate-' + scope)
    output.mkdir(parents=True, exist_ok=False)
    retained = {}
    with tempfile.TemporaryDirectory(prefix='learnstack-ci-aggregate-') as temporary:
        paths = {}
        for owner, job in jobs.items():
            require(time.monotonic() < deadline, 'aggregate_deadline_exhausted')
            metadata = api.fetch('actions/runs/' + context['runId'] + '/artifacts?per_page=100&name=' + artifact_name(context, owner))
            artifact = verify_artifact(metadata, context, owner, job)
            raw = api.fetch('actions/artifacts/' + str(artifact['id']) + '/zip', binary=True)
            path = Path(temporary) / owner
            path.mkdir(mode=0o700)
            unpack_archive(raw, artifact, path)
            paths[owner] = path
            retained[owner] = {'jobId': job['id'], 'artifactId': artifact['id'], 'digest': artifact['digest'],
                'jobWallSeconds': timestamp(job['completed_at']) - timestamp(job['started_at']),
                'queueSeconds': timestamp(job['started_at']) - timestamp(job['created_at'])}
        result = validate_outputs(paths, context, plan, scope)
        require(time.monotonic() < deadline, 'aggregate_deadline_exhausted')
    result['producers'] = retained
    result['aggregateSecondsBeforeUpload'] = round(time.time() - int(started), 3)
    OWNER.write_json(output / 'aggregate.json', result)
    print(json.dumps(result, sort_keys=True))
    return 0
