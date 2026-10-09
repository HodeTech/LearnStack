import { spawn } from 'node:child_process';
import { readdirSync } from 'node:fs';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const ignored = new Set(['node_modules', '.next', '.server', '.git', 'dist', 'coverage']);
const extension = /\.(?:[cm]?[jt]s|[jt]sx)$/;
export function lintSubjects(root) {
  const files = [];
  function visit(directory) {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      if (ignored.has(entry.name)) continue;
      const path = resolve(directory, entry.name);
      if (entry.isSymbolicLink()) throw new Error('Symlinked lint source is unsupported');
      if (entry.isDirectory()) visit(path);
      else if (
        entry.isFile() &&
        extension.test(entry.name) &&
        path !== resolve(root, 'next-env.d.ts')
      )
        files.push(relative(root, path));
    }
  }
  visit(root);
  return files.sort();
}
export function selectedSubjects(root, selected) {
  const census = lintSubjects(root);
  if (selected === undefined) return census;
  const known = new Set(census);
  return [
    ...new Set(
      selected
        .map((path) => {
          const name = relative(root, resolve(root, path));
          if (name === 'next-env.d.ts' || ignored.has(name.split('/')[0])) return undefined;
          if (!known.has(name)) throw new Error('Unsupported staged web lint subject: ' + name);
          return name;
        })
        .filter(Boolean),
    ),
  ].sort();
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
    const args = process.argv.slice(2);
    const fix = args[0] === '--fix';
    if (fix) args.shift();
    if (args[0] === '--') args.shift();
    const files = selectedSubjects(root, args.length ? args : undefined);
    if (files.length) {
      const child = spawn(
        'pnpm',
        [
          'exec',
          'next',
          'lint',
          '--max-warnings',
          '0',
          ...(fix ? ['--fix'] : []),
          ...files.flatMap((file) => ['--file', file]),
        ],
        { cwd: root, stdio: 'inherit' },
      );
      child.once('error', () => {
        process.exitCode = 1;
      });
      child.once('exit', (code) => {
        process.exitCode = code ?? 1;
      });
    }
  } catch (error) {
    console.error(error instanceof Error ? error.message : 'Web lint subject census failed');
    process.exitCode = 1;
  }
}
