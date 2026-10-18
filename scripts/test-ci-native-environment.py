#!/usr/bin/env python3
"""Closed read-only diagnostics and workflow controls; no sudo/Docker invocation."""
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('native_environment', Path(__file__).with_name('ci-native-environment.py'))
probe = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(probe)


class NativeEnvironmentTests(unittest.TestCase):
    def test_collector_class_never_discloses_command_arguments(self):
        cases = {
            'core\n': 'supported_file', 'core.%p': 'supported_file',
            '|/usr/share/apport/apport private-canary': 'apport',
            '|/usr/lib/systemd/systemd-coredump private-canary': 'systemd_coredump',
            '|/lib/systemd/systemd-coredump private-canary': 'systemd_coredump',
            '|/tmp/apport private-canary': 'other_pipe',
            '/private-canary/core': 'other_file', '': 'invalid',
            'core\0': 'invalid', 'core\nother': 'invalid', 'core\n\n': 'invalid',
            'x' * 4097: 'invalid',
        }
        for value, expected in cases.items():
            with self.subTest(expected=expected):
                self.assertEqual(expected, probe.collector_class(value))

    def test_unsupported_context_starts_no_commands(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(probe.subprocess, 'run') as run, \
                patch('sys.stdout', new_callable=io.StringIO) as output:
            self.assertEqual(1, probe.main())
            run.assert_not_called()
            self.assertEqual({'protocol': 1, 'error': 'unsupported_context'}, json.loads(output.getvalue()))

    def test_actual_observer_uses_only_fixed_read_only_commands_and_closed_states(self):
        commands = []
        def run(argv, **kwargs):
            commands.append(argv)
            self.assertEqual(subprocess.DEVNULL, kwargs['stdin'])
            self.assertEqual(3, kwargs['timeout'])
            self.assertNotIn('shell', kwargs)
            return subprocess.CompletedProcess(argv, 0, 'running\npaused\nrestarting\nremoving\nforeign-canary\n'
                                               if argv[0] == '/usr/bin/docker' else 'private-canary')
        with patch.dict(os.environ, {'GITHUB_ACTIONS': 'true', 'LEARNSTACK_RUNNER_CONTEXT': 'github-hosted'}), \
                patch.object(probe.sys, 'platform', 'linux'), \
                patch.object(probe, 'read_observation', return_value='|/usr/share/apport/apport private-canary'), \
                patch.object(probe.resource, 'getrlimit', return_value=(0, 0)), \
                patch.object(probe.subprocess, 'run', side_effect=run):
            result = probe.observe()
        self.assertEqual([
            ['/usr/bin/sudo', '-n', '/usr/bin/true'],
            ['/usr/bin/systemd-detect-virt', '--container'],
            ['/usr/bin/systemd-detect-virt', '--vm'],
            ['/usr/bin/docker', 'ps', '--all', '--format', '{{.State}}'],
        ], commands)
        self.assertEqual('apport', result['collector'])
        self.assertEqual('zero', result['softCore'])
        self.assertEqual(1, result['dockerStates']['unknown'])
        self.assertNotIn('canary', json.dumps(result))

    def test_command_failure_has_no_provider_diagnostics(self):
        with patch.object(probe.subprocess, 'run', side_effect=subprocess.TimeoutExpired('private-canary', 3)):
            self.assertEqual((None, ''), probe.command(['/usr/bin/docker', 'ps']))

    def test_workflow_preserves_backend_and_observes_both_docker_partitions(self):
        source = (ROOT / '.github/workflows/ci.yml').read_text()
        block = source.split('  backend:\n', 1)[1].split('\n  # ───', 1)[0]
        self.assertIn('--filter "Requires!=Docker"', block)
        self.assertIn('--logger "console;verbosity=normal"', block)
        self.assertIn('--logger trx', block)
        for name, mode in [('backend-integration-tests', 'ordinary'), ('backend-native', 'native')]:
            block = source.split('  ' + name + ':\n', 1)[1].split('\n  # ───', 1)[0]
            self.assertIn('timeout-minutes: 20', block)
            self.assertIn('LEARNSTACK_RUNNER_CONTEXT: ${{ runner.environment }}', block)
            self.assertLess(block.index('python3 scripts/ci-native-environment.py'),
                            block.index('python3 scripts/ci-native-tests.py ' + mode))
        self.assertIn('python3 scripts/test-ci-native-environment.py', source)


if __name__ == '__main__':
    unittest.main()
