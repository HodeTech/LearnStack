// @vitest-environment node
import { mkdtemp, mkdir, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import {
  discoverTestPackages,
  runTestWorkspaces,
  validateTestReport,
} from '../../../../../scripts/run-frontend-tests.mjs';

const frontend = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');

function report() {
  return {
    success: true,
    numTotalTests: 1,
    numPassedTests: 1,
    numFailedTests: 0,
    numPendingTests: 0,
    numTodoTests: 0,
    numTotalTestSuites: 1,
    numPassedTestSuites: 1,
    numFailedTestSuites: 0,
    numPendingTestSuites: 0,
    testResults: [{ status: 'passed', assertionResults: [{ status: 'passed' }] }],
  };
}

/** Actual runner controls for ADR-0053 G38(d), with separate test-owned workspaces. */
describe('frontend outcome guard', () => {
  it('accepts a nonempty complete report', () => expect(validateTestReport(report())).toBe(1));

  it.each([
    undefined,
    {},
    { ...report(), success: false },
    { ...report(), numTotalTests: 0, numPassedTests: 0 },
    { ...report(), numFailedTests: 1 },
    { ...report(), numPendingTests: 1 },
    { ...report(), numTodoTests: 1 },
    { ...report(), numTotalTests: 2, numPassedTests: 2 },
    { ...report(), numTotalTests: 1.5 },
    { ...report(), numTotalTestSuites: 0 },
    { ...report(), numPendingTestSuites: 1 },
    { ...report(), testResults: [] },
    { ...report(), testResults: [{ status: 'passed', assertionResults: [] }] },
    { ...report(), testResults: [{ status: 'passed', assertionResults: [{ status: 'pending' }] }] },
    { ...report(), testResults: [{ status: 'failed', assertionResults: [{ status: 'passed' }] }] },
  ])('refuses incomplete, unexecuted or contradictory report %#', (value) => {
    expect(() => validateTestReport(value)).toThrow();
  });

  it.each([
    'clean',
    'skip',
    'todo',
    'missing-script',
    'missing-report',
    'invalid-report',
    'omitted-file',
  ])(
    'executes the actual workspace runner with planted %s',
    async (mode) => {
      const root = await mkdtemp(join(tmpdir(), 'learnstack-frontend-control-'));
      try {
        const pkg = join(root, 'packages/probe');
        await mkdir(pkg, { recursive: true });
        await writeFile(join(root, 'pnpm-workspace.yaml'), "packages:\n  - 'packages/*'\n");
        await symlink(
          join(frontend, 'packages/sdk/node_modules'),
          join(pkg, 'node_modules'),
          'dir',
        );
        const script =
          mode === 'missing-report'
            ? 'node -e "process.exit(0)"'
            : mode === 'invalid-report'
              ? 'node broken-report.mjs'
              : mode === 'omitted-file'
                ? 'vitest run probe.test.ts'
                : 'vitest run';
        await writeFile(
          join(pkg, 'package.json'),
          JSON.stringify({
            name: 'probe',
            private: true,
            type: 'module',
            scripts: mode === 'missing-script' ? {} : { test: script },
          }),
        );
        await writeFile(
          join(pkg, 'probe.test.ts'),
          "import { it, expect } from 'vitest';\nit('clean control', () => expect(1).toBe(1));\n" +
            (mode === 'skip'
              ? "it.skip('planted skip', () => {});\n"
              : mode === 'todo'
                ? "it.todo('planted todo');\n"
                : ''),
        );
        await writeFile(
          join(pkg, 'broken-report.mjs'),
          "import { writeFileSync } from 'node:fs';\n" +
            "writeFileSync(process.argv.find(x=>x.startsWith('--outputFile=')).slice(13), '{');\n",
        );
        if (mode === 'omitted-file')
          await writeFile(
            join(pkg, 'omitted.test.ts'),
            "import { it } from 'vitest';\nit('discovered but omitted', () => {});\n",
          );
        const result = runTestWorkspaces(root, () => {});
        if (mode === 'clean') await expect(result).resolves.toEqual({ packages: 1, tests: 1 });
        else await expect(result).rejects.toThrow();
      } finally {
        await rm(root, { recursive: true, force: true });
      }
    },
    30_000,
  );

  it('discovers the actual web and SDK test packages', async () => {
    const packages = await discoverTestPackages(frontend);
    expect(packages.map((pkg) => pkg.name)).toEqual(['apps/web', 'packages/sdk']);
    expect(packages.every((pkg) => pkg.files.length > 0)).toBe(true);
  });
});
