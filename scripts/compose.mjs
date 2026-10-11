import { spawn } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const repositoryRoot = fileURLToPath(new URL('../', import.meta.url));

/** One explicit selection, shared by start, readiness, seeding and teardown. */
export function composeSelection(mode, environment = process.env, root = repositoryRoot) {
  if (!['dev', 'e2e'].includes(mode)) throw new Error('Unsupported Compose mode');
  const project = environment.LEARNSTACK_COMPOSE_PROJECT ?? `learnstack-${mode}`;
  if (!/^[a-z0-9][a-z0-9_-]{0,62}$/.test(project)) throw new Error('Invalid Compose project');
  const file = resolve(root, environment.LEARNSTACK_COMPOSE_ENV_FILE ?? '.env');
  if (!existsSync(file))
    throw new Error('Compose environment file is missing; run make public-env');
  const source = readFileSync(file, 'utf8');
  // Docker also reads its own controls from --env-file, after this process's
  // environment has been sanitized. Reject every supported assignment spelling.
  if (/^\s*(?:export\s+)?COMPOSE_[A-Za-z0-9_]*\s*(?:[=:]|$)/im.test(source))
    throw new Error('Reserved COMPOSE_* controls cannot appear in the selected environment');
  const env = { ...environment };
  // Explicit selection cannot be widened by an inherited profile/file/project.
  for (const key of Object.keys(env)) if (/^COMPOSE_/i.test(key)) delete env[key];
  // Compose's process environment otherwise silently overrides --env-file.
  for (const match of source.matchAll(/^([A-Za-z_][A-Za-z0-9_]*)=/gm)) delete env[match[1]];
  const files = [resolve(root, 'infra/compose/dev.yml')];
  if (mode === 'e2e') files.push(resolve(root, 'infra/compose/e2e.yml'));
  return Object.freeze({
    mode,
    project,
    file,
    cwd: root,
    env: Object.freeze(env),
    args: Object.freeze([
      'compose',
      '--project-name',
      project,
      '--env-file',
      file,
      ...files.flatMap((value) => ['-f', value]),
    ]),
  });
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const selected = composeSelection(
      process.argv[2] === 'selected'
        ? (process.env.LEARNSTACK_COMPOSE_MODE ?? 'dev')
        : process.argv[2],
    );
    const child = spawn('docker', [...selected.args, ...process.argv.slice(3)], {
      cwd: selected.cwd,
      env: selected.env,
      stdio: 'inherit',
    });
    child.on('error', () => {
      console.error('Compose command failed');
      process.exitCode = 1;
    });
    child.on('exit', (code) => {
      process.exitCode = code ?? 1;
    });
    for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
  } catch {
    console.error('Invalid Compose selection or missing private environment');
    process.exitCode = 1;
  }
}
