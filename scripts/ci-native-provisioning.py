#!/usr/bin/env python3
"""Closed, one-way ADR-0062 setup, called only by the native CI launcher.

The trusted workflow and GitHub's disposable hosted-VM contract supply authority.
These checks contain accidents; they do not attest a VM against malicious code
that already has the runner's passwordless sudo capability.
"""
import json
import math
import os
from pathlib import Path
import re
import resource
import subprocess
import sys
import time

# Offline controls replace only these fixed paths in a byte-verified copy.
CORE_PATTERN = '/proc/sys/kernel/core_pattern'
OS_RELEASE = '/etc/os-release'
CGROUP_INIT = '/proc/1/cgroup'
CGROUP_SELF = '/proc/self/cgroup'
CONTAINER_MARKERS = ('/.dockerenv', '/run/.containerenv', '/run/systemd/container')
HEAD = '/usr/bin/head'
UNAME = '/usr/bin/uname'
NPROC = '/usr/bin/nproc'
ID = '/usr/bin/id'
GIT = '/usr/bin/git'
VIRT = '/usr/bin/systemd-detect-virt'
DOCKER = '/usr/bin/docker'
SUDO = '/usr/bin/sudo'
TRUE = '/usr/bin/true'
TEE = '/usr/bin/tee'


class Refusal(Exception):
    """A closed reason only; never carry provider output or collector arguments."""

    def __init__(self, reason):
        self.reason = reason
        super().__init__(reason)


def require(condition, reason):
    if not condition:
        raise Refusal(reason)


class Budget:
    def __init__(self, deadline, command_timeout):
        require(isinstance(deadline, (int, float)) and math.isfinite(deadline)
                and isinstance(command_timeout, (int, float))
                and math.isfinite(command_timeout) and command_timeout > 0,
                'invalid_budget')
        self.deadline = deadline
        self.command_timeout = command_timeout

    def remaining(self):
        remaining = self.deadline - time.monotonic()
        require(remaining > 0, 'deadline_exhausted')
        return min(remaining, self.command_timeout)

    def command(self, argv, reason, *, write=False):
        # No inherited Docker/Git configuration, shell, provider stderr or raw
        # collector output can escape through subprocess diagnostics.
        try:
            result = subprocess.run(
                argv, input=b'core\n' if write else None,
                stdin=None if write else subprocess.DEVNULL,
                stdout=subprocess.DEVNULL if write else subprocess.PIPE,
                stderr=subprocess.DEVNULL, check=False,
                timeout=self.remaining(),
                env={'PATH': '/usr/bin:/bin', 'LANG': 'C.UTF-8', 'LC_ALL': 'C.UTF-8',
                     'GIT_CONFIG_NOSYSTEM': '1', 'GIT_CONFIG_GLOBAL': '/dev/null'},
            )
        except (OSError, subprocess.TimeoutExpired):
            raise Refusal(reason) from None
        self.remaining()
        require(write or len(result.stdout) <= 16384, reason)
        return result.returncode, b'' if write else result.stdout

    def read(self, path, reason, limit=4096):
        status, value = self.command([HEAD, '--bytes=' + str(limit + 1), path], reason)
        require(status == 0 and 0 < len(value) <= limit, reason)
        try:
            return value.decode('ascii')
        except UnicodeError:
            raise Refusal(reason) from None


def collector_class(value):
    """Classify actual bytes without retaining or reporting the collector argv."""
    require(0 < len(value) <= 4096, 'unsupported_collector')
    value = value[:-1] if value.endswith('\n') else value
    require(value and all(32 <= ord(char) <= 126 for char in value),
            'unsupported_collector')
    if value in ('core', 'core.%p'):
        return 'supported_file'
    for executable, kind in (
        ('/usr/share/apport/apport', 'apport'),
        ('/usr/lib/systemd/systemd-coredump', 'systemd_coredump'),
        ('/lib/systemd/systemd-coredump', 'systemd_coredump'),
    ):
        if value == '|' + executable or value.startswith('|' + executable + ' '):
            return kind
    raise Refusal('unsupported_collector')


