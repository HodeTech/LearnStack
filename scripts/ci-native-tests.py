#!/usr/bin/env python3
"""Closed ADR-0062 execution and aggregate owner. No command/path CLI overrides.

The delegated work cgroup is retained by a live supervisor sibling. RemainAfterExit
alone does not retain an empty cgroup: systemd prunes it on SERVICE_EXITED. Only
the supervisor's waitpid followed by an existing work/cgroup.events populated=0
is completion evidence; later unit disappearance is never that evidence.
"""
from collections import Counter
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import resource
import selectors
import signal
import stat
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

# Fixed platform seams. Offline controls replace only these assignments in a
# byte-verified disposable copy, never production arguments/environment overrides.
PROC = '/proc'
CGROUP = '/sys/fs/cgroup'
DOTNET = '/usr/share/dotnet/dotnet'
PYTHON = '/usr/bin/python3'
ENV = '/usr/bin/env'
SUDO = '/usr/bin/sudo'
SYSTEMD_RUN = '/usr/bin/systemd-run'
SYSTEMCTL = '/usr/bin/systemctl'
DOCKER = '/usr/bin/docker'
GIT = '/usr/bin/git'
FIND = '/usr/bin/find'
HOST_CRASH_ROOTS = ('/var/crash', '/var/lib/systemd/coredump', '/var/lib/docker', '/tmp', '/var/tmp')

# Sole duration owner. These are bounded path allocations, not a hosted fit
# claim. Qualification must measure setup, complete execution and failure tails.
JOB_SECONDS = 20 * 60
UPLOAD_SECONDS = 60
UNIT_STOP_SECONDS = 20
JOIN_SECONDS = 15
PROCESS_JOIN_SECONDS = 3
DOCKER_SECONDS = 45
SCAN_SECONDS = 20
EXPORT_SECONDS = 20
MARGIN_SECONDS = 20
COMMAND_SECONDS = 5
POLL_SECONDS = 0.1
UNIT_POLL_SECONDS = 0.5
TAIL_SECONDS = (UNIT_STOP_SECONDS + JOIN_SECONDS + PROCESS_JOIN_SECONDS + DOCKER_SECONDS
                + SCAN_SECONDS + EXPORT_SECONDS + MARGIN_SECONDS)
RESERVE_SECONDS = TAIL_SECONDS + UPLOAD_SECONDS
MAX_BYTES = 64 * 1024 * 1024
MAX_CASES = 100000
PROTOCOL = 1
ROOT = Path(__file__).resolve().parents[1]
JOBS = {'native': 'backend-native', 'ordinary': 'backend-integration-tests'}
OUTPUTS = {'native': 'backend-native', 'ordinary': 'backend-integration'}
FILTERS = {'all': 'Requires=Docker', 'native': 'Requires=Docker&NativeCrashProof=true',
           'ordinary': 'Requires=Docker&NativeCrashProof!=true'}
NATIVE_CLASSES = ('LearnStack.Tests.Integration.Database.HangfireFatalProcessTests',
                  'LearnStack.Tests.Integration.Database.HangfireGenerationPhaseTests')
CONTEXT_NAMES = ('RUN_ID', 'RUN_ATTEMPT', 'JOB', 'SHA', 'REPOSITORY',
                 'WORKFLOW_REF', 'WORKFLOW_SHA', 'WORKSPACE')
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
CANCELLED = False


class Refusal(Exception):
    def __init__(self, reason):
        self.reason = reason
        super().__init__(reason)


def require(value, reason):
    if not value:
        raise Refusal(reason)


def remaining(deadline, cap=None):
    value = deadline - time.monotonic()
    require(value > 0 and not CANCELLED, 'cancelled' if CANCELLED else 'deadline_exhausted')
    return min(value, cap) if cap is not None else value


def deadlines():
    value = os.environ.get('LEARNSTACK_CI_STARTED_AT', '')
    require(re.fullmatch(r'[1-9][0-9]*', value), 'job_clock_missing')
    elapsed = time.time() - int(value)
    require(0 <= elapsed < JOB_SECONDS, 'job_clock_invalid')
    end = time.monotonic() + JOB_SECONDS - elapsed
    return end - RESERVE_SECONDS, end - UPLOAD_SECONDS - MARGIN_SECONDS


def read_bytes(path, limit=MAX_BYTES):
    try:
        with Path(path).open('rb') as stream:
            value = stream.read(limit + 1)
        require(len(value) <= limit, 'input_bound')
        return value
    except OSError:
        raise Refusal('input_unreadable') from None


def read_json(path):
    try:
        return json.loads(read_bytes(path))
    except (ValueError, UnicodeError):
        raise Refusal('invalid_json') from None


def write_json(path, value):
    path = Path(path)
    temporary = path.with_name(path.name + '.tmp')
    with temporary.open('x', encoding='utf-8') as stream:
        os.chmod(temporary, 0o600)
        json.dump(value, stream, sort_keys=True, separators=(',', ':'))
        stream.write('\n')
    temporary.replace(path)


def command(argv, deadline, reason, *, cap=COMMAND_SECONDS):
    try:
        result = subprocess.run(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                stderr=subprocess.DEVNULL, timeout=remaining(deadline, cap),
                                env={'PATH': '/usr/bin:/bin', 'LANG': 'C.UTF-8',
                                     'LC_ALL': 'C.UTF-8', 'HOME': os.environ.get('HOME', ''),
                                     'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null'},
                                check=False)
    except (OSError, subprocess.TimeoutExpired):
        raise Refusal(reason) from None
    require(result.returncode == 0 and len(result.stdout) <= MAX_BYTES, reason)
    return result.stdout


