import { readFileSync } from 'node:fs';
import { createServer } from 'node:https';
import { fileURLToPath } from 'node:url';

import next from 'next';

import {
  admitIncomingRequest,
  loadLocalEnvironment,
  PUBLIC_ENV_KEYS,
  publicServerConfiguration,
  refuseIngress,
} from '../.server/ingress.js';

const root = fileURLToPath(new URL('../../../../', import.meta.url));
try {
  const values = loadLocalEnvironment(root, process.env);
  const configuration = publicServerConfiguration(values, root);
  // Next reads the same values; root/projection conflicts were refused before prepare.
  for (const key of PUBLIC_ENV_KEYS) process.env[key] = values[key];
  const dev = process.argv.includes('--dev');
  const app = next({ dev, hostname: '127.0.0.1', port: 3000 });
  await app.prepare();
  const handle = app.getRequestHandler();
  const upgrade = app.getUpgradeHandler();
  const server = createServer(
    {
      cert: readFileSync(configuration.certificate),
      key: readFileSync(configuration.privateKey),
      minVersion: 'TLSv1.2',
    },
    (request, response) => {
      if (!admitIncomingRequest(request, configuration.secret)) return refuseIngress(response);
      // No request, credential or provider-error values enter launcher diagnostics.
      Promise.resolve(handle(request, response)).catch(() => {
        if (!response.headersSent) {
          response.writeHead(503, { 'cache-control': 'no-store' });
          response.end('Service unavailable');
        } else response.destroy();
      });
    },
  );
  server.on('upgrade', (request, socket, head) => {
    if (!admitIncomingRequest(request, configuration.secret)) return socket.destroy();
    Promise.resolve(upgrade(request, socket, head)).catch(() => socket.destroy());
  });
  server.on('error', () => {
    console.error('Public HTTPS listener failed');
    process.exit(1);
  });
  server.listen(3000, '127.0.0.1', () => {
    console.warn('Public HTTPS listener ready on 127.0.0.1:3000');
  });
} catch {
  console.error('Public server configuration or startup failed');
  process.exit(1);
}
