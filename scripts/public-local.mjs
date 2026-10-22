import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import {
  closeSync,
  constants,
  fstatSync,
  ftruncateSync,
  openSync,
  readSync,
  writeSync,
} from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { publicApiEnvironment } from './public-runtime.mjs';

import {
  loadLocalEnvironment,
  parseLocalEnvironment,
  publicServerConfiguration,
  validSecret,
} from '../frontend/apps/web/.server/ingress.js';

const root = fileURLToPath(new URL('../', import.meta.url));
try {
  const operation = process.argv[2];
  if (operation === 'prepare') {
    const path = resolve(root, '.env');
    const maximumBytes = 256 * 1024;
    const descriptor = openSync(
      path,
      constants.O_RDWR | constants.O_NOFOLLOW | constants.O_NONBLOCK,
    );
    try {
      const state = fstatSync(descriptor);
      if (
        !state.isFile() ||
        state.uid !== process.getuid() ||
        state.nlink !== 1 ||
        (state.mode & 0o077) !== 0 ||
        state.size > maximumBytes
      )
        throw new Error();
      // Qualify the held file before its first read; growth cannot bypass the cap.
      const bytes = Buffer.alloc(maximumBytes + 1);
      let size = 0;
      while (size < bytes.length) {
        const count = readSync(descriptor, bytes, size, bytes.length - size, null);
        if (count === 0) break;
        size += count;
      }
      if (size > maximumBytes) throw new Error();
      let source = bytes.subarray(0, size).toString('utf8');
      if (Buffer.byteLength(source) > maximumBytes) throw new Error();
      const values = parseLocalEnvironment(source);
      const defaults = {
        LEARNSTACK_PUBLIC_API_ORIGIN: 'http://127.0.0.1:5080',
        LEARNSTACK_PUBLIC_TLS_CERT: '.data/tls/public.pem',
        LEARNSTACK_PUBLIC_TLS_KEY: '.data/tls/public-key.pem',
      };
      for (const [key, value] of Object.entries(defaults)) {
        if (values[key] === undefined) source += '\n' + key + '=' + value;
      }
      const secret = values.LEARNSTACK_PUBLIC_HOP_SECRET;
      if (!secret) {
        const generated = randomBytes(32).toString('base64url');
        if (secret === undefined) source += '\nLEARNSTACK_PUBLIC_HOP_SECRET=' + generated;
        else
          source = source.replace(
            /^LEARNSTACK_PUBLIC_HOP_SECRET=.*$/m,
            'LEARNSTACK_PUBLIC_HOP_SECRET=' + generated,
          );
      } else if (!validSecret(secret)) throw new Error();
      publicServerConfiguration(parseLocalEnvironment(source), root);
      const prepared = Buffer.from(source.replace(/\n*$/, '\n'));
      if (prepared.length > maximumBytes) throw new Error();
      // Reads advance the descriptor; explicit write positions replace from byte zero.
      let written = 0;
      while (written < prepared.length) {
        const count = writeSync(descriptor, prepared, written, prepared.length - written, written);
        if (count === 0) throw new Error();
        written += count;
      }
      ftruncateSync(descriptor, prepared.length);
    } finally {
      closeSync(descriptor);
    }
    console.warn('Private renderer configuration prepared; TLS and hosts remain manual.');
  } else if (operation === 'api') {
    const values = loadLocalEnvironment(root, process.env, { apiAuthentication: true });
    const configuration = publicServerConfiguration(values, root);
    if (!values.ConnectionStrings__Default) throw new Error();
    const environment = publicApiEnvironment(values, configuration);
    const child = spawn(
      'dotnet',
      ['run', '--no-launch-profile', '--project', 'src/LearnStack.Api'],
      { cwd: resolve(root, 'backend'), env: environment, stdio: 'inherit' },
    );
    child.on('error', () => {
      console.error('Local API launcher failed');
      process.exit(1);
    });
    child.on('exit', (code) => process.exit(code ?? 1));
    for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
  } else throw new Error();
} catch {
  console.error('Local public configuration or launch failed');
  process.exit(1);
}
