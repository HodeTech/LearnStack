#!/usr/bin/env python3
"""Read-only CI observations; this neither provisions nor qualifies a crash host."""
import json
import os
from pathlib import Path
import resource
import subprocess
import sys


def collector_class(value):
    if not value or len(value) > 4096 or '\0' in value:
        return 'invalid'
    value = value[:-1] if value.endswith('\n') else value
    if '\n' in value:
        return 'invalid'
    if value in ('core', 'core.%p'):
        return 'supported_file'
    if not value.startswith('|'):
        return 'other_file'
    words = value[1:].split()
    executable = words[0] if words else ''
    if executable == '/usr/share/apport/apport':
        return 'apport'
    if executable in ('/usr/lib/systemd/systemd-coredump', '/lib/systemd/systemd-coredump'):
        return 'systemd_coredump'
    return 'other_pipe'


def read_observation(path):
    try:
        with Path(path).open(encoding='utf-8') as stream:
            return stream.read(4097)
    except (OSError, UnicodeError):
        return None


def command(argv):
    try:
        result = subprocess.run(argv, stdin=subprocess.DEVNULL, capture_output=True,
                                text=True, timeout=3, check=False)
        return result.returncode, result.stdout
    except (OSError, UnicodeError, subprocess.TimeoutExpired):
        return None, ''


def limit_class(value):
    return 'unlimited' if value == resource.RLIM_INFINITY else 'zero' if value == 0 else 'positive'


def observe():
    # Accident guard only. PR code already executes as this user, with hosted
    # sudo; environment strings and these observations are not VM attestation.
    if (sys.platform != 'linux' or os.environ.get('GITHUB_ACTIONS') != 'true'
            or os.environ.get('LEARNSTACK_RUNNER_CONTEXT') != 'github-hosted'):
        raise ValueError('unsupported_context')
    pattern = read_observation('/proc/sys/kernel/core_pattern')
    soft, hard = resource.getrlimit(resource.RLIMIT_CORE)
    sudo, _ = command(['/usr/bin/sudo', '-n', '/usr/bin/true'])
    container, _ = command(['/usr/bin/systemd-detect-virt', '--container'])
    vm, _ = command(['/usr/bin/systemd-detect-virt', '--vm'])
    docker, states = command(['/usr/bin/docker', 'ps', '--all', '--format', '{{.State}}'])
    counts = {key: 0 for key in ('created', 'running', 'paused', 'restarting',
                                'removing', 'exited', 'dead', 'unknown')}
    if docker == 0:
        for state in states.splitlines():
            counts[state if state in counts else 'unknown'] += 1
    try:
        proc_read_only = bool(os.statvfs('/proc/sys').f_flag & os.ST_RDONLY)
    except OSError:
        proc_read_only = None
    return {
        'protocol': 1, 'scope': 'read_only_observation_not_qualification',
        'collector': 'unreadable' if pattern is None else collector_class(pattern),
        'softCore': limit_class(soft), 'hardCore': limit_class(hard),
        'cpus': os.cpu_count(),
        'containerMarker': Path('/.dockerenv').exists() or Path('/run/.containerenv').exists(),
        'containerReported': True if container == 0 else False if container == 1 else None,
        'vmReported': True if vm == 0 else False if vm == 1 else None,
        'nonInteractiveSudoTrue': sudo == 0,
        'procSysMountReadOnly': proc_read_only,
        'corePatternWritableByCaller': os.access('/proc/sys/kernel/core_pattern', os.W_OK),
        'dockerCensusReadable': docker == 0,
        'dockerStates': counts if docker == 0 else None,
    }


def main():
    try:
        result = observe()
    except ValueError:
        print('{"protocol":1,"error":"unsupported_context"}')
        return 1
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == '__main__':
    sys.exit(main())
