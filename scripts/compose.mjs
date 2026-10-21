import { spawn } from 'node:child_process';
import { closeSync, constants, existsSync, fstatSync, openSync, readSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { RealmError, realmFailureNotice, refuse } from './development-realm-errors.mjs';

export const repositoryRoot = fileURLToPath(new URL('../', import.meta.url));

const selectionMessages = Object.freeze({
  mode: 'Unsupported Compose mode',
  project: 'Invalid Compose project',
  missing: 'Compose environment file is missing; run make public-env',
  reserved: 'Reserved COMPOSE_* controls cannot appear in the selected environment',
});
class ComposeSelectionError extends Error {
  constructor(code) {
    super(selectionMessages[code]);
    this.code = code;
  }
}

/** Only locally classified selection failures may expose a specific notice. */
export function composeSelectionFailure(error) {
  return error instanceof ComposeSelectionError ? selectionMessages[error.code] : undefined;
}

/** Unknown argv stays strict; these operations cannot install a client secret. */
function lifecycleOnly(args) {
  if (!Array.isArray(args) || args.some(value => typeof value !== 'string')) return false;
  let index = 0;
  while (args[index] === '--profile') {
    if (!/^(?:[a-zA-Z0-9_][a-zA-Z0-9_.-]*|\*)$/.test(args[index + 1] ?? '')) return false;
    index += 2;
  }
  const command = args[index++];
  if (command === 'config') return args.length === index + 1 && args[index] === '--services';
  const flagsByCommand = {
    down: ['-v', '--volumes', '--remove-orphans'],
    logs: ['-f', '--follow', '-t', '--timestamps', '--no-color', '--no-log-prefix'],
    ps: ['-a', '--all', '-q', '--quiet', '--services', '--no-trunc'],
    stop: [],
  };
  if (!Object.hasOwn(flagsByCommand, command)) return false;
  const flags = flagsByCommand[command];
  const values = {
    down: { '-t': /^\d+$/, '--timeout': /^\d+$/ },
    logs: { '--tail': /^(?:all|\d+)$/, '--since': /^[^-\r\n\0][^\r\n\0]*$/, '--until': /^[^-\r\n\0][^\r\n\0]*$/ },
    ps: { '--format': /^(?:json|table)$/, '--status': /^(?:paused|restarting|removing|running|dead|created|exited)$/ },
    stop: { '-t': /^\d+$/, '--timeout': /^\d+$/ },
  }[command];
  for (; index < args.length; index++) {
    const argument = args[index];
    if (flags.includes(argument)) continue;
    if (/^[a-zA-Z0-9][a-zA-Z0-9_.-]*$/.test(argument)) continue;
    const separator = argument.indexOf('=');
    const option = separator === -1 ? argument : argument.slice(0, separator);
    if (!Object.hasOwn(values, option)) return false;
    const value = separator === -1 ? args[++index] : argument.slice(separator + 1);
    if (typeof value !== 'string' || !values[option].test(value)) return false;
  }
  return true;
}

/** Qualify the selected private source before its first content read. */
export function readComposeEnvironmentFile(path) {
  const maximumBytes = 256 * 1024;
  let descriptor;
  try {
    descriptor = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK);
    const state = fstatSync(descriptor);
    if (!state.isFile() || state.uid !== process.getuid() || state.nlink !== 1
      || (state.mode & 0o077) !== 0)
      refuse('preflight', 'private_file');
    if (state.size > maximumBytes) refuse('preflight', 'configuration');
    // Read at most one byte beyond the cap, even if an owned file grows after
    // fstat. O_NONBLOCK and the type check prevent a FIFO from stalling selection.
    const bytes = Buffer.alloc(maximumBytes + 1);
    let size = 0;
    while (size < bytes.length) {
      const count = readSync(descriptor, bytes, size, bytes.length - size, null);
      if (count === 0) break;
      size += count;
    }
    if (size > maximumBytes) refuse('preflight', 'configuration');
    const source = bytes.subarray(0, size).toString('utf8');
    if (Buffer.byteLength(source) > maximumBytes) refuse('preflight', 'configuration');
    return source;
  } catch (error) {
    if (error instanceof RealmError) throw error;
    refuse('preflight', error?.code === 'ELOOP' ? 'private_file' : 'configuration');
  }
  finally {
    if (descriptor !== undefined) {
      try { closeSync(descriptor); }
      catch { refuse('cleanup', 'cleanup'); }
    }
  }
}

/** Docker prerequisites only; the selected file owns service configuration. */
export function composeProcessEnvironment(environment = process.env) {
  const prerequisites = ['PATH', 'HOME', 'TMPDIR', 'APPDATA', 'USERPROFILE',
    'XDG_CONFIG_HOME', 'XDG_RUNTIME_DIR', 'DOCKER_CONFIG', 'DOCKER_CONTEXT',
    'DOCKER_HOST', 'DOCKER_TLS_VERIFY', 'DOCKER_CERT_PATH',
    'DOCKER_DEFAULT_PLATFORM', 'DOCKER_API_VERSION'];
  return Object.fromEntries(prerequisites.filter(key => environment[key] !== undefined)
    .map(key => [key, environment[key]]));
}

/** One explicit selection, shared by start, readiness, seeding and teardown. */
export function composeSelection(mode, environment = process.env, root = repositoryRoot, commandArgs = []) {
  if (!['dev', 'e2e'].includes(mode)) throw new ComposeSelectionError('mode');
  const project = environment.LEARNSTACK_COMPOSE_PROJECT ?? `learnstack-${mode}`;
  if (!/^[a-z0-9][a-z0-9_-]{0,62}$/.test(project)) throw new ComposeSelectionError('project');
  const file = resolve(root, environment.LEARNSTACK_COMPOSE_ENV_FILE ?? '.env');
  if (!existsSync(file))
    throw new ComposeSelectionError('missing');
  const source = readComposeEnvironmentFile(file);
  // Docker also reads its own controls from --env-file, after this process's
  // environment has been sanitized. Reject every supported assignment spelling.
  if (/^\s*(?:export\s+)?COMPOSE_[A-Za-z0-9_]*\s*(?:[=:]|$)/im.test(source))
    throw new ComposeSelectionError('reserved');
  // Compose reads selected configuration from the private file. It needs Docker
  // process prerequisites, not the caller's Keycloak, application or BFF secrets.
  const env = composeProcessEnvironment(environment);
  // Compose's process environment otherwise silently overrides --env-file.
  for (const match of source.matchAll(/^([A-Za-z_][A-Za-z0-9_]*)=/gm)) delete env[match[1]];
  // This process-only value never reaches a container-creating command or the
  // private source. Full config output also remains strict.
  if (lifecycleOnly(commandArgs)) env.KEYCLOAK_WEB_CLIENT_SECRET = 'learnstack-lifecycle-placeholder';
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
    const commandArgs = process.argv.slice(3);
    const selected = composeSelection(
      process.argv[2] === 'selected'
        ? (process.env.LEARNSTACK_COMPOSE_MODE ?? 'dev')
        : process.argv[2],
      process.env,
      repositoryRoot,
      commandArgs,
    );
    const child = spawn('docker', [...selected.args, ...commandArgs], {
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
  } catch (error) {
    console.error(error instanceof RealmError ? realmFailureNotice(error)
      : composeSelectionFailure(error) ?? 'Invalid Compose selection or missing private environment');
    process.exitCode = 1;
  }
}