def workflow_context():
    env = os.environ
    require(env.get('GITHUB_ACTIONS') == 'true', 'unsupported_context')
    for name, expected in (('CONTEXT', 'github-hosted'), ('OS', 'Linux'),
                           ('ARCH', 'X64'), ('LABEL', 'ubuntu-24.04')):
        require(env.get('LEARNSTACK_NATIVE_RUNNER_' + name) == expected,
                'unsupported_context')
    require(env.get('RUNNER_OS') == 'Linux' and env.get('RUNNER_ARCH') == 'X64',
            'unsupported_context')
    require(env.get('RUNNER_ENVIRONMENT') == 'github-hosted',
            'unsupported_context')
    for name in ('RUN_ID', 'RUN_ATTEMPT', 'JOB', 'SHA', 'REPOSITORY',
                 'WORKFLOW_REF', 'WORKFLOW_SHA', 'WORKSPACE'):
        actual = env.get('GITHUB_' + name)
        # The workflow uses ATTEMPT for readability; GitHub names RUN_ATTEMPT.
        copy_name = 'ATTEMPT' if name == 'RUN_ATTEMPT' else name
        require(actual and env.get('LEARNSTACK_NATIVE_' + copy_name) == actual,
                'context_identity_mismatch')
    require(env['GITHUB_JOB'] == 'backend-native'
            and env['GITHUB_REPOSITORY'] == 'HodeTech/LearnStack',
            'unsupported_context')
    require(re.fullmatch(r'[1-9][0-9]*', env['GITHUB_RUN_ID']) is not None
            and re.fullmatch(r'[1-9][0-9]*', env['GITHUB_RUN_ATTEMPT']) is not None
            and re.fullmatch(r'[0-9a-f]{40}', env['GITHUB_SHA']) is not None
            and env['GITHUB_WORKFLOW_SHA'] == env['GITHUB_SHA'],
            'context_identity_mismatch')
    require(re.fullmatch(
        r'HodeTech/LearnStack/\.github/workflows/ci\.yml@refs/'
        r'(heads/(main|development)|pull/[1-9][0-9]*/merge)',
        env['GITHUB_WORKFLOW_REF']) is not None, 'unsupported_workflow')
    try:
        container = json.loads(env.get('LEARNSTACK_NATIVE_JOB_CONTAINER', ''))
        services = json.loads(env.get('LEARNSTACK_NATIVE_JOB_SERVICES', ''))
    except (ValueError, TypeError):
        raise Refusal('unsupported_job_topology') from None
    require(container in (None, {}) and services in (None, {}), 'unsupported_job_topology')
    workspace = Path(env['GITHUB_WORKSPACE'])
    try:
        require(workspace.is_absolute()
                and workspace.resolve(strict=True) == Path(__file__).resolve().parents[1]
                and Path.cwd().resolve() == workspace.resolve(), 'checkout_mismatch')
    except (OSError, ValueError, RuntimeError):
        raise Refusal('checkout_mismatch') from None
    require(env.get('ImageOS') == 'ubuntu24'
            and re.fullmatch(r'[0-9]{8}\.[0-9]+\.[0-9]+', env.get('ImageVersion', ''))
            is not None, 'unsupported_image')
    return workspace


