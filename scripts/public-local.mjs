import { spawn } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { chmodSync, readFileSync, writeFileSync } from 'node:fs';
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
    let source = readFileSync(path, 'utf8');
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
    writeFileSync(path, source.replace(/\n*$/, '\n'), { mode: 0o600 });
    chmodSync(path, 0o600);
    console.warn('Private renderer configuration prepared; TLS and hosts remain manual.');
  } else if (operation === 'api') {
    const values = loadLocalEnvironment(root, process.env);
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
