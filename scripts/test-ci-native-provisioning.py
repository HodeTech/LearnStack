#!/usr/bin/env python3
"""Offline subprocess controls: only fixed paths change in the executed owner."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SOURCE = Path(__file__).with_name('ci-native-provisioning.py')
SOURCE_SHA256 = '2af57083522e7415ee73c2617644bf0b831065ebdacc580e75582c62366cda7b'
FIXED_PATHS = {
    'CORE_PATTERN': '/proc/sys/kernel/core_pattern', 'OS_RELEASE': '/etc/os-release',
    'CGROUP_INIT': '/proc/1/cgroup', 'CGROUP_SELF': '/proc/self/cgroup',
    'CONTAINER_MARKERS': ('/.dockerenv', '/run/.containerenv', '/run/systemd/container'),
    'HEAD': '/usr/bin/head', 'UNAME': '/usr/bin/uname', 'NPROC': '/usr/bin/nproc',
    'ID': '/usr/bin/id', 'GIT': '/usr/bin/git', 'VIRT': '/usr/bin/systemd-detect-virt',
    'DOCKER': '/usr/bin/docker', 'SUDO': '/usr/bin/sudo', 'TRUE': '/usr/bin/true',
    'TEE': '/usr/bin/tee',
}


def verified_copy(original, replacements):
    candidate = original
    for name, value in FIXED_PATHS.items():
        before = (name + ' = ' + repr(value) + '\n').encode()
        after = (name + ' = ' + repr(replacements[name]) + '\n').encode()
        if candidate.count(before) != 1:
            raise AssertionError('Fixed source assignment changed: ' + name)
        candidate = candidate.replace(before, after, 1)
    restored = candidate
    for name, value in FIXED_PATHS.items():
        before = (name + ' = ' + repr(value) + '\n').encode()
        after = (name + ' = ' + repr(replacements[name]) + '\n').encode()
        if restored.count(after) != 1:
            raise AssertionError('Ambiguous temporary assignment: ' + name)
        restored = restored.replace(after, before, 1)
    if restored != original:
        raise AssertionError('Temporary owner changed bytes outside fixed paths')
    return candidate


class Fixture:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.repo = self.root / 'repo'
        scripts = self.repo / 'scripts'
        scripts.mkdir(parents=True)
        self.pattern = self.root / 'core_pattern'
        self.pattern.write_text('|/usr/share/apport/apport PRIVATE-COLLECTOR-CANARY\n')
        (self.root / 'os-release').write_text('ID=ubuntu\nVERSION_ID="24.04"\n')
        (self.root / 'init-cgroup').write_text('0::/init.scope\n')
        (self.root / 'self-cgroup').write_text('0::/user.slice/runner.scope\n')
        self.config = self.root / 'config.json'
        self.config.write_text('{}')
        self.log = self.root / 'commands.jsonl'
        self.log.write_text('')
        self.authorized = self.root / 'launch-authorized'
        replacements = {
            'CORE_PATTERN': str(self.pattern), 'OS_RELEASE': str(self.root / 'os-release'),
            'CGROUP_INIT': str(self.root / 'init-cgroup'),
            'CGROUP_SELF': str(self.root / 'self-cgroup'),
            'CONTAINER_MARKERS': tuple(str(self.root / ('marker-' + str(i))) for i in range(3)),
        }
        for name in FIXED_PATHS.keys() - replacements.keys():
            replacements[name] = str(self.root / name.lower())
        self.paths = replacements
        self.owner = scripts / SOURCE.name
        self.owner.write_bytes(verified_copy(SOURCE.read_bytes(), replacements))
        # Every observation is a real executable. Neither unittest mocks nor an
        # alternate provisioning model replace production subprocess semantics.
        helper = '#!' + sys.executable + '\n' + r'''
import json
import os
from pathlib import Path
import sys
import time
root = Path(ROOT_LITERAL)
cfg = json.loads((root / 'config.json').read_text())
assert not any(key in os.environ for key in ('PRIVATE_ENV_CANARY', 'DOCKER_HOST', 'GIT_CONFIG_COUNT'))
name = Path(sys.argv[0]).name
args = sys.argv[1:]
with (root / 'commands.jsonl').open('a') as stream:
    stream.write(json.dumps({'name': name, 'args': args}) + '\n')
if cfg.get('timeout') == name:
    time.sleep(1)
if name == 'head':
    target = Path(args[1])
    if target == root / 'core_pattern':
        counter = root / 'reads'
        count = int(counter.read_text()) if counter.exists() else 0
        counter.write_text(str(count + 1))
        if cfg.get('read_fail_at') == count:
            print('PRIVATE-PROVIDER-CANARY', file=sys.stderr)
            sys.exit(1)
        if cfg.get('drift_at') == count:
            target.write_text(cfg.get('drift_value', '|/lib/systemd/systemd-coredump DRIFT\n'))
    sys.stdout.buffer.write(target.read_bytes()[:int(args[0].split('=')[1])])
elif name == 'uname':
    print(cfg.get('uname', 'Linux 6.14.0-1017-azure x86_64'))
elif name == 'nproc':
    print(cfg.get('cpus', '4'))
elif name == 'id':
    print(cfg.get('uid', '1001'))
elif name == 'git':
    print(cfg.get('sha', 'a' * 40) if args[-1] == 'HEAD' else cfg.get('workspace', str(root / 'repo')))
elif name == 'virt':
    if args == ['--container']:
        print(cfg.get('container', 'none'))
        sys.exit(cfg.get('container_status', 1))
    print(cfg.get('vm', 'microsoft'))
    sys.exit(cfg.get('vm_status', 0))
elif name == 'docker':
    sys.stdout.write(cfg.get('docker_output', ''))
    print('PRIVATE-PROVIDER-CANARY', file=sys.stderr)
    sys.exit(cfg.get('docker_status', 0))
elif name == 'sudo':
    assert args[0] == '-n'
    if Path(args[1]).name == 'true' and cfg.get('sudo_probe_fail'):
        sys.exit(1)
    if Path(args[1]).name == 'tee' and cfg.get('write_fail'):
        sys.exit(1)
    os.execv(args[1], args[1:])
elif name == 'true':
    print('PRIVATE-PROVIDER-CANARY', file=sys.stderr)
elif name == 'tee':
    data = sys.stdin.buffer.read()
    (root / 'stdin').write_bytes(data)
    target = Path(args[0])
    target.write_bytes(data[:2] if cfg.get('partial_write') else data)
    print('PRIVATE-PROVIDER-CANARY')
    print('PRIVATE-PROVIDER-CANARY', file=sys.stderr)
else:
    raise AssertionError(name)
'''.replace('ROOT_LITERAL', repr(str(self.root)))
        for name in FIXED_PATHS.keys() - {'CORE_PATTERN', 'OS_RELEASE', 'CGROUP_INIT',
                                         'CGROUP_SELF', 'CONTAINER_MARKERS'}:
            executable = Path(replacements[name])
            executable.write_text(helper)
            executable.chmod(0o700)
        self.runner = self.root / 'runner.py'
        self.runner.write_text('''import importlib.util
import json
from pathlib import Path
import resource
import time
spec = importlib.util.spec_from_file_location('owner', OWNER_LITERAL)
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)
cfg = json.loads(Path(CONFIG_LITERAL).read_text())
try:
    receipt = owner.provision(deadline=time.monotonic() + cfg.get('deadline', 15),
                              command_timeout=cfg.get('command_timeout', 2))
    Path(AUTHORIZED_LITERAL).touch()
    print(json.dumps({'ok': True, 'receipt': receipt,
                      'limits': resource.getrlimit(resource.RLIMIT_CORE)}))
except owner.Refusal as error:
    print(json.dumps({'ok': False, 'reason': error.reason}))
'''.replace('OWNER_LITERAL', repr(str(self.owner)))
           .replace('CONFIG_LITERAL', repr(str(self.config)))
           .replace('AUTHORIZED_LITERAL', repr(str(self.authorized))))
        self.env = {
            'GITHUB_ACTIONS': 'true', 'RUNNER_OS': 'Linux', 'RUNNER_ARCH': 'X64',
            'RUNNER_ENVIRONMENT': 'github-hosted', 'ImageOS': 'ubuntu24',
            'ImageVersion': '20261011.5.1', 'LEARNSTACK_NATIVE_RUNNER_CONTEXT': 'github-hosted',
            'LEARNSTACK_NATIVE_RUNNER_OS': 'Linux', 'LEARNSTACK_NATIVE_RUNNER_ARCH': 'X64',
            'LEARNSTACK_NATIVE_RUNNER_LABEL': 'ubuntu-24.04',
            'LEARNSTACK_NATIVE_JOB_CONTAINER': 'null', 'LEARNSTACK_NATIVE_JOB_SERVICES': 'null',
            'PRIVATE_ENV_CANARY': 'PRIVATE-ENVIRONMENT-CANARY',
        }
        for key, value in {
            'RUN_ID': '39120501520', 'RUN_ATTEMPT': '1', 'JOB': 'backend-native',
            'SHA': 'a' * 40, 'REPOSITORY': 'HodeTech/LearnStack',
            'WORKFLOW_REF': 'HodeTech/LearnStack/.github/workflows/ci.yml@refs/heads/development',
            'WORKFLOW_SHA': 'a' * 40, 'WORKSPACE': str(self.repo),
        }.items():
            self.env['GITHUB_' + key] = value
            self.env['LEARNSTACK_NATIVE_' + ('ATTEMPT' if key == 'RUN_ATTEMPT' else key)] = value

    def run(self, config=None, env=None):
        self.config.write_text(json.dumps(config or {}))
        child_env = dict(self.env)
        for key, value in (env or {}).items():
            if value is None:
                child_env.pop(key, None)
            else:
                child_env[key] = value
        result = subprocess.run([sys.executable, str(self.runner)], cwd=self.repo,
                                env=child_env, stdin=subprocess.DEVNULL,
                                capture_output=True, text=True, timeout=25, check=False)
        if result.returncode != 0:
            raise AssertionError('Fixture owner crashed: ' + result.stderr)
        if result.stderr:
            raise AssertionError('Fixture owner disclosed stderr: ' + result.stderr)
        if 'CANARY' in result.stdout:
            raise AssertionError('Fixture owner disclosed private bytes')
        return json.loads(result.stdout)

    def commands(self, name=None):
        commands = [json.loads(line) for line in self.log.read_text().splitlines()]
        return [item for item in commands if name is None or item['name'] == name]


class NativeProvisioningTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='native-provisioning-')
        self.addCleanup(self.temp.cleanup)
        self.fixture = Fixture(self.temp.name)

    def assert_refused(self, result, reason, *, before_privilege=True):
        self.assertEqual({'ok': False, 'reason': reason}, result)
        self.assertFalse(self.fixture.authorized.exists())
        if before_privilege:
            self.assertEqual([], self.fixture.commands('sudo'))

    def test_executed_owner_bytes_are_reviewed_and_only_fixed_paths_change(self):
        original = SOURCE.read_bytes()
        self.assertEqual(SOURCE_SHA256, hashlib.sha256(original).hexdigest())
        self.assertEqual(verified_copy(original, self.fixture.paths), self.fixture.owner.read_bytes())
        with self.assertRaisesRegex(AssertionError, 'Fixed source assignment changed'):
            verified_copy(original.replace(b"HEAD = '/usr/bin/head'", b"HEAD = '/bin/cat'"),
                          self.fixture.paths)

    def test_known_pipe_setup_writes_once_with_exact_arguments_and_literal_stdin(self):
        canary_file = self.fixture.root / 'evaluated'
        self.fixture.pattern.write_text('|/usr/share/apport/apport $(touch ' + str(canary_file)
                                        + ') ; PRIVATE-COLLECTOR-CANARY\n')
        result = self.fixture.run(env={'DOCKER_HOST': 'PRIVATE-ENVIRONMENT-CANARY',
                                       'GIT_CONFIG_COUNT': 'PRIVATE-ENVIRONMENT-CANARY'})
        self.assertTrue(result['ok'])
        self.assertEqual([0, 0], result['limits'])
        self.assertEqual(b'core\n', self.fixture.pattern.read_bytes())
        self.assertEqual(b'core\n', (self.fixture.root / 'stdin').read_bytes())
        self.assertFalse(canary_file.exists())
        self.assertEqual([
            {'name': 'sudo', 'args': ['-n', self.fixture.paths['TRUE']]},
            {'name': 'sudo', 'args': ['-n', self.fixture.paths['TEE'], str(self.fixture.pattern)]},
        ], self.fixture.commands('sudo'))
        self.assertEqual(3, int((self.fixture.root / 'reads').read_text()))
        self.assertEqual(['ps', '--all', '--last', '1', '--format', '{{.State}}'],
                         self.fixture.commands('docker')[0]['args'])
        self.assertEqual([
            ('git', ['rev-parse', '--verify', 'HEAD']),
            ('git', ['rev-parse', '--show-toplevel']),
            ('uname', ['-s', '-r', '-m']),
            ('head', ['--bytes=16385', self.fixture.paths['OS_RELEASE']]),
            ('id', ['-u']), ('nproc', []),
            ('head', ['--bytes=4097', self.fixture.paths['CGROUP_INIT']]),
            ('head', ['--bytes=4097', self.fixture.paths['CGROUP_SELF']]),
            ('virt', ['--container']), ('virt', ['--vm']),
            ('docker', ['ps', '--all', '--last', '1', '--format', '{{.State}}']),
            ('head', ['--bytes=4097', str(self.fixture.pattern)]),
            ('sudo', ['-n', self.fixture.paths['TRUE']]), ('true', []),
            ('head', ['--bytes=4097', str(self.fixture.pattern)]),
            ('sudo', ['-n', self.fixture.paths['TEE'], str(self.fixture.pattern)]),
            ('tee', [str(self.fixture.pattern)]),
            ('head', ['--bytes=4097', str(self.fixture.pattern)]),
        ], [(command['name'], command['args']) for command in self.fixture.commands()])
        receipt = result['receipt']
        self.assertEqual('apport', receipt['collectorBefore'])
        self.assertTrue(receipt['changed'])
        self.assertNotIn(str(self.fixture.root), json.dumps(receipt))

    def test_executed_nondocker_runtime_reader_has_no_docker_collector_or_sudo_calls(self):
        runner = self.fixture.root / 'runtime-reader.py'
        runner.write_text('''import importlib.util, json, time
from pathlib import Path
spec = importlib.util.spec_from_file_location('owner', OWNER_LITERAL)
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)
result = owner.qualify_runtime(owner.Budget(time.monotonic() + 15, 2), Path.cwd(), docker=False)
print(json.dumps(result))
'''.replace('OWNER_LITERAL', repr(str(self.fixture.owner))))
        original = self.fixture.pattern.read_bytes()
        result = subprocess.run([sys.executable, str(runner)], cwd=self.fixture.repo,
                                env=self.fixture.env, capture_output=True, text=True, timeout=20)
        self.assertEqual(0, result.returncode)
        self.assertEqual('', result.stderr)
        self.assertEqual(4, json.loads(result.stdout)['cpus'])
        self.assertEqual(original, self.fixture.pattern.read_bytes())
        self.assertFalse(self.fixture.commands('docker'))
        self.assertFalse(self.fixture.commands('sudo'))
        self.assertFalse(self.fixture.commands('tee'))
        self.assertFalse(any(command['name'] == 'head' and command['args'][-1] == str(self.fixture.pattern)
                             for command in self.fixture.commands()))

    def test_both_systemd_classes_take_the_same_fixed_writer(self):
        for executable in ('/usr/lib/systemd/systemd-coredump', '/lib/systemd/systemd-coredump'):
            with self.subTest(executable=executable), tempfile.TemporaryDirectory() as root:
                fixture = Fixture(root)
                fixture.pattern.write_text('|' + executable + ' PRIVATE-COLLECTOR-CANARY\n')
                result = fixture.run()
                self.assertTrue(result['ok'])
                self.assertEqual('systemd_coredump', result['receipt']['collectorBefore'])
                self.assertEqual(1, len(fixture.commands('tee')))

    def test_supported_file_patterns_are_independently_verified_without_a_write(self):
        for original in ('core\n', 'core.%p\n', 'core', 'core.%p'):
            with self.subTest(original=original), tempfile.TemporaryDirectory() as root:
                fixture = Fixture(root)
                fixture.pattern.write_text(original)
                result = fixture.run()
                self.assertTrue(result['ok'])
                self.assertFalse(result['receipt']['changed'])
                self.assertEqual(original, fixture.pattern.read_text())
                self.assertEqual([], fixture.commands('tee'))
                self.assertEqual(1, len(fixture.commands('sudo')))
                self.assertEqual('3', (fixture.root / 'reads').read_text())

    def test_absent_services_accept_json_null_and_empty_object(self):
        for services in ('null', '{}'):
            with self.subTest(services=services), tempfile.TemporaryDirectory() as root:
                fixture = Fixture(root)
                fixture.pattern.write_text('core\n')
                result = fixture.run(env={'LEARNSTACK_NATIVE_JOB_SERVICES': services})
                self.assertTrue(result['ok'])
                self.assertFalse(result['receipt']['changed'])
                self.assertTrue(fixture.authorized.exists())

    def test_untrusted_or_mismatched_step_context_refuses_before_any_command(self):
        cases = [
            ({'GITHUB_ACTIONS': 'false'}, 'unsupported_context'),
            ({'LEARNSTACK_NATIVE_RUNNER_CONTEXT': 'self-hosted'}, 'unsupported_context'),
            ({'LEARNSTACK_NATIVE_RUNNER_CONTEXT': None}, 'unsupported_context'),
            ({'LEARNSTACK_NATIVE_RUNNER_OS': 'macOS'}, 'unsupported_context'),
            ({'LEARNSTACK_NATIVE_RUNNER_ARCH': 'ARM64'}, 'unsupported_context'),
            ({'LEARNSTACK_NATIVE_RUNNER_LABEL': 'ubuntu-latest'}, 'unsupported_context'),
            ({'RUNNER_ENVIRONMENT': 'self-hosted'}, 'unsupported_context'),
            ({'RUNNER_ENVIRONMENT': None}, 'unsupported_context'),
            ({'RUNNER_OS': 'Windows'}, 'unsupported_context'),
            ({'RUNNER_ARCH': 'ARM64'}, 'unsupported_context'),
            ({'LEARNSTACK_NATIVE_RUN_ID': '2'}, 'context_identity_mismatch'),
            ({'LEARNSTACK_NATIVE_ATTEMPT': '2'}, 'context_identity_mismatch'),
            ({'LEARNSTACK_NATIVE_JOB': 'backend'}, 'context_identity_mismatch'),
            ({'LEARNSTACK_NATIVE_SHA': 'b' * 40}, 'context_identity_mismatch'),
            ({'LEARNSTACK_NATIVE_REPOSITORY': 'foreign/repo'}, 'context_identity_mismatch'),
            ({'LEARNSTACK_NATIVE_WORKSPACE': '/foreign'}, 'context_identity_mismatch'),
            ({'LEARNSTACK_NATIVE_WORKFLOW_REF': 'foreign'}, 'context_identity_mismatch'),
            ({'LEARNSTACK_NATIVE_WORKFLOW_SHA': 'b' * 40}, 'context_identity_mismatch'),
            ({'LEARNSTACK_NATIVE_JOB_CONTAINER': '{"id":"foreign"}'}, 'unsupported_job_topology'),
            ({'LEARNSTACK_NATIVE_JOB_SERVICES': '{"db":{}}'}, 'unsupported_job_topology'),
            ({'LEARNSTACK_NATIVE_JOB_SERVICES': '[]'}, 'unsupported_job_topology'),
            ({'LEARNSTACK_NATIVE_JOB_SERVICES': '"foreign"'}, 'unsupported_job_topology'),
            ({'LEARNSTACK_NATIVE_JOB_SERVICES': 'broken'}, 'unsupported_job_topology'),
            ({'LEARNSTACK_NATIVE_JOB_SERVICES': None}, 'unsupported_job_topology'),
            ({'LEARNSTACK_NATIVE_JOB_CONTAINER': 'broken'}, 'unsupported_job_topology'),
            ({'ImageOS': 'ubuntu22'}, 'unsupported_image'),
            ({'ImageVersion': 'PRIVATE-ENVIRONMENT-CANARY'}, 'unsupported_image'),
        ]
        for env, reason in cases:
            with self.subTest(env=env):
                self.assert_refused(self.fixture.run(env=env), reason)
                self.assertEqual([], self.fixture.commands())

    def test_actual_checkout_and_host_evidence_refuse_before_privilege(self):
        cases = [
            ({'sha': 'b' * 40}, 'checkout_mismatch'),
            ({'workspace': '/foreign'}, 'checkout_mismatch'),
            ({'uname': 'Darwin 25.0.0 arm64'}, 'unsupported_platform'),
            ({'uname': 'PRIVATE-PROVIDER-CANARY'}, 'unsupported_platform'),
            ({'uid': '0'}, 'unsupported_user'),
            ({'cpus': '1'}, 'unsupported_cpu_count'),
            ({'cpus': 'unknown'}, 'unsupported_cpu_count'),
            ({'cpus': '9' * 5000}, 'unsupported_cpu_count'),
            ({'container': 'docker', 'container_status': 0}, 'container_detected'),
            ({'container_status': 2}, 'container_detected'),
            ({'vm': 'none', 'vm_status': 1}, 'vm_evidence_missing'),
            ({'vm': 'docker'}, 'vm_evidence_missing'),
            ({'docker_status': 1}, 'docker_baseline_unreadable'),
        ]
        for config, reason in cases:
            with self.subTest(config=config):
                self.assert_refused(self.fixture.run(config), reason)

    def test_matching_but_unsupported_context_identity_still_refuses(self):
        for key, value, reason in [
            ('JOB', 'backend', 'unsupported_context'),
            ('REPOSITORY', 'foreign/repo', 'unsupported_context'),
            ('RUN_ID', '0', 'context_identity_mismatch'),
            ('RUN_ATTEMPT', '0', 'context_identity_mismatch'),
            ('SHA', 'foreign', 'context_identity_mismatch'),
            ('WORKFLOW_SHA', 'b' * 40, 'context_identity_mismatch'),
            ('WORKFLOW_REF', 'HodeTech/LearnStack/.github/workflows/ci.yml@refs/heads/foreign',
             'unsupported_workflow'),
            ('WORKSPACE', '/foreign', 'checkout_mismatch'),
        ]:
            copy_key = 'ATTEMPT' if key == 'RUN_ATTEMPT' else key
            with self.subTest(key=key):
                self.assert_refused(self.fixture.run(env={'GITHUB_' + key: value,
                                                         'LEARNSTACK_NATIVE_' + copy_key: value}), reason)
        self.assertEqual([], self.fixture.commands())

    def test_all_container_states_including_unknown_disprove_an_empty_baseline(self):
        for state in ('created', 'running', 'paused', 'restarting', 'removing', 'exited',
                      'dead', 'unknown', 'PRIVATE-PROVIDER-CANARY'):
            with self.subTest(state=state):
                self.assert_refused(self.fixture.run({'docker_output': state + '\n'}),
                                    'docker_baseline_nonempty')

    def test_container_markers_cgroup_and_ubuntu_contradictions_refuse(self):
        for marker in self.fixture.paths['CONTAINER_MARKERS']:
            with self.subTest(marker=marker):
                Path(marker).touch()
                self.assert_refused(self.fixture.run(), 'container_detected')
                Path(marker).unlink()
        cgroup = self.fixture.root / 'self-cgroup'
        for value, reason in [('0::/docker/foreign\n', 'container_detected'),
                              ('0::/kubepods/foreign\n', 'container_detected'),
                              ('invalid\n', 'container_evidence_unreadable')]:
            cgroup.write_text(value)
            self.assert_refused(self.fixture.run(), reason)
        cgroup.write_text('0::/runner.scope\n')
        for value in ('ID=debian\nVERSION_ID="24.04"\n',
                      'ID=ubuntu\nID=foreign\nVERSION_ID="24.04"\n',
                      'ID=ubuntu\nVERSION_ID="22.04"\n'):
            (self.fixture.root / 'os-release').write_text(value)
            self.assert_refused(self.fixture.run(), 'unsupported_platform')

    def test_unknown_malformed_and_unreadable_collectors_refuse_before_privilege(self):
        for original in ('', 'core\0\n', 'core\nother\n', 'core\n\n', 'core\r\n',
                         'x' * 4097, '|/tmp/apport PRIVATE-COLLECTOR-CANARY\n',
                         '|/usr/share/apport/apport-foreign %p\n', '/tmp/core\n',
                         '| /usr/share/apport/apport\n', '|/usr/share/apport/apport\t%p\n'):
            with self.subTest(length=len(original)):
                self.fixture.pattern.write_text(original)
                self.assert_refused(self.fixture.run(),
                                    'collector_unreadable' if len(original) in (0, 4097)
                                    else 'unsupported_collector')
        self.fixture.pattern.unlink()
        self.assert_refused(self.fixture.run(), 'collector_unreadable')

    def test_second_actual_read_detects_drift_and_never_writes(self):
        for config, reason in [({'drift_at': 1}, 'collector_drift'),
                               ({'read_fail_at': 1}, 'collector_unreadable')]:
            with self.subTest(config=config), tempfile.TemporaryDirectory() as root:
                fixture = Fixture(root)
                self.assertEqual({'ok': False, 'reason': reason}, fixture.run(config))
                self.assertFalse(fixture.authorized.exists())
                self.assertEqual(1, len(fixture.commands('sudo')))
                self.assertEqual([], fixture.commands('tee'))

    def test_probe_failure_never_attempts_a_write(self):
        self.assert_refused(self.fixture.run({'sudo_probe_fail': True}),
                            'sudo_capability_unavailable', before_privilege=False)
        self.assertEqual([], self.fixture.commands('tee'))

    def test_write_or_independent_readback_uncertainty_never_authorizes_launch_or_restore(self):
        for config, reason in [({'write_fail': True}, 'collector_write_failed'),
                               ({'timeout': 'tee', 'command_timeout': 0.5}, 'collector_write_failed'),
                               ({'partial_write': True}, 'collector_readback_mismatch'),
                               ({'read_fail_at': 2}, 'collector_readback_failed'),
                               ({'drift_at': 2}, 'collector_readback_mismatch')]:
            with self.subTest(config=config), tempfile.TemporaryDirectory() as root:
                fixture = Fixture(root)
                self.assertEqual({'ok': False, 'reason': reason}, fixture.run(config))
                self.assertFalse(fixture.authorized.exists())
                self.assertEqual(2, len(fixture.commands('sudo')))
                self.assertLessEqual(len(fixture.commands('tee')), 1)
                self.assertEqual([fixture.paths['TEE'], str(fixture.pattern)],
                                 fixture.commands('sudo')[-1]['args'][1:])

    def test_supported_original_readback_drift_remains_failure_without_write(self):
        self.fixture.pattern.write_text('core.%p\n')
        self.assert_refused(self.fixture.run({'drift_at': 2, 'drift_value': 'core\n'}),
                            'collector_readback_mismatch', before_privilege=False)
        self.assertEqual([], self.fixture.commands('tee'))

    def test_absolute_deadline_and_command_timeout_are_fail_closed(self):
        self.assert_refused(self.fixture.run({'deadline': -1}), 'deadline_exhausted')
        self.assertEqual([], self.fixture.commands())
        self.assert_refused(self.fixture.run({'command_timeout': 0}), 'invalid_budget')
        self.assert_refused(self.fixture.run({'timeout': 'uname', 'command_timeout': 0.5}),
                            'unsupported_platform')
        self.assert_refused(self.fixture.run({'timeout': 'uname', 'deadline': 0.5}),
                            'unsupported_platform')

    def test_standalone_production_cli_cannot_select_a_target_or_start_any_writer(self):
        result = subprocess.run([sys.executable, str(SOURCE), '--target', str(self.fixture.pattern),
                                 '--command', 'PRIVATE-ENVIRONMENT-CANARY'],
                                capture_output=True, text=True, timeout=5, check=False)
        self.assertEqual(1, result.returncode)
        self.assertEqual({'protocol': 1, 'error': 'launcher_required'}, json.loads(result.stdout))
        self.assertEqual('', result.stderr)
        self.assertEqual([], self.fixture.commands())
        self.assertIn('PRIVATE-COLLECTOR-CANARY', self.fixture.pattern.read_text())


if __name__ == '__main__':
    unittest.main()
