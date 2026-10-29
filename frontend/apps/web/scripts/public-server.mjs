import { readFileSync } from 'node:fs';
import { createServer as createUpgradeSink } from 'node:http';
import { createServer } from 'node:https';
import { fileURLToPath } from 'node:url';

import { delegateDevelopmentHmr } from './development-hmr.mjs';
import { BffConfigurationError, bffConfiguration } from '../.server/auth/configuration.js';
import { protectBffLogoutResponse } from '../.server/auth/logout-response.js';
import {
  admitIncomingRequest,
  loadLocalEnvironment,
  PUBLIC_ENV_KEYS,
  PUBLIC_HTTPS_PORT,
  INGRESS_HEADER,
  publicServerConfiguration,
  refuseIngress,
  verifyProvenance,
} from '../.server/ingress.js';
import { installPublicAdmissionRuntime } from '../.server/public-admission-runtime.js';

const root = fileURLToPath(new URL('../../../../', import.meta.url));
try {
  // The native provider/API transports never inherit an ambient TLS bypass.
  process.env.NODE_TLS_REJECT_UNAUTHORIZED = '1';
  // A nonempty exclusion survives debug initialization and prevents later dotenv
  // loading from restoring diagnostics. Apply it before importing any Next code.
  process.env.DEBUG = '-*';
  // Pinned Next records raw development request URLs in .next/trace even for
  // middleware responses. Every accepted span duration is <= MAX_SAFE_INTEGER
  // microseconds; this millisecond threshold suppresses that reporter before
  // Next loads. The separately invoked build keeps its non-request traces.
  process.env.NEXT_TRACE_SPAN_THRESHOLD_MS = '9007199254740991';
  const { default: next } = await import('next');
  const values = loadLocalEnvironment(root, process.env);
  const configuration = publicServerConfiguration(values, root);
  const dev = process.argv.includes('--dev');
  const bff = bffConfiguration(root, {
    ...process.env,
    NODE_ENV: dev ? 'development' : 'production',
  });
  const admission = installPublicAdmissionRuntime(bff.enabled);
  let bffRequests;
  let bffSessions;
  if (bff.enabled) {
    const [
      { createTenantProvider },
      { createAccountSiteCaller },
      { createNativeSessionApi },
      { createNativeAdministrationCaller },
      { installBffSessionRuntime },
      { installBffRequestRuntime },
    ] = await Promise.all([
      import('../.server/auth/provider-core.js'),
      import('../.server/auth/account-client.js'),
      import('../.server/auth/native-session-client.js'),
      import('../.server/auth/native-administration-client.js'),
      import('../.server/auth/session-runtime.js'),
      import('../.server/auth/request-runtime.js'),
    ]);
    const nativeConfiguration = {
      ...configuration,
      nativeSourceSecret: bff.nativeSourceSecret,
      authorityId: bff.authorityId,
      issuer: bff.issuer,
    };
    const dependencies = {
      provider: createTenantProvider(bff, globalThis.fetch),
      account: createAccountSiteCaller(nativeConfiguration, globalThis.fetch),
      native: createNativeSessionApi(nativeConfiguration, globalThis.fetch),
      administration: createNativeAdministrationCaller(nativeConfiguration, globalThis.fetch),
      diagnostic: (reason) => console.warn('BFF session lifecycle', { reason }),
    };
    bffRequests = installBffRequestRuntime(admission.holder);
    bffSessions = installBffSessionRuntime(dependencies, bffRequests.preparation);
  }
  // Next reads the same values; root/projection conflicts were refused before prepare.
  for (const key of PUBLIC_ENV_KEYS) process.env[key] = values[key];
  // Next attaches its automatic upgrade handler to this documented httpServer
  // option. The sink never listens: only the native TLS admission may emit to it.
  const upgradeSink = createUpgradeSink();
  const app = next({
    dev,
    hostname: '127.0.0.1',
    port: PUBLIC_HTTPS_PORT,
    httpServer: upgradeSink,
  });
  await app.prepare();
  const handle = app.getRequestHandler();
  const upgradeSockets = new Set();
  const server = createServer(
    {
      cert: readFileSync(configuration.certificate),
      key: readFileSync(configuration.privateKey),
      minVersion: 'TLSv1.2',
    },
    (request, response) => {
      if (!admitIncomingRequest(request, configuration.secret, bff.enabled))
        return refuseIngress(response);
      const binding = verifyProvenance(request.headers[INGRESS_HEADER], configuration.secret);
      if (!binding) return refuseIngress(response);
      if (bff.enabled && binding.method === 'POST' && binding.target === '/api/auth/signout')
        protectBffLogoutResponse(response, bff.issuer, binding.host);
      // No request, credential or provider-error values enter launcher diagnostics.
      Promise.resolve()
        .then(() =>
          admission.run(binding, request, response, () =>
            bffRequests
              ? bffRequests.run(binding, request, response, () => handle(request, response))
              : handle(request, response),
          ),
        )
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
    upgradeSockets.add(socket);
    socket.once('close', () => upgradeSockets.delete(socket));
    delegateDevelopmentHmr(request, socket, head, upgradeSink);
  });
  server.on('connect', (_request, socket) => socket.destroy());
  let stopping = false;
  let listenerFailed = false;
  const shutdown = async () => {
    if (stopping) return;
    stopping = true;
    admission.shutdown();
    const requestsStopped = bffRequests?.stop();
    const sessionsStopped = bffSessions?.stop();
    // Node excludes upgraded sockets from closeAllConnections; Next's custom
    // server also leaves development handles alive after app.close().
    for (const socket of upgradeSockets) socket.destroy();
    upgradeSockets.clear();
    const deadline = setTimeout(() => {
      console.error('Public server shutdown failed');
      process.exit(1);
    }, 5000);
    deadline.unref();
    const closed = new Promise((resolve, reject) =>
      server.close((error) => (error ? reject(error) : resolve())),
    );
    server.closeAllConnections();
    upgradeSink.close();
    const results = await Promise.allSettled([
      closed,
      app.close(),
      sessionsStopped,
      requestsStopped,
    ]);
    clearTimeout(deadline);
    const failed = listenerFailed || results.some((result) => result.status === 'rejected');
    if (failed) console.error('Public server shutdown failed');
    process.exit(failed ? 1 : 0);
  };
  server.on('error', () => {
    listenerFailed = true;
    console.error('Public HTTPS listener failed');
    void shutdown();
  });
  process.once('SIGTERM', shutdown);
  process.once('SIGINT', shutdown);
  server.listen(PUBLIC_HTTPS_PORT, '127.0.0.1', () => {
    console.warn(`Public HTTPS listener ready on 127.0.0.1:${PUBLIC_HTTPS_PORT}`);
  });
} catch (error) {
  if (error instanceof BffConfigurationError)
    console.error(`Public server BFF configuration refused: ${error.issue}`);
  else console.error('Public server configuration or startup failed');
  process.exit(1);
}