def identity(mode):
    env = os.environ
    require(mode in (*JOBS, 'aggregate'), 'mode_refused')
    require(sys.platform == 'linux' and env.get('GITHUB_ACTIONS') == 'true', 'unsupported_context')
    expected_job = JOBS.get(mode, 'backend-integration')
    require(env.get('GITHUB_JOB') == expected_job, 'job_identity_mismatch')
    for name in CONTEXT_NAMES:
        copy_name = 'ATTEMPT' if name == 'RUN_ATTEMPT' else name
        require(env.get('GITHUB_' + name)
                and env.get('LEARNSTACK_NATIVE_' + copy_name) == env['GITHUB_' + name],
                'context_identity_mismatch')
    require(env['GITHUB_REPOSITORY'] == 'HodeTech/LearnStack'
            and re.fullmatch(r'[0-9a-f]{40}', env['GITHUB_SHA'])
            and env['GITHUB_WORKFLOW_SHA'] == env['GITHUB_SHA']
            and re.fullmatch(r'[1-9][0-9]*', env['GITHUB_RUN_ID'])
            and re.fullmatch(r'[1-9][0-9]*', env['GITHUB_RUN_ATTEMPT']), 'source_identity_mismatch')
    require(re.fullmatch(r'HodeTech/LearnStack/\.github/workflows/ci\.yml@refs/'
                         r'(heads/(main|development)|pull/[1-9][0-9]*/merge)',
                         env['GITHUB_WORKFLOW_REF']), 'workflow_identity_mismatch')
    require(Path(env['GITHUB_WORKSPACE']).resolve() == ROOT and Path.cwd().resolve() == ROOT,
            'checkout_mismatch')
    require(command([GIT, 'rev-parse', '--verify', 'HEAD'], time.monotonic() + COMMAND_SECONDS,
                    'checkout_mismatch').decode().strip() == env['GITHUB_SHA'], 'checkout_mismatch')
    if mode != 'aggregate':
        require(os.getuid() > 0 and os.getgid() > 0 and os.geteuid() == os.getuid(), 'nonroot_required')
        require(env.get('LEARNSTACK_NATIVE_RUNNER_CONTEXT') == 'github-hosted'
                and env.get('LEARNSTACK_NATIVE_RUNNER_OS') == env.get('RUNNER_OS') == 'Linux'
                and env.get('LEARNSTACK_NATIVE_RUNNER_ARCH') == env.get('RUNNER_ARCH') == 'X64'
                and env.get('LEARNSTACK_NATIVE_RUNNER_LABEL') ==
                ('ubuntu-24.04' if mode == 'native' else 'ubuntu-latest'), 'unsupported_context')
        try:
            require(json.loads(env.get('LEARNSTACK_NATIVE_JOB_CONTAINER', '')) in (None, {})
                    and json.loads(env.get('LEARNSTACK_NATIVE_JOB_SERVICES', '')) == {},
                    'unsupported_job_topology')
        except ValueError:
            raise Refusal('unsupported_job_topology') from None
    return {'sourceSha': env['GITHUB_SHA'], 'runId': env['GITHUB_RUN_ID'],
            'runAttempt': env['GITHUB_RUN_ATTEMPT'], 'repository': env['GITHUB_REPOSITORY'],
            'workflowRef': env['GITHUB_WORKFLOW_REF'], 'workflowSha': env['GITHUB_WORKFLOW_SHA']}


def locations(mode, context):
    unit = 'learnstack-ci-' + context['runId'] + '-' + context['runAttempt'] + '-' + mode + '.service'
    temporary = Path(os.environ.get('RUNNER_TEMP', ''))
    require(temporary.is_absolute() and temporary.is_dir() and not temporary.is_symlink(), 'runner_temp_invalid')
    private = temporary / unit.removesuffix('.service')
    return unit, private, ROOT / 'artifacts' / OUTPUTS[mode]


def lower_core():
    try:
        resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
        require(resource.getrlimit(resource.RLIMIT_CORE) == (0, 0), 'core_limits_not_zero')
    except (OSError, ValueError):
        raise Refusal('core_limits_not_zero') from None


def cgroup_path(pid):
    try:
        value = read_bytes(Path(PROC) / str(pid) / 'cgroup', 4096).decode('ascii')
    except UnicodeError:
        raise Refusal('cgroup_unreadable') from None
    require(re.fullmatch(r'0::/[A-Za-z0-9_./:@-]*\n', value), 'cgroup_v2_required')
    return value[3:].strip()


def populated(path):
    try:
        fields = dict(line.split() for line in read_bytes(path / 'cgroup.events', 4096).decode().splitlines())
    except (ValueError, UnicodeError):
        raise Refusal('cgroup_events_invalid') from None
    require(fields.get('populated') in ('0', '1'), 'cgroup_events_invalid')
    return fields['populated'] == '1'


def testhost_membership(expected):
    count = 0
    for entry in Path(PROC).iterdir():
        if not entry.name.isdecimal():
            continue
        try:
            value = (entry / 'cmdline').read_bytes()
            if not any(Path(part.decode('utf-8', errors='replace')).name in ('testhost.dll', 'testhost')
                       for part in value.split(b'\0') if part):
                continue
            actual = cgroup_path(entry.name)
        except FileNotFoundError:
            continue
        except PermissionError:
            raise Refusal('testhost_unreadable') from None
        except Refusal:
            if not entry.exists():
                continue
            raise
        require(actual == expected or actual.startswith(expected + '/'), 'testhost_outside_owned_subtree')
        count += 1
    return count


def test_environment():
    env = {name: os.environ[name] for name in ('PATH', 'HOME', 'RUNNER_TEMP', 'DOTNET_ROOT', 'PNPM_HOME')
           if name in os.environ}
    env.update({'CI': 'true', 'LANG': 'C.UTF-8', 'LC_ALL': 'C.UTF-8', 'DOTNET_CLI_UI_LANGUAGE': 'en',
                'DOTNET_NOLOGO': '1', 'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
                'DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER': '1', 'DOTNET_CLI_USE_MSBUILD_SERVER': '0',
                'MSBUILDDISABLENODEREUSE': '1'})
    return env


