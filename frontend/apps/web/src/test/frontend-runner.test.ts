// @vitest-environment node
import { spawnSync } from 'node:child_process';
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
  it('reports bounded categories for actual failed cases without exporting their payloads', async () => {
    const root = await mkdtemp(join(tmpdir(), 'learnstack-frontend-failure-control-'));
    try {
      const pkg = join(root, 'packages/probe');
      await mkdir(pkg, { recursive: true });
      await writeFile(join(root, 'pnpm-workspace.yaml'), "packages:\n  - 'packages/*'\n");
      await symlink(join(frontend, 'packages/sdk/node_modules'), join(pkg, 'node_modules'), 'dir');
      await writeFile(
        join(pkg, 'package.json'),
        JSON.stringify({
          name: 'probe',
          private: true,
          type: 'module',
          scripts: { test: 'vitest run' },
        }),
      );
      await writeFile(
        join(pkg, 'probe.test.ts'),
        "import { it, expect } from 'vitest';\n" +
          "it('passing control', () => expect(1).toBe(1));\n" +
          "it('assertion control', () => expect('PRIVATE_FAILURE_CANARY').toBe('different'));\n" +
          "it('timeout control', () => new Promise(() => {}), 30);\n" +
          "it('error control', () => { throw new Error('PRIVATE_FAILURE_CANARY'); });\n",
      );
      let output = '';
      await expect(
        runTestWorkspaces(root, (chunk) => {
          output += chunk.toString();
        }),
      ).rejects.toThrow('Frontend test package exited unsuccessfully');
      const lines = output
        .split(/\r?\n/)
        .filter((line) => line.startsWith('Frontend diagnostic: '));
      expect(lines).toHaveLength(3);
      const rows = lines.map(
        (line) =>
          JSON.parse(line.slice('Frontend diagnostic: '.length)) as {
            kind: string;
            file: string;
            caseId: string;
            categories: Record<string, number>;
          },
      );
      expect(
        rows.every((row) => row.kind === 'frontend-test-failure' && row.file === 'probe.test.ts'),
      ).toBe(true);
      expect(new Set(rows.map((row) => row.caseId)).size).toBe(3);
      expect(rows.map((row) => row.categories)).toEqual([
        { assertion: 1, testTimeout: 0, hookTimeout: 0, other: 0 },
        { assertion: 0, testTimeout: 1, hookTimeout: 0, other: 0 },
        { assertion: 0, testTimeout: 0, hookTimeout: 0, other: 1 },
      ]);
      expect(lines.join('\n')).not.toContain('PRIVATE_FAILURE_CANARY');
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  }, 30_000);

  it('bounds immediate failure diagnostics and excludes dynamic names and malformed fields', () => {
    const reporter = new URL(
      '../../../../../scripts/frontend-test-failure-reporter.mjs',
      import.meta.url,
    ).href;
    const result = spawnSync(
      process.execPath,
      [
        '--input-type=module',
        '-e',
        `
      import Reporter from ${JSON.stringify(reporter)};
      const reporter = new Reporter();
      const canary = 'PRIVATE_FAILURE_CANARY';
      const names = [
        'closes actual matcher, Next routing hooks, framework entry points and Server Actions',
        'confines browser fetch to the exact same-origin no-store BFF extension action',
      ];
      reporter.onTestCaseResult({ result: () => ({ state: 'passed' }) });
      for (let index = 0; index < 132; index++) reporter.onTestCaseResult({
        result: () => ({ state: 'failed', errors: Array.from({ length: 65 }, () => ({ name: 'AssertionError', message: canary, stack: canary, actual: canary, expected: canary })) }),
        diagnostic: () => ({ duration: Infinity }), location: { line: -1 },
        id: canary, name: names[index - 126] ?? canary,
        module: { relativeModuleId: '../' + canary + '.test.ts' },
      });
    `,
      ],
      { encoding: 'utf8', timeout: 5000, maxBuffer: 256 * 1024 },
    );
    expect(result.error).toBeUndefined();
    expect(result.signal).toBeNull();
    expect(result.status).toBe(0);
    expect(result.stdout).toBe('');
    expect(result.stderr).not.toContain('PRIVATE_FAILURE_CANARY');
    const rows = result.stderr
      .trim()
      .split('\n')
      .filter(Boolean)
      .map((line) => {
        expect(line.startsWith('Frontend diagnostic: ')).toBe(true);
        return JSON.parse(line.slice('Frontend diagnostic: '.length)) as {
          kind: string;
          file: string;
          line: number | null;
          durationMs: number | null;
          case: string;
          caseId: string;
          categories: Record<string, number>;
          errorsTruncated: boolean;
        };
      });
    expect(rows).toHaveLength(129);
    expect(rows[128]).toEqual({ schema: 1, kind: 'frontend-test-failures-truncated' });
    for (const row of rows.slice(0, 128)) {
      expect(row.file).toBe('unavailable');
      expect(row.line).toBeNull();
      expect(row.durationMs).toBeNull();
      expect(row.caseId).toMatch(/^[a-f0-9]{16}$/);
      expect(row.categories).toEqual({ assertion: 64, testTimeout: 0, hookTimeout: 0, other: 0 });
      expect(row.errorsTruncated).toBe(true);
    }
    expect(rows.slice(126, 128).map((row) => row.case)).toEqual([
      'public-http-surface',
      'public-browser-fetch',
    ]);
  });

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
    'empty-report',
    'unreadable-report',
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
        const script = mode.endsWith('-report')
          ? 'node report-probe.mjs'
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
          join(pkg, 'report-probe.mjs'),
          "import { writeFileSync, mkdirSync } from 'node:fs';\n" +
            "const path=process.argv.find(x=>x.startsWith('--outputFile=')).slice(13);\n" +
            (mode === 'invalid-report'
              ? "writeFileSync(path, '{');\n"
              : mode === 'unreadable-report'
                ? 'mkdirSync(path);\n'
                : mode === 'empty-report'
                  ? `writeFileSync(path, ${JSON.stringify(JSON.stringify({ ...report(), numTotalTests: 0, numPassedTests: 0 }))});\n`
                  : '// Exit successfully without a report; appended arguments are script data.\n'),
        );
        if (mode === 'omitted-file')
          await writeFile(
            join(pkg, 'omitted.test.ts'),
            "import { it } from 'vitest';\nit('discovered but omitted', () => {});\n",
          );
        const result = runTestWorkspaces(root, () => {});
        if (mode === 'clean') await expect(result).resolves.toEqual({ packages: 1, tests: 1 });
        else if (mode === 'missing-report')
          await expect(result).rejects.toMatchObject({ code: 'ENOENT' });
        else if (mode === 'unreadable-report')
          await expect(result).rejects.toMatchObject({ code: 'EISDIR' });
        else if (mode === 'invalid-report')
          await expect(result).rejects.toBeInstanceOf(SyntaxError);
        else if (mode === 'missing-script')
          await expect(result).rejects.toThrow('Discovered tests without an execution script');
        else if (mode === 'omitted-file')
          await expect(result).rejects.toThrow('Frontend report omits a discovered test file');
        else
          await expect(result).rejects.toThrow(
            'Frontend test report is empty, incomplete, failed, skipped or todo',
          );
      } finally {
        await rm(root, { recursive: true, force: true });
      }
    },
    30_000,
  );

  it('refuses a symlinked workspace package instead of skipping its tests', async () => {
    const root = await mkdtemp(join(tmpdir(), 'learnstack-workspace-symlink-'));
    try {
      await mkdir(join(root, 'packages'), { recursive: true });
      await mkdir(join(root, 'actual'), { recursive: true });
      await writeFile(join(root, 'pnpm-workspace.yaml'), "packages:\n  - 'packages/*'\n");
      await symlink(join(root, 'actual'), join(root, 'packages/probe'), 'dir');
      await expect(discoverTestPackages(root)).rejects.toThrow(
        'Symlinked workspace package is unsupported: packages/probe',
      );
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it('refuses symlinked test sources instead of silently omitting them', async () => {
    const root = await mkdtemp(join(tmpdir(), 'learnstack-source-symlink-'));
    try {
      const pkg = join(root, 'packages/probe');
      await mkdir(pkg, { recursive: true });
      await writeFile(join(root, 'pnpm-workspace.yaml'), "packages:\n  - 'packages/*'\n");
      await writeFile(
        join(pkg, 'package.json'),
        JSON.stringify({ scripts: { test: 'vitest run' } }),
      );
      await writeFile(join(root, 'actual.test.ts'), 'owned source');
      await symlink(join(root, 'actual.test.ts'), join(pkg, 'probe.test.ts'));
      await expect(discoverTestPackages(root)).rejects.toThrow(
        'Symlinked test source is unsupported',
      );
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it('discovers the actual web and SDK test packages', async () => {
    const packages = await discoverTestPackages(frontend);
    expect(packages.map((pkg) => pkg.name)).toEqual(['apps/web', 'packages/sdk']);
    expect(packages.every((pkg) => pkg.files.length > 0)).toBe(true);
  });
});
