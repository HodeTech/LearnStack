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

    def test_workflow_preserves_complete_owners_and_native_only_environment_observation(self):
        spec = importlib.util.spec_from_file_location('ci_environment_topology', ROOT / 'scripts/ci-test-shard-results.py')
        result = importlib.util.module_from_spec(spec); spec.loader.exec_module(result)
        jobs = result.source_topology(result.PLAN.load_plan())
        observations = [(name, index, step) for name, job in jobs.items()
                        for index, step in enumerate(job.get('steps', []))
                        if 'scripts/ci-native-environment.py' in step.get('run', '')]
        self.assertEqual(1, len(observations))
        name, index, step = observations[0]
        self.assertEqual('backend-native', name)
        self.assertEqual('${{ runner.environment }}', step['env']['LEARNSTACK_RUNNER_CONTEXT'])
        execution = next(i for i, item in enumerate(jobs[name]['steps'])
                         if item.get('name') == result.EXECUTE_STEP)
        self.assertLess(index, execution)
        self.assertTrue(any(step.get('run') == 'python3 scripts/test-ci-native-environment.py'
                            for step in jobs['meta']['steps']))


if __name__ == '__main__':
    unittest.main()
