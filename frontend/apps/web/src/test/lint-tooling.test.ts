// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { chmodSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { LegacyESLint } from 'eslint/use-at-your-own-risk';
import { describe, expect, it } from 'vitest';

import { lintSubjects, selectedSubjects } from '../../scripts/lint-web.mjs';

const app = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const repository = resolve(app, '../../..');

describe('shared CI and staged web lint subjects', () => {
  it('includes real root configs and native declarations, excluding generated output', () => {
    const subjects = lintSubjects(app);
    for (const file of [
      'tailwind.config.ts',
      'postcss.config.cjs',
      '.eslintrc.cjs',
      'scripts/lint-web.d.mts',
    ])
      expect(subjects).toContain(file);
    expect(subjects).not.toContain('next-env.d.ts');
    expect(subjects.some((file) => file.startsWith('.server/') || file.startsWith('.next/'))).toBe(
      false,
    );
    expect(selectedSubjects(app, [...subjects, 'next-env.d.ts', '.server/ingress.js'])).toEqual(
      subjects,
    );
  });

  it('finds both module TypeScript extensions and refuses unknown staged subjects', () => {
    const root = mkdtempSync(join(tmpdir(), 'learnstack-lint-census-'));
    try {
      for (const file of ['probe.mts', 'probe.cts', 'next-env.d.ts', '.server/generated.mjs']) {
        mkdirSync(dirname(join(root, file)), { recursive: true });
        writeFileSync(join(root, file), '');
      }
      expect(lintSubjects(root)).toEqual(['probe.cts', 'probe.mts']);
      expect(() => selectedSubjects(root, ['../outside.ts'])).toThrow(
        'Unsupported staged web lint subject',
      );
      expect(() => selectedSubjects(root, ['missing.ts'])).toThrow(
        'Unsupported staged web lint subject',
      );
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });

  it.each(['tailwind.config.ts', 'postcss.config.cjs', 'scripts/probe.mts', 'scripts/probe.cts'])(
    'applies a blocking real rule to %s with an independent clean control',
    async (file) => {
      const eslint = new LegacyESLint({
        cwd: app,
        resolvePluginsRelativeTo: resolve(repository, 'frontend/packages/config/eslint'),
        overrideConfig: { parserOptions: { project: null } },
      });
      const dirty = await eslint.lintText('fetch("/api/v1/public/site");\n', {
        filePath: join(app, file),
      });
      expect(dirty).toHaveLength(1);
      expect(dirty[0]!.messages).toEqual(
        expect.arrayContaining([
          expect.objectContaining({ ruleId: 'no-restricted-globals', severity: 2 }),
        ]),
      );
      const clean = await eslint.lintText('export const value = 1;\n', {
        filePath: join(app, file),
      });
      expect(clean).toHaveLength(1);
      expect(clean[0]!.messages).toEqual([]);
    },
  );

  it('gives native declarations an explicit non-project parser policy', async () => {
    const eslint = new LegacyESLint({
      cwd: app,
      resolvePluginsRelativeTo: resolve(repository, 'frontend/packages/config/eslint'),
    });
    const configuration = await eslint.calculateConfigForFile(
      join(app, 'scripts/fixture-support.d.mts'),
    );
    expect(configuration.parserOptions.project).toBeNull();
    const result = await eslint.lintText('export const value: string;\n', {
      filePath: join(app, 'scripts/fixture-support.d.mts'),
    });
    expect(result).toHaveLength(1);
    expect(result[0]!.messages).toEqual([]);
  });

  it.each([
    ['frontend/apps/web/probe.mts', 'lint:staged -- probe.mts'],
    ['frontend/apps/web/probe.cts', 'lint:staged -- probe.cts'],
    ['frontend/packages/sdk/src/probe.ts', '--filter @learnstack/sdk lint'],
    ['frontend/packages/ui/src/probe.tsx', '--filter @learnstack/ui lint'],
  ])('actual hook invokes blocking lint for staged %s', (file, invocation) => {
    const root = mkdtempSync(join(tmpdir(), 'learnstack-hook-lint-'));
    try {
      const bin = join(root, 'bin');
      mkdirSync(bin);
      const pnpm = join(bin, 'pnpm');
      writeFileSync(
        pnpm,
        '#!/bin/sh\nprintf "%s\\n" "$*" >> "$LEARNSTACK_LINT_CONTROL_LOG"\ncase "$*" in *lint*) exit 1 ;; esac\n',
      );
      chmodSync(pnpm, 0o700);
      writeFileSync(join(bin, 'leakwatch'), '#!/bin/sh\nexit 0\n');
      chmodSync(join(bin, 'leakwatch'), 0o700);
      mkdirSync(dirname(join(root, file)), { recursive: true });
      writeFileSync(join(root, file), 'export const probe = 1;\n');
      const gitEnvironment = { ...process.env };
      for (const key of Object.keys(gitEnvironment))
        if (key.startsWith('GIT_')) delete gitEnvironment[key];
      execFileSync('git', ['init', '-q'], { cwd: root, env: gitEnvironment });
      execFileSync('git', ['add', '.'], { cwd: root, env: gitEnvironment });
      expect(() =>
        execFileSync('bash', [join(repository, '.githooks/pre-commit')], {
          cwd: root,
          env: {
            ...gitEnvironment,
            PATH: bin + ':' + gitEnvironment.PATH,
            LEARNSTACK_LINT_CONTROL_LOG: join(root, 'calls'),
          },
          stdio: 'pipe',
        }),
      ).toThrow();
      expect(readFileSync(join(root, 'calls'), 'utf8')).toContain(invocation);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });
});