def dotnet(argv, deadline, work, consume):
    """Wait the actual child, retaining completed lines before a final TRX exists."""
    require(testhost_membership(work) == 0, 'preexisting_testhost')
    process = subprocess.Popen([DOTNET, *argv], cwd=ROOT / 'backend', env=test_environment(),
                               stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    selector = selectors.DefaultSelector()
    selector.register(process.stdout, selectors.EVENT_READ)
    pending = b''
    seen_testhost = False
    try:
        while selector.get_map():
            remaining(deadline)
            seen_testhost |= testhost_membership(work) > 0
            for key, _ in selector.select(POLL_SECONDS):
                chunk = os.read(key.fileobj.fileno(), 65536)
                if not chunk:
                    selector.unregister(key.fileobj)
                    continue
                pending += chunk
                require(len(pending) <= MAX_BYTES, 'console_bound')
                while b'\n' in pending:
                    line, pending = pending.split(b'\n', 1)
                    consume(line.decode('utf-8', errors='replace').rstrip('\r'))
        if pending:
            consume(pending.decode('utf-8', errors='replace'))
        status = process.wait(timeout=remaining(deadline))
        require(seen_testhost, 'actual_testhost_not_observed')
        return status
    except BaseException:
        # A refusal is already RED. Join this actual launcher after bounded
        # termination; its other descendants remain owned by the work cgroup.
        if process.poll() is None:
            stop = time.monotonic() + PROCESS_JOIN_SECONDS
            process.terminate()
            try:
                process.wait(timeout=PROCESS_JOIN_SECONDS / 2)
            except subprocess.TimeoutExpired:
                process.kill()
                try:
                    process.wait(timeout=max(0.001, stop - time.monotonic()))
                except subprocess.TimeoutExpired:
                    raise Refusal('launcher_join_failed') from None
        raise
    finally:
        selector.close()
        process.stdout.close()
        # Any remaining descendants belong to work; the supervisor accounts for
        # the entire hierarchy rather than assuming this leader's exit is enough.


def test_projects():
    solution = ET.parse(ROOT / 'backend/LearnStack.slnx').getroot()
    projects = []
    paths = set()
    for item in solution.iter('Project'):
        relative = item.get('Path', '')
        require(relative and not Path(relative).is_absolute() and '..' not in Path(relative).parts,
                'solution_project_invalid')
        path = ROOT / 'backend' / relative
        paths.add(path.resolve())
        project = ET.parse(path).getroot()
        flags = list(project.iter('IsTestProject'))
        if path.stem.startswith('LearnStack.Tests.'):
            require(len(flags) == 1 and not flags[0].attrib and flags[0].text in ('true', 'false'),
                    'test_project_classification_missing')
        if any(value.text == 'true' for value in flags):
            require(path.stem.startswith('LearnStack.Tests.') and path.stem not in projects,
                    'solution_project_invalid')
            projects.append(path.stem)
            yield relative, path.stem
    require(projects, 'empty_test_solution')
    require(all(path.resolve() in paths for path in (ROOT / 'backend/tests').rglob('*.csproj')
                if 'obj' not in path.parts and 'bin' not in path.parts), 'test_project_missing_from_solution')


def case_identity(raw, assembly):
    require(isinstance(raw, dict), 'discovery_case_invalid')
    name, test_id = raw.get('DisplayName'), raw.get('Id')
    require(isinstance(name, str) and 0 < len(name) <= 8192
            and not any(ord(character) < 32 for character in name), 'discovery_case_invalid')
    require(isinstance(test_id, str) and re.fullmatch(
        r'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}', test_id),
        'discovery_case_invalid')
    require(Path(raw.get('Source', '')).name == assembly + '.dll', 'discovery_source_invalid')
    return {'assembly': assembly.casefold(), 'id': test_id, 'name': name}


def discovery_log(path, assembly):
    marker = 'TestRequestSender.OnDiscoveryMessageReceived: Received message: '
    cases, completed = [], None
    for line in read_bytes(path).decode('utf-8').splitlines():
        if marker not in line:
            continue
        message = json.loads(line.split(marker, 1)[1])
        kind, payload = message.get('MessageType'), message.get('Payload')
        if kind == 'TestDiscovery.TestFound':
            require(completed is None and isinstance(payload, list), 'discovery_protocol_invalid')
            cases.extend(case_identity(case, assembly) for case in payload)
        elif kind == 'TestDiscovery.Completed':
            require(completed is None and isinstance(payload, dict), 'discovery_protocol_invalid')
            completed = payload
            cases.extend(case_identity(case, assembly) for case in payload.get('LastDiscoveredTests') or [])
    require(completed is not None and completed.get('IsAborted') is False
            and completed.get('TotalTests') == len(cases) and len(cases) <= MAX_CASES,
            'discovery_incomplete')
    require([Path(value).name for value in completed.get('FullyDiscoveredSources', [])] == [assembly + '.dll']
            and all(completed.get(key) == [] for key in
                    ('PartiallyDiscoveredSources', 'NotDiscoveredSources', 'SkippedDiscoverySources')),
            'discovery_sources_incomplete')
    require(len({case['id'] for case in cases}) == len(cases), 'duplicate_discovery_identity')
    return sorted(cases, key=lambda case: (case['assembly'], case['id']))


def key(case):
    return case['assembly'], case['id'], case['name']


def validate_partition(manifest):
    require(set(manifest) == {'all', 'native', 'ordinary', 'assemblies'}, 'partition_shape_invalid')
    require(manifest['assemblies'] and len(set(manifest['assemblies'])) == len(manifest['assemblies']),
            'discovery_assemblies_invalid')
    assemblies = {name.casefold() for name in manifest['assemblies']}
    populations = {part: {key(case) for case in manifest[part]} for part in FILTERS}
    require(all(len(populations[part]) == len(manifest[part]) for part in FILTERS),
            'duplicate_discovery_identity')
    require(all(case['assembly'] in assemblies for part in FILTERS for case in manifest[part]),
            'discovery_assembly_unowned')
    require(populations['native'] and populations['ordinary']
            and not populations['native'] & populations['ordinary']
            and populations['all'] == populations['native'] | populations['ordinary'], 'partition_incomplete')
    actual_classes = set()
    for case in manifest['native']:
        matches = [name for name in NATIVE_CLASSES if case['name'].startswith(name + '.')]
        require(len(matches) == 1, 'native_class_unclassified')
        actual_classes.update(matches)
    require(actual_classes == set(NATIVE_CLASSES), 'native_class_missing')


def discover(private, deadline, work):
    manifest = {part: [] for part in FILTERS}
    manifest['assemblies'] = []
    for project, assembly in test_projects():
        manifest['assemblies'].append(assembly)
        for part, expression in FILTERS.items():
            diagnostic = private / (assembly + '-' + part + '.diag')
            names = []
            def line(value):
                if value.startswith('    '):
                    names.append(value[4:])
            status = dotnet(['test', project, '--configuration', 'Release', '--no-build', '--no-restore',
                             '--disable-build-servers', '--list-tests', '--filter', expression,
                             '--diag', str(diagnostic)], deadline, work, line)
            require(status == 0, 'discovery_command_failed')
            cases = discovery_log(diagnostic, assembly)
            require(Counter(case['name'] for case in cases) == Counter(names), 'discovery_console_mismatch')
            manifest[part].extend(cases)
            # Diagnostic files contain serialized theory inputs. No raw file is
            # copied to artifacts or retained after its identity extraction.
            for candidate in private.glob(diagnostic.stem + '*'):
                candidate.unlink()
    validate_partition(manifest)
    return manifest


COMPLETED = re.compile(r'^  (Passed|Failed|Skipped) (.+) \[((?:< )?[0-9]+(?:\.[0-9]+)? (?:ms|s|m|h)|[^\[\]]+)\]$')
PREFLIGHT_REFUSALS = (
    'unknown', 'argument_refused', 'collector_unreadable', 'collector_pipe', 'collector_unsupported',
    'soft_core_not_zero', 'hard_core_not_zero', 'soft_core_refused', 'hard_core_refused',
    'limits_unreadable', 'scratch_refused', 'managed_preflight_refused', 'probe_failed',
    'benign_control_failed', 'benign_artifact_refused', 'receipt_bound', 'container_start')
PREFLIGHT_PATTERN = ('(?:' + '|'.join(PREFLIGHT_REFUSALS)
    + '|phase_(?:envelope|child|twin|fault|rows|receipt)_'
      '(?:authentication|privilege|table|postgres|npgsql|io|json|timeout|canceled|argument|invalid_operation|other))')
TOPOLOGY_PATTERN = (
    'supervisor_topology_membership_internal_(?:yes|no)_members_[0-3]_database_(?:yes|no)'
    '_supervisor_(?:yes|no)_running_(?:yes|no)_oom_(?:yes|no)'
    '_exit_(?:absent|zero|abort|refused|failed|other)_preflight_' + PREFLIGHT_PATTERN)


def native_refusal(line, stream, seen):
    match = re.search(r'The isolated (?:generation phase fixture|Linux fatal probe) refused: ([a-z0-9_]+)', line)
    if not match:
        return
    reason = match.group(1)
    # Only the finite pre-private-input facts and existing preflight categories
    # survive. Exception/provider text and arbitrary new stage names do not.
    allowed = (re.fullmatch(PREFLIGHT_PATTERN, reason)
               or re.fullmatch(TOPOLOGY_PATTERN, reason)
               or re.fullmatch('supervisor_preflight_' + PREFLIGHT_PATTERN, reason))
    if allowed and reason not in seen:
        seen.add(reason)
        stream.write(json.dumps({'reason': reason}, sort_keys=True) + '\n')
        stream.flush()


def completed_line(line, expected, seen, stream):
    match = COMPLETED.fullmatch(line)
    if not match:
        return
    outcome, name, duration = match.groups()
    require(name in expected and seen[name] < expected[name], 'completed_case_unexpected')
    require(re.fullmatch(r'(?:< )?[0-9]+(?:\.[0-9]+)? (?:ms|s|m|h)(?: [0-9]+(?:\.[0-9]+)? (?:ms|s|m|h))*', duration),
            'completed_duration_invalid')
    seen[name] += 1
    item = {'name': name, 'outcome': outcome, 'duration': duration}
    value = json.dumps(item, sort_keys=True, separators=(',', ':'))
    stream.write(value + '\n')
    stream.flush()
    print(value, flush=True)


def trx_cases(path, *, sanitize=False):
    root = ET.fromstring(read_bytes(path))
    summary = root.find('t:ResultSummary', NS)
    counts = root.find('t:ResultSummary/t:Counters', NS)
    require(summary is not None and counts is not None, 'trx_incomplete')
    definitions = {}
    for definition in root.findall('t:TestDefinitions/t:UnitTest', NS):
        test_id = definition.get('id')
        require(test_id not in definitions, 'trx_duplicate_definition')
        definitions[test_id] = Path(definition.get('storage', '')).stem.casefold()
    cases, ids = [], set()
    for result in root.findall('t:Results/t:UnitTestResult', NS):
        test_id = result.get('testId')
        require(test_id in definitions and test_id not in ids, 'trx_identity_invalid')
        ids.add(test_id)
        require(result.get('outcome') in ('Passed', 'Failed', 'NotExecuted'), 'trx_outcome_invalid')
        duration = result.get('duration', '')
        require(re.fullmatch(r'[0-9]+:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?', duration), 'trx_duration_invalid')
        cases.append({'assembly': definitions[test_id], 'id': test_id, 'name': result.get('testName'),
                      'outcome': result.get('outcome'), 'duration': duration})
    require(int(counts.get('total', '-1')) == len(cases)
            and int(counts.get('executed', '-1')) == sum(case['outcome'] != 'NotExecuted' for case in cases),
            'trx_counters_invalid')
    if sanitize:
        # Preserve test IDs, assembly, outcome and measured duration. Raw failure
        # messages, captured environment, stack traces and provider output never
        # enter the explicit export, even when a test fails.
        tag = lambda name: '{' + NS['t'] + '}' + name
        clean = ET.Element(tag('TestRun'))
        results, defs = ET.SubElement(clean, tag('Results')), ET.SubElement(clean, tag('TestDefinitions'))
        for case in cases:
            ET.SubElement(results, tag('UnitTestResult'), testId=case['id'], testName=case['name'],
                          outcome=case['outcome'], duration=case['duration'])
            ET.SubElement(defs, tag('UnitTest'), id=case['id'], name=case['name'], storage=case['assembly'] + '.dll')
        cleaned_summary = ET.SubElement(clean, tag('ResultSummary'), outcome=summary.get('outcome', 'Failed'))
        ET.SubElement(cleaned_summary, tag('Counters'), total=str(len(cases)),
                      executed=str(sum(case['outcome'] != 'NotExecuted' for case in cases)),
                      passed=str(sum(case['outcome'] == 'Passed' for case in cases)),
                      failed=str(sum(case['outcome'] == 'Failed' for case in cases)))
        ET.ElementTree(clean).write(path, encoding='utf-8', xml_declaration=True)
    return cases


def validate_results(output, expected):
    rows = []
    files = sorted((output / 'results').glob('*.trx'))
    require(files, 'trx_missing')
    for path in files:
        summary = ET.fromstring(read_bytes(path)).find('t:ResultSummary', NS)
        require(summary is not None and summary.get('outcome') in ('Completed', 'Passed'), 'trx_run_failed')
        rows.extend(trx_cases(path))
    require(rows and all(case['outcome'] == 'Passed' for case in rows)
            and Counter(key(case) for case in rows) == Counter(key(case) for case in expected), 'results_incomplete')
    lines = [json.loads(line) for line in read_bytes(output / 'completed-cases.jsonl').splitlines()]
    require(all(set(line) == {'name', 'outcome', 'duration'} and line['outcome'] == 'Passed' for line in lines)
            and Counter(line['name'] for line in lines) == Counter(case['name'] for case in expected),
            'completed_evidence_incomplete')
    return len(rows)


def export_results(raw_results, output, deadline):
    end = min(deadline, time.monotonic() + EXPORT_SECONDS)
    for raw in sorted(raw_results.glob('*.trx')):
        remaining(end)
        # Rewrite while still private. A failed parse or expired export budget
        # cannot publish the original file through the upload glob.
        trx_cases(raw, sanitize=True)
        remaining(end)
        raw.replace(output / 'results' / raw.name)
    remaining(end)


def workload(mode, context, private, output, deadline, work):
    require(cgroup_path('self') == work, 'worker_outside_owned_subtree')
    manifest = discover(private, deadline, work)
    write_json(output / 'discovery.json', {'protocol': PROTOCOL, 'identity': context, 'population': manifest})
    expected = Counter(case['name'] for case in manifest[mode])
    seen = Counter()
    # Raw TRX initially stays private; only the sanitized projection is exported.
    raw_results = private / 'trx'
    raw_results.mkdir(mode=0o700)
    refusal_stream = (output / 'native-refusals.jsonl').open('x', encoding='utf-8') if mode == 'native' else None
    try:
        with (output / 'completed-cases.jsonl').open('x', encoding='utf-8') as stream:
            refusal_seen = set()
            def consume(line):
                completed_line(line, expected, seen, stream)
                if refusal_stream is not None:
                    native_refusal(line, refusal_stream, refusal_seen)
            status = dotnet(['test', 'LearnStack.slnx', '--configuration', 'Release', '--no-build', '--no-restore',
                             '--disable-build-servers', '--filter', FILTERS[mode],
                             '--logger', 'console;verbosity=normal', '--logger', 'trx',
                             '--results-directory', str(raw_results)], deadline, work, consume)
    finally:
        if refusal_stream is not None:
            refusal_stream.close()
    export_results(raw_results, output, deadline)
    require(status == 0, 'tests_failed')
    count = validate_results(output, manifest[mode])
    write_json(private / 'workload.json', {'protocol': PROTOCOL, 'success': True, 'cases': count})


def join_workload(pid, work, execution, cleanup):
    global CANCELLED
    joined, status, forced = 0, None, False
    try:
        while True:
            remaining(execution)
            joined, status = os.waitpid(pid, os.WNOHANG)
            if joined == pid:
                break
            time.sleep(POLL_SECONDS)
        require(not populated(work), 'live_owned_descendant')
    except Refusal:
        forced = True
        CANCELLED = False
        (work / 'cgroup.kill').write_text('1', encoding='ascii')
        stop = min(cleanup, time.monotonic() + JOIN_SECONDS)
        while joined != pid:
            remaining(stop)
            joined, status = os.waitpid(pid, os.WNOHANG)
            if joined != pid:
                time.sleep(POLL_SECONDS)
        while populated(work):
            remaining(stop)
            time.sleep(POLL_SECONDS)
    return status, forced


def leaf(mode):
    context = identity(mode)
    unit, private, output = locations(mode, context)
    execution, cleanup = deadlines()
    lower_core()
    base = '/system.slice/' + unit
    require(cgroup_path('self') == base + '/supervisor', 'leaf_requires_owned_unit')
    require(stat.S_IMODE(private.stat().st_mode) == 0o700 and private.stat().st_uid == os.getuid(), 'private_directory_invalid')
    group = Path(CGROUP) / base.lstrip('/')
    work = group / 'work'
    work.mkdir()
    require(not populated(work), 'work_cgroup_not_empty')
    require((work / 'cgroup.kill').is_file(), 'cgroup_kill_unavailable')
    write_json(private / 'ready.json', {'protocol': PROTOCOL, 'ready': True})
    while not (private / 'launch').exists():
        remaining(execution)
        time.sleep(POLL_SECONDS)
    require(read_bytes(private / 'launch', 16) == b'launch\n', 'launch_handshake_invalid')
    pid = os.fork()
    if pid == 0:
        try:
            (work / 'cgroup.procs').write_text(str(os.getpid()), encoding='ascii')
            workload(mode, context, private, output, execution, base + '/work')
            os._exit(0)
        except BaseException as failure:
            reason = failure.reason if isinstance(failure, Refusal) else 'workload_internal_refusal'
            write_json(private / 'workload.json', {'protocol': PROTOCOL, 'success': False, 'error': reason})
            os._exit(1)
    # Kill/join on failure keeps the entire retained work subtree accounted for,
    # including setsid descendants. Forced termination can never become success.
    status, forced = join_workload(pid, work, execution, cleanup)
    # The sibling supervisor keeps this existing subtree alive until after this
    # witness. Neither stop/kill nor systemd's subsequent pruning supplies it.
    require(not populated(work), 'live_owned_descendant')
    require(read_bytes(group / 'cgroup.procs', 4096).strip() == b'', 'unexpected_unit_process')
    supervisor_pids = read_bytes(group / 'supervisor/cgroup.procs', 4096).split()
    require(supervisor_pids == [str(os.getpid()).encode()], 'unexpected_supervisor_descendant')
    require({path.name for path in group.iterdir() if path.is_dir()} == {'supervisor', 'work'},
            'unexpected_owned_subgroup')
    write_json(private / 'ownership.json', {'protocol': PROTOCOL, 'workloadJoined': True,
                                           'workPopulated': False, 'witnessBeforeUnitExit': True,
                                           'workloadStatus': os.waitstatus_to_exitcode(status),
                                           'forcedTermination': forced})
    return 0 if not forced and os.WIFEXITED(status) and os.WEXITSTATUS(status) == 0 else 1


def unit_properties(unit, deadline):
    names = ('LoadState', 'ActiveState', 'SubState', 'Result', 'ControlGroup', 'User', 'Group',
             'Delegate', 'DelegateSubgroup', 'Type', 'RemainAfterExit', 'ExecMainCode',
             'ExecMainStatus', 'ExecMainExitTimestampMonotonic')
    data = command([SYSTEMCTL, 'show', unit, '--property=' + ','.join(names)], deadline, 'unit_observation_failed')
    try:
        fields = dict(line.split('=', 1) for line in data.decode().splitlines())
    except (UnicodeError, ValueError):
        raise Refusal('unit_observation_failed') from None
    require(set(fields) == set(names), 'unit_observation_incomplete')
    return fields


def launch_unit(mode, context, private, unit, deadline):
    forwarded = {'PATH', 'HOME', 'RUNNER_TEMP', 'DOTNET_ROOT', 'PNPM_HOME', 'GITHUB_ACTIONS',
                 'RUNNER_OS', 'RUNNER_ARCH', 'RUNNER_ENVIRONMENT', 'ImageOS', 'ImageVersion',
                 'LEARNSTACK_CI_STARTED_AT'}
    forwarded.update('GITHUB_' + name for name in CONTEXT_NAMES)
    forwarded.update('LEARNSTACK_NATIVE_' + ('ATTEMPT' if name == 'RUN_ATTEMPT' else name)
                     for name in CONTEXT_NAMES)
    forwarded.update('LEARNSTACK_NATIVE_' + name for name in
                     ('RUNNER_CONTEXT', 'RUNNER_OS', 'RUNNER_ARCH', 'RUNNER_LABEL', 'JOB_CONTAINER', 'JOB_SERVICES'))
    env = [name + '=' + os.environ[name] for name in sorted(forwarded) if name in os.environ]
    log = private / 'unit-private.log'
    log.touch(mode=0o600, exist_ok=False)
    command([SUDO, '-n', SYSTEMD_RUN, '--quiet', '--unit=' + unit, '--service-type=exec',
             '--remain-after-exit', '--expand-environment=no', '--uid=' + str(os.getuid()),
             '--gid=' + str(os.getgid()), '--working-directory=' + str(ROOT),
             '--property=Delegate=yes', '--property=DelegateSubgroup=supervisor',
             '--property=LimitCORE=0', '--property=UMask=0077', '--property=KillMode=control-group',
             '--property=TimeoutStopSec=' + str(UNIT_STOP_SECONDS // 2),
             '--property=RuntimeMaxSec=' + str(math.ceil(remaining(deadline) + JOIN_SECONDS + PROCESS_JOIN_SECONDS)),
             '--property=StandardInput=null', '--property=StandardOutput=append:' + str(log),
             '--property=StandardError=append:' + str(log),
             ENV, '-i', *env, PYTHON, '-I', str(Path(__file__).resolve()), '_' + mode + '-leaf'],
            deadline, 'unit_launch_failed')
    while not (private / 'ready.json').exists():
        remaining(deadline)
        time.sleep(POLL_SECONDS)
    actual = unit_properties(unit, deadline)
    require(actual['LoadState'] == 'loaded' and actual['ActiveState'] == 'active'
            and actual['SubState'] == 'running' and actual['User'] == str(os.getuid())
            and actual['Group'] == str(os.getgid()) and actual['Delegate'] == 'yes'
            and actual['DelegateSubgroup'] == 'supervisor' and actual['Type'] == 'exec'
            and actual['RemainAfterExit'] == 'yes'
            and actual['ControlGroup'] == '/system.slice/' + unit, 'unit_ownership_invalid')


def docker_census(deadline):
    data = command([DOCKER, 'ps', '--all', '--no-trunc', '--format', '{{.ID}} {{.State}}'],
                   deadline, 'docker_census_unreadable').decode('ascii')
    result = {}
    for line in data.splitlines():
        fields = line.split(' ')
        require(len(fields) == 2 and re.fullmatch(r'[0-9a-f]{64}', fields[0])
                and fields[1] in ('created', 'running', 'paused', 'restarting', 'removing', 'exited', 'dead'),
                'docker_state_unknown')
        require(fields[0] not in result, 'docker_census_invalid')
        result[fields[0]] = fields[1]
    require(len(result) <= 256, 'docker_census_bound')
    return result


def docker_complete(deadline, *, forced):
    end = min(deadline, time.monotonic() + DOCKER_SECONDS)
    while True:
        census = docker_census(end)
        if not census:
            # Once all native start authorities have joined, the independent
            # daemon's all-state empty read is a disposal witness, not restoration.
            require(not docker_census(end), 'docker_census_changed')
            return not forced
        if forced:
            command([DOCKER, 'rm', '--force', *sorted(census)], end, 'docker_cleanup_failed')
        remaining(end)
        time.sleep(POLL_SECONDS)


def crash_scan(deadline):
    roots = [str(ROOT), os.environ['RUNNER_TEMP'], *HOST_CRASH_ROOTS]
    roots = [path for path in roots if Path(path).exists()]
    # Read-only, fixed predicates. Container scratch proofs remain in the native
    # tests; this additionally inspects actual host file-collector destinations.
    value = command([SUDO, '-n', FIND, *roots, '-xdev', '-type', 'f', '(', '-name', 'core',
                     '-o', '-regex', '.*/core\\.[0-9]+', '-o', '-name', '*.core',
                     '-o', '-name', '*.dmp', '-o', '-name', '*.dump', '-o', '-name', '*.crash', ')',
                     '-print', '-quit'], deadline, 'crash_inventory_unreadable', cap=SCAN_SECONDS)
    require(not value, 'unexpected_crash_artifact')


def export_hashes(output, deadline=None):
    names = ['discovery.json', 'completed-cases.jsonl']
    if (output / 'provisioning.json').exists():
        names.append('provisioning.json')
    if (output / 'native-refusals.jsonl').exists():
        names.append('native-refusals.jsonl')
    names.extend('results/' + path.name for path in sorted((output / 'results').glob('*.trx')))
    hashes = {}
    for name in names:
        if deadline is not None:
            remaining(deadline)
        hashes[name] = hashlib.sha256(read_bytes(output / name)).hexdigest()
    if deadline is not None:
        remaining(deadline)
    return hashes


def print_completed(path, state):
    if not path.exists():
        return
    with path.open('rb') as stream:
        stream.seek(state['offset'])
        chunk = stream.read(MAX_BYTES + 1)
    state['offset'] += len(chunk)
    require(state['offset'] <= MAX_BYTES, 'completed_evidence_bound')
    pending = state['pending'] + chunk
    lines = pending.split(b'\n')
    state['pending'] = lines.pop()
    for line in lines:
        value = json.loads(line)
        require(set(value) == {'name', 'outcome', 'duration'}, 'completed_shape_invalid')
        print(json.dumps(value, sort_keys=True), flush=True)


def prevalidate_native(provisioner, deadline):
    try:
        budget = provisioner.Budget(deadline, COMMAND_SECONDS)
        workspace = provisioner.workflow_context()
        provisioner.qualify_runtime(budget, workspace)
        provisioner.collector_class(budget.read(provisioner.CORE_PATTERN, 'collector_unreadable'))
    except provisioner.Refusal as failure:
        raise Refusal(failure.reason) from None


def execute(mode):
    global CANCELLED
    context = identity(mode)
    execution, cleanup = deadlines()
    remaining(execution)
    lower_core()
    unit, private, output = locations(mode, context)
    private.mkdir(mode=0o700)
    output.mkdir(parents=True, exist_ok=True)
    require(not any(path.name != 'environment.json' for path in output.iterdir()), 'stale_artifacts')
    (output / 'results').mkdir()
    started, baseline_empty, error, success = False, False, None, False
    began = time.monotonic()
    receipt = {'protocol': PROTOCOL, 'identity': context, 'partition': mode, 'job': JOBS[mode],
               'success': False, 'complete': False, 'ownershipComplete': False,
               'dockerAbsent': False, 'forcedCleanup': False}
    try:
        require(not docker_census(execution), 'docker_baseline_nonempty')
        baseline_empty = True
        require((Path(CGROUP) / 'cgroup.controllers').is_file(), 'cgroup_v2_required')
        require(os.access(DOTNET, os.X_OK), 'fixed_dotnet_missing')
        if mode == 'native':
            spec = importlib.util.spec_from_file_location('native_provisioning', Path(__file__).with_name('ci-native-provisioning.py'))
            provisioner = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(provisioner)
            # Refuse platform/topology/CPU and collector contradictions before
            # even read-only sudo or the transient unit's privilege. Provisioning
            # independently repeats qualification, drift and write/readback checks.
            prevalidate_native(provisioner, execution)
            crash_scan(execution)
        require(not command([SYSTEMCTL, 'list-units', '--all', '--plain', '--no-legend', unit],
                            execution, 'unit_baseline_unreadable').strip(), 'unit_already_exists')
        started = True
        launch_unit(mode, context, private, unit, execution)
        if mode == 'native':
            try:
                result = provisioner.provision(deadline=execution, command_timeout=COMMAND_SECONDS)
            except provisioner.Refusal as failure:
                raise Refusal(failure.reason) from None
            write_json(output / 'provisioning.json', result)
        receipt['setupSeconds'] = round(time.monotonic() - began, 3)
        (private / 'launch').write_bytes(b'launch\n')
        progress = {'offset': 0, 'pending': b''}
        while True:
            remaining(execution)
            print_completed(output / 'completed-cases.jsonl', progress)
            actual = unit_properties(unit, execution)
            if actual['SubState'] == 'exited' or actual['ActiveState'] in ('failed', 'inactive'):
                break
            time.sleep(UNIT_POLL_SECONDS)
        print_completed(output / 'completed-cases.jsonl', progress)
        ownership = read_json(private / 'ownership.json')
        require(ownership == {'protocol': PROTOCOL, 'workloadJoined': True, 'workPopulated': False,
                              'witnessBeforeUnitExit': True, 'workloadStatus': 0,
                              'forcedTermination': False}, 'owned_completion_missing')
        require(actual['ExecMainCode'] == '1' and actual['ExecMainStatus'] == '0'
                and int(actual['ExecMainExitTimestampMonotonic']) > 0 and actual['Result'] == 'success',
                'unit_join_failed')
        receipt['ownershipComplete'] = True
        manifest = read_json(output / 'discovery.json')
        require(manifest['identity'] == context, 'discovery_identity_mismatch')
        validate_partition(manifest['population'])
        receipt['cases'] = validate_results(output, manifest['population'][mode])
        require(read_json(private / 'workload.json').get('success') is True, 'workload_failed')
        receipt['executionSeconds'] = round(time.monotonic() - began - receipt['setupSeconds'], 3)
        success = True
    except (Refusal, OSError, ValueError, KeyError, ET.ParseError) as failure:
        error = failure.reason if isinstance(failure, Refusal) else 'execution_internal_refusal'
    finally:
        # Cleanup has its own bounded allocation and remains RED after any kill.
        # A cancellation enters this path; it does not suppress owned cleanup.
        if CANCELLED:
            success, error = False, error or 'cancelled'
        CANCELLED = False
        cleanup_began = time.monotonic()
        if started:
            # Give the supervisor its bounded failure-join path before asking
            # systemd to stop it. Missing evidence stays explicitly incomplete.
            receipt['cleanupOwnershipComplete'] = receipt['ownershipComplete']
            if not success and (private / 'launch').exists():
                try:
                    join_end = min(cleanup, time.monotonic() + JOIN_SECONDS)
                    while True:
                        actual = unit_properties(unit, join_end)
                        if actual['SubState'] == 'exited' or actual['ActiveState'] in ('failed', 'inactive'):
                            witness = read_json(private / 'ownership.json')
                            receipt['cleanupOwnershipComplete'] = (
                                witness.get('workloadJoined') is True and witness.get('workPopulated') is False
                                and witness.get('witnessBeforeUnitExit') is True
                                and actual['ExecMainCode'] == '1'
                                and int(actual['ExecMainExitTimestampMonotonic']) > 0)
                            break
                        remaining(join_end)
                        time.sleep(POLL_SECONDS)
                except (Refusal, ValueError):
                    pass
            try:
                command([SUDO, '-n', SYSTEMCTL, 'stop', unit], cleanup, 'unit_stop_failed', cap=UNIT_STOP_SECONDS)
            except Refusal:
                success, error = False, error or 'unit_stop_failed'
            try:
                command([SUDO, '-n', SYSTEMCTL, 'reset-failed', unit], cleanup, 'unit_collect_failed')
            except Refusal:
                # Stopping a successful transient unit may already collect it.
                pass
        if started and baseline_empty:
            docker_end = min(cleanup, time.monotonic() + DOCKER_SECONDS)
            if success:
                try:
                    # Leave half the same Docker allocation for forced failure
                    # cleanup. Natural-disposal timeout never earns a second
                    # full allocation or a passing result.
                    receipt['dockerAbsent'] = docker_complete(
                        min(docker_end, time.monotonic() + DOCKER_SECONDS / 2), forced=False)
                except Refusal as failure:
                    success, error = False, error or failure.reason
            if not success:
                receipt['forcedCleanup'] = True
                try:
                    docker_complete(docker_end, forced=True)
                except Refusal:
                    error = error or 'docker_cleanup_incomplete'
        try:
            if mode == 'native' and started:
                crash_scan(cleanup)
        except Refusal as failure:
            success, error = False, error or failure.reason
        if success:
            try:
                receipt['artifacts'] = export_hashes(
                    output, min(cleanup, time.monotonic() + EXPORT_SECONDS))
            except (Refusal, OSError) as failure:
                success = False
                error = failure.reason if isinstance(failure, Refusal) else 'export_failed'
        receipt.update({'success': success, 'complete': success, 'error': error,
                        'cleanupSeconds': round(time.monotonic() - cleanup_began, 3)})
        write_json(output / 'result.json', receipt)
        print(json.dumps(receipt, sort_keys=True), flush=True)
    return 0 if success else 1


def validate_aggregate(context):
    require(os.environ.get('LEARNSTACK_NATIVE_RESULT') == 'success'
            and os.environ.get('LEARNSTACK_ORDINARY_RESULT') == 'success', 'execution_job_not_successful')
    manifests = []
    for mode in JOBS:
        output = ROOT / 'artifacts/ci-aggregate' / mode
        result = read_json(output / 'result.json')
        require(result.get('protocol') == PROTOCOL and result.get('identity') == context
                and result.get('partition') == mode and result.get('job') == JOBS[mode]
                and result.get('success') is True and result.get('complete') is True
                and result.get('ownershipComplete') is True and result.get('dockerAbsent') is True
                and result.get('forcedCleanup') is False and result.get('error') is None,
                'execution_receipt_incomplete')
        if mode == 'native':
            provisioning = read_json(output / 'provisioning.json')
            require(provisioning.get('protocol') == PROTOCOL
                    and provisioning.get('scope') == 'native_ci_setup_not_child_qualification'
                    and provisioning.get('collectorAfter') == 'supported_file'
                    and provisioning.get('softCore') == provisioning.get('hardCore') == 'zero'
                    and provisioning.get('sourceSha') == context['sourceSha']
                    and provisioning.get('runId') == context['runId']
                    and provisioning.get('runAttempt') == context['runAttempt']
                    and provisioning.get('job') == JOBS[mode], 'provisioning_receipt_incomplete')
            require(read_bytes(output / 'native-refusals.jsonl') == b'', 'native_refusal_reported')
        require(result.get('artifacts') == export_hashes(output), 'artifact_hash_mismatch')
        discovery = read_json(output / 'discovery.json')
        require(discovery.get('protocol') == PROTOCOL and discovery.get('identity') == context,
                'discovery_identity_mismatch')
        population = discovery['population']
        validate_partition(population)
        require(result.get('cases') == validate_results(output, population[mode]), 'result_count_mismatch')
        manifests.append(population)
    require(manifests[0] == manifests[1], 'cross_job_discovery_mismatch')
    print(json.dumps({'protocol': PROTOCOL, 'identity': context, 'success': True,
                      'cases': len(manifests[0]['all'])}, sort_keys=True))
    return 0


def aggregate():
    return validate_aggregate(identity('aggregate'))


def interrupted(_signal, _frame):
    global CANCELLED
    CANCELLED = True


def main():
    if len(sys.argv) != 2 or sys.argv[1] not in ('native', 'ordinary', 'aggregate', 'upload-budget',
                                               '_native-leaf', '_ordinary-leaf'):
        print('{"protocol":1,"error":"mode_refused"}')
        return 1
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    try:
        mode = sys.argv[1]
        if mode == 'upload-budget':
            print('minutes=' + str(math.ceil(UPLOAD_SECONDS / 60)))
            return 0
        if mode.startswith('_'):
            return leaf(mode[1:].removesuffix('-leaf'))
        return aggregate() if mode == 'aggregate' else execute(mode)
    except (Refusal, OSError, ValueError, KeyError, TypeError, UnicodeError, ET.ParseError) as failure:
        reason = failure.reason if isinstance(failure, Refusal) else 'launcher_internal_refusal'
        print(json.dumps({'protocol': PROTOCOL, 'error': reason}, sort_keys=True))
        return 1


if __name__ == '__main__':
    sys.exit(main())