def qualify_runtime(budget, workspace):
    status, value = budget.command([GIT, 'rev-parse', '--verify', 'HEAD'], 'checkout_mismatch')
    require(status == 0 and value == (os.environ['GITHUB_SHA'] + '\n').encode(),
            'checkout_mismatch')
    status, value = budget.command([GIT, 'rev-parse', '--show-toplevel'], 'checkout_mismatch')
    require(status == 0 and value == (str(workspace.resolve()) + '\n').encode(),
            'checkout_mismatch')
    status, value = budget.command([UNAME, '-s', '-r', '-m'], 'unsupported_platform')
    try:
        kernel, release, machine = value.decode('ascii').strip().split(' ')
    except (UnicodeError, ValueError):
        raise Refusal('unsupported_platform') from None
    require(status == 0 and kernel == 'Linux' and machine == 'x86_64'
            and re.fullmatch(r'[A-Za-z0-9._+-]{1,128}', release) is not None,
            'unsupported_platform')
    os_release = budget.read(OS_RELEASE, 'unsupported_platform', 16384)
    require(re.findall(r'^ID=(.*)$', os_release, re.MULTILINE) in (['ubuntu'], ['"ubuntu"'])
            and re.findall(r'^VERSION_ID=(.*)$', os_release, re.MULTILINE)
            in (['24.04'], ['"24.04"']), 'unsupported_platform')
    status, value = budget.command([ID, '-u'], 'unsupported_user')
    require(status == 0 and re.fullmatch(rb'[1-9][0-9]*\n', value) is not None,
            'unsupported_user')
    status, value = budget.command([NPROC], 'unsupported_cpu_count')
    require(status == 0 and re.fullmatch(rb'[1-9][0-9]{0,5}\n', value) is not None
            and int(value) > 1, 'unsupported_cpu_count')
    cpus = int(value)
    for marker in CONTAINER_MARKERS:
        try:
            Path(marker).lstat()
        except FileNotFoundError:
            continue
        except OSError:
            raise Refusal('container_evidence_unreadable') from None
        raise Refusal('container_detected')
    for path in (CGROUP_INIT, CGROUP_SELF):
        cgroup = budget.read(path, 'container_evidence_unreadable')
        require(re.fullmatch(r'0::/[A-Za-z0-9_./:@-]*\n', cgroup) is not None,
                'container_evidence_unreadable')
        require(not re.search(r'docker|containerd|kubepods|lxc|libpod', cgroup,
                              re.IGNORECASE), 'container_detected')
    status, value = budget.command([VIRT, '--container'], 'container_evidence_unreadable')
    require(status == 1 and value == b'none\n', 'container_detected')
    status, value = budget.command([VIRT, '--vm'], 'vm_evidence_missing')
    require(status == 0 and value in (b'microsoft\n', b'kvm\n', b'qemu\n'),
            'vm_evidence_missing')
    vm = value.decode('ascii').strip()
    status, value = budget.command(
        [DOCKER, 'ps', '--all', '--last', '1', '--format', '{{.State}}'],
        'docker_baseline_unreadable')
    require(status == 0, 'docker_baseline_unreadable')
    # The most recent container in *any* state is enough to disprove emptiness.
    require(value == b'', 'docker_baseline_nonempty')
    return {'cpus': cpus, 'kernelRelease': release, 'virtualization': vm}


def provision(*, deadline, command_timeout):
    """Return a sanitized receipt only after exact, independently read setup."""
    budget = Budget(deadline, command_timeout)
    budget.remaining()
    workspace = workflow_context()
    try:
        resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
        require(resource.getrlimit(resource.RLIMIT_CORE) == (0, 0), 'core_limits_not_zero')
    except (OSError, ValueError):
        raise Refusal('core_limits_not_zero') from None
    runtime = qualify_runtime(budget, workspace)
    original = budget.read(CORE_PATTERN, 'collector_unreadable')
    kind = collector_class(original)
    # All context, topology, baseline and collector refusals precede privilege.
    status, _ = budget.command([SUDO, '-n', TRUE], 'sudo_capability_unavailable')
    require(status == 0, 'sudo_capability_unavailable')
    require(budget.read(CORE_PATTERN, 'collector_unreadable') == original, 'collector_drift')
    changed = kind != 'supported_file'
    if changed:
        status, _ = budget.command([SUDO, '-n', TEE, CORE_PATTERN],
                                   'collector_write_failed', write=True)
        require(status == 0, 'collector_write_failed')
    actual = budget.read(CORE_PATTERN, 'collector_readback_failed')
    require(actual == ('core\n' if changed else original), 'collector_readback_mismatch')
    collector_class(actual)
    budget.remaining()
    return {
        'protocol': 1, 'scope': 'native_ci_setup_not_child_qualification',
        'collectorBefore': kind, 'collectorAfter': 'supported_file', 'changed': changed,
        'softCore': 'zero', 'hardCore': 'zero', 'imageOS': os.environ['ImageOS'],
        'imageVersion': os.environ['ImageVersion'], 'sourceSha': os.environ['GITHUB_SHA'],
        'runId': os.environ['GITHUB_RUN_ID'], 'runAttempt': os.environ['GITHUB_RUN_ATTEMPT'],
        'job': 'backend-native', **runtime,
    }


def main():
    # No standalone writer, argument parser or caller-selected path/key/command.
    print('{"protocol":1,"error":"launcher_required"}')
    return 1


if __name__ == '__main__':
    sys.exit(main())
