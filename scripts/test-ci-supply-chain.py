#!/usr/bin/env python3
"""Exercise scanner checksum admission and the actual workflow dependency pins."""
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location('install_leakwatch', ROOT / 'scripts/install-leakwatch.py')
INSTALLER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(INSTALLER)


def unpinned_actions(source):
    return [value for value in re.findall(r'^\s+uses:\s+(\S+)', source, re.MULTILINE)
            if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}', value)]


class SupplyChainControls(unittest.TestCase):
    def metadata(self):
        return {'Path': INSTALLER.MODULE, 'Version': INSTALLER.VERSION,
                'Sum': INSTALLER.SUM, 'GoModSum': INSTALLER.GO_MOD_SUM}

    def test_all_actual_workflow_actions_use_full_commit_identity(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        self.assertTrue(re.search(r'^\s+uses:', workflow, re.MULTILINE))
        self.assertEqual([], unpinned_actions(workflow))
        self.assertEqual(['actions/checkout@v4'], unpinned_actions(
            '        uses: actions/checkout@v4\n'))
        self.assertEqual(['actions/checkout@abc123'], unpinned_actions(
            '        uses: actions/checkout@abc123\n'))

    def test_valid_module_installs_only_after_both_sums_and_identity_are_verified(self):
        result = subprocess.CompletedProcess([], 0, json.dumps(self.metadata()))
        with patch.object(INSTALLER.subprocess, 'run', return_value=result) as run:
            INSTALLER.main()
        target = INSTALLER.MODULE + '@' + INSTALLER.VERSION
        self.assertEqual(['go', 'mod', 'download', '-json', target], run.call_args_list[0].args[0])
        self.assertEqual(['go', 'install', target], run.call_args_list[1].args[0])
        self.assertEqual(2, run.call_count)

    def test_wrong_module_version_or_either_sum_never_reaches_install(self):
        for key in self.metadata():
            with self.subTest(key=key):
                metadata = self.metadata()
                metadata[key] += '-changed'
                result = subprocess.CompletedProcess([], 0, json.dumps(metadata))
                with patch.object(INSTALLER.subprocess, 'run', return_value=result) as run:
                    with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
                        INSTALLER.main()
                self.assertEqual(1, run.call_count)

    def test_malformed_download_metadata_never_reaches_install(self):
        for source in ('[]', 'null', '{'):
            with self.subTest(source=source):
                with patch.object(INSTALLER.subprocess, 'run', return_value=
                                  subprocess.CompletedProcess([], 0, source)) as run:
                    with self.assertRaises(ValueError):
                        INSTALLER.main()
                self.assertEqual(1, run.call_count)

    def test_module_download_failure_never_reaches_install(self):
        with patch.object(INSTALLER.subprocess, 'run', side_effect=
                          subprocess.CalledProcessError(1, ['go'])) as run:
            with self.assertRaises(subprocess.CalledProcessError):
                INSTALLER.main()
        self.assertEqual(1, run.call_count)

    def test_ambient_settings_cannot_disable_checksum_database_for_either_command(self):
        with patch.dict(INSTALLER.os.environ, {'GOSUMDB': 'off', 'GOPROXY': 'direct',
                                             'GOPRIVATE': '*', 'GONOSUMDB': '*', 'GONOPROXY': '*',
                                             'GOENV': '/ambient/go/settings'}):
            with patch.object(INSTALLER.subprocess, 'run', return_value=
                              subprocess.CompletedProcess([], 0, json.dumps(self.metadata()))) as run:
                INSTALLER.main()
        for call in run.call_args_list:
            environment = call.kwargs['env']
            self.assertEqual('sum.golang.org', environment['GOSUMDB'])
            self.assertEqual('https://proxy.golang.org', environment['GOPROXY'])
            for key in ('GOPRIVATE', 'GONOSUMDB', 'GONOPROXY'):
                self.assertEqual('', environment[key])
            self.assertEqual('off', environment['GOWORK'])
            self.assertEqual('off', environment['GOENV'])

    def test_actual_workflow_uses_verified_installer_and_requires_sarif(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        self.assertIn('run: python3 scripts/install-leakwatch.py\n', workflow)
        self.assertIn('run: python3 scripts/test-ci-supply-chain.py\n', workflow)
        sarif = workflow.split('      - name: Upload SARIF artifact\n', 1)[1]
        self.assertIn('          if-no-files-found: error\n', sarif)
        self.assertNotIn('          if-no-files-found: warn\n', sarif)


if __name__ == '__main__':
    unittest.main()
