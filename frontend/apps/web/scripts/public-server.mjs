import { readFileSync } from 'node:fs';
import { createServer as createUpgradeSink } from 'node:http';
import { createServer } from 'node:https';
import { fileURLToPath } from 'node:url';

import { delegateDevelopmentHmr } from './development-hmr.mjs';
import {
  admitIncomingRequest,
  loadLocalEnvironment,
  PUBLIC_ENV_KEYS,
  INGRESS_HEADER,
  publicServerConfiguration,
  refuseIngress,
  verifyProvenance,
} from '../.server/ingress.js';
import { installPublicAdmissionRuntime } from '../.server/public-admission-runtime.js';

const root = fileURLToPath(new URL('../../../../', import.meta.url));
try {
  // A nonempty exclusion survives debug initialization and prevents later dotenv
  // loading from restoring diagnostics. Apply it before importing any Next code.
  process.env.DEBUG = '-*';
  const { default: next } = await import('next');
  const values = loadLocalEnvironment(root, process.env);
  const configuration = publicServerConfiguration(values, root);
  const admission = installPublicAdmissionRuntime();
  // Next reads the same values; root/projection conflicts were refused before prepare.
  for (const key of PUBLIC_ENV_KEYS) process.env[key] = values[key];
  const dev = process.argv.includes('--dev');
  // Next attaches its automatic upgrade handler to this documented httpServer
  // option. The sink never listens: only the native TLS admission may emit to it.
  const upgradeSink = createUpgradeSink();
  const app = next({ dev, hostname: '127.0.0.1', port: 3000, httpServer: upgradeSink });
  await app.prepare();
  const handle = app.getRequestHandler();
  const server = createServer(
    {
      cert: readFileSync(configuration.certificate),
      key: readFileSync(configuration.privateKey),
      minVersion: 'TLSv1.2',
    },
    (request, response) => {
      if (!admitIncomingRequest(request, configuration.secret)) return refuseIngress(response);
      const binding = verifyProvenance(request.headers[INGRESS_HEADER], configuration.secret);
      if (!binding) return refuseIngress(response);
      // No request, credential or provider-error values enter launcher diagnostics.
      Promise.resolve()
        .then(() => admission.run(binding, request, response, () => handle(request, response)))
        .catch(() => {
          if (!response.headersSent) {
            response.writeHead(503, { 'cache-control': 'no-store' });
            response.end('Service unavailable');
          } else response.destroy();
        });
    },
  );
  server.on('upgrade', (request, socket, head) => {
    if (!dev || !admitIncomingRequest(request, configuration.secret)) return socket.destroy();
    delegateDevelopmentHmr(request, socket, head, upgradeSink);
  });
  server.on('connect', (_request, socket) => socket.destroy());
  server.on('error', () => {
    admission.shutdown();
    console.error('Public HTTPS listener failed');
    process.exit(1);
  });
  const shutdown = () => {
    admission.shutdown();
    server.close();
    server.closeAllConnections();
    void app.close().catch(() => console.error('Public server shutdown failed'));
  };
  process.once('SIGTERM', shutdown);
  process.once('SIGINT', shutdown);
  server.listen(3000, '127.0.0.1', () => {
    console.warn('Public HTTPS listener ready on 127.0.0.1:3000');
  });
} catch {
  console.error('Public server configuration or startup failed');
  process.exit(1);
}
