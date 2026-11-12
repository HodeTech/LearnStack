import { readFileSync } from 'node:fs';
import { createServer as createUpgradeSink } from 'node:http';
import { createServer } from 'node:https';
import { fileURLToPath } from 'node:url';

/** The verified private child owns stop, failure and the single shutdown watchdog. */
export async function startPublicRuntime(control) {
  let admission;
  let bffRequests;
  let bffSessions;
  let backchannel;
  let bridge;
  let invitationHandler;
  let nativeInvitationTarget;
  let consentHandler;
  let nativeConsentTarget;
  let upgradeSink;
  let app;
  let server;
  let BffConfigurationError;
  let stopping = false;
  let shutdownPromise;
  let failed = false;
  const setupTasks = new Set();
  const shutdownTasks = [];
  const upgradeSockets = new Set();
  const startupStopped = Symbol('Public runtime startup stopped');

  const assertStarting = () => {
    if (stopping || control.stopRequested()) throw startupStopped;
  };
  const ownSetup = (task) => {
    const actual = Promise.resolve(task);
    setupTasks.add(actual);
    void actual.then(
      () => setupTasks.delete(actual),
      () => {
        failed = true;
        setupTasks.delete(actual);
      },
    );
    return actual;
  };
  const ownShutdown = (work) => {
    try {
      const actual = Promise.resolve(work());
      // Observe immediately, including while an independent setup task is held.
      void actual.catch(() => {
        failed = true;
      });
      shutdownTasks.push(actual);
    } catch {
      failed = true;
    }
  };
  let admissionStopped = false;
  let requestsStopStarted = false;
  let sessionsStopStarted = false;
  let bridgeStopStarted = false;
  let backchannelStopStarted = false;
  const requestNativeStops = () => {
    if (bridge && !bridgeStopStarted) {
      bridgeStopStarted = true;
      ownShutdown(() => bridge.stop());
    }
    if (backchannel && !backchannelStopStarted) {
      backchannelStopStarted = true;
      ownShutdown(() => backchannel.stop());
    }
    if (admission && !admissionStopped) {
      admissionStopped = true;
      ownShutdown(() => admission.shutdown());
    }
    if (bffRequests && !requestsStopStarted) {
      requestsStopStarted = true;
      ownShutdown(() => bffRequests.stop());
    }
    if (bffSessions && !sessionsStopStarted) {
      sessionsStopStarted = true;
      ownShutdown(() => bffSessions.stop());
    }
  };
  const shutdown = () => {
    if (shutdownPromise) return shutdownPromise;
    stopping = true;
    shutdownPromise = Promise.resolve().then(async () => {
      requestNativeStops();
      // Join each actual import/prepare/listen task, never the startup function
      // that may itself be waiting for this shutdown. A held prepare must settle
      // before its dependent app.close(), even when it eventually rejects.
      do {
        await Promise.allSettled([...setupTasks]);
      } while (setupTasks.size);
      // A synchronous factory may have returned its partial resource after stop.
      requestNativeStops();
      // Node excludes upgraded sockets from closeAllConnections. Destroy requests
      // cancellation; only their actual close event retires this native owner.
      for (const socket of upgradeSockets)
        ownShutdown(
          () =>
            new Promise((resolve) => {
              if (socket.closed) return resolve();
              // Peer cancellation is expected during stop; an error is no close
              // witness and does not retire this still-owned socket.
              const cancelling = () => {};
              socket.on('error', cancelling);
              socket.once('close', () => {
                socket.removeListener('error', cancelling);
                resolve();
              });
              try {
                socket.destroy();
              } catch {
                failed = true;
              }
            }),
        );
      upgradeSockets.clear();
      if (server?.listening) {
        ownShutdown(
          () =>
            new Promise((resolve, reject) =>
              server.close((error) => (error ? reject(error) : resolve())),
            ),
        );
        ownShutdown(() => server.closeAllConnections());
      }
      // The upgrade sink never listens, so there is no listener close to invent.
      if (app) ownShutdown(() => app.close());
      const results = await Promise.allSettled(shutdownTasks);
      if (failed || results.some((result) => result.status === 'rejected'))
        throw new Error('Public server shutdown failed');
    });
    return shutdownPromise;
  };
  // Register synchronously: a stop during import or prepare already owns this
  // handler and the controller's original remaining watchdog interval.
  control.registerShutdown(shutdown);
  try {
    assertStarting();
    const root = fileURLToPath(new URL('../../../../', import.meta.url));
    const framework = control.framework;
    if (!framework || !['development', 'production'].includes(framework.mode))
      throw new Error('Framework runtime admission missing');
    const dev = framework.mode === 'development';
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
    assertStarting();
    const [
      { delegateDevelopmentHmr },
      { createFrameworkDevelopmentAdmission, protectFrameworkDevelopmentResponse },
      configurationModule,
      { backchannelConfiguration },
      { createBackchannelAdmission, createNativeBackchannel, BACKCHANNEL_PATH },
      { createBackchannelBridge },
      { createLogoutTokenVerifier },
      { protectBffLogoutResponse },
      {
        admitIncomingRequest,
        loadLocalEnvironment,
        PUBLIC_ENV_KEYS,
        PUBLIC_HTTPS_PORT,
        INGRESS_HEADER,
        publicServerConfiguration,
        refuseIngress,
        verifyProvenance,
      },
      { installPublicAdmissionRuntime },
      { default: next },
    ] = await Promise.all([
      ownSetup(import('./development-hmr.mjs')),
      ownSetup(import('../../../scripts/framework-development.mjs')),
      ownSetup(import('../.server/auth/configuration.js')),
      ownSetup(import('../.server/auth/backchannel-configuration.js')),
      ownSetup(import('../.server/auth/backchannel-http.js')),
      ownSetup(import('../.server/auth/backchannel-bridge.js')),
      ownSetup(import('../.server/auth/backchannel-verifier.js')),
      ownSetup(import('../.server/auth/logout-response.js')),
      ownSetup(import('../.server/ingress.js')),
      ownSetup(import('../.server/public-admission-runtime.js')),
      ownSetup(import('next')),
    ]);
    assertStarting();
    BffConfigurationError = configurationModule.BffConfigurationError;
    const developmentAdmission = dev ? createFrameworkDevelopmentAdmission(framework) : undefined;
    assertStarting();
    const values = loadLocalEnvironment(root, process.env);
    assertStarting();
    const configuration = publicServerConfiguration(values, root);
    assertStarting();
    const bff = configurationModule.bffConfiguration(root, {
      ...process.env,
      NODE_ENV: dev ? 'development' : 'production',
    });
    assertStarting();
    const backchannelConfig = backchannelConfiguration(root, process.env, bff.enabled);
    const backchannelAdmission = createBackchannelAdmission();
    admission = installPublicAdmissionRuntime(bff.enabled);
    assertStarting();
    if (bff.enabled) {
      const [
        { createTenantProvider },
        { createAccountSiteCaller },
        { createNativeSessionApi },
        { createNativeAdministrationCaller, createNativeRetainedConsentCaller },
        { installBffSessionRuntime },
        { installBffRequestRuntime },
        { createNativeInvitationApi },
        { createNativeInvitationHandler, nativeInvitationTarget: invitationTarget },
        { createInvitationRenderer },
        { default: invitationEn },
        { default: invitationTr },
        { createNativeConsentHandler, nativeConsentTarget: consentTarget },
        { default: authEn },
        { default: authTr },
      ] = await Promise.all([
        ownSetup(import('../.server/auth/provider-core.js')),
        ownSetup(import('../.server/auth/account-client.js')),
        ownSetup(import('../.server/auth/native-session-client.js')),
        ownSetup(import('../.server/auth/native-administration-client.js')),
        ownSetup(import('../.server/auth/session-runtime.js')),
        ownSetup(import('../.server/auth/request-runtime.js')),
        ownSetup(import('../.server/auth/native-invitation-client.js')),
        ownSetup(import('../.server/auth/native-invitation-http.js')),
        ownSetup(import('../.server/auth/invitation-html.js')),
        ownSetup(import('../src/i18n/messages/en/invitation.json', { with: { type: 'json' } })),
        ownSetup(import('../src/i18n/messages/tr/invitation.json', { with: { type: 'json' } })),
        ownSetup(import('../.server/auth/native-consent-http.js')),
        ownSetup(import('../src/i18n/messages/en/auth.json', { with: { type: 'json' } })),
        ownSetup(import('../src/i18n/messages/tr/auth.json', { with: { type: 'json' } })),
      ]);
      assertStarting();
      const nativeConfiguration = {
        ...configuration,
        nativeSourceSecret: bff.nativeSourceSecret,
        authorityId: bff.authorityId,
        issuer: bff.issuer,
      };
      const provider = createTenantProvider(bff, globalThis.fetch);
      assertStarting();
      const account = createAccountSiteCaller(nativeConfiguration, globalThis.fetch);
      assertStarting();
      const native = createNativeSessionApi(nativeConfiguration, globalThis.fetch);
      assertStarting();
      const administration = createNativeAdministrationCaller(
        nativeConfiguration,
        globalThis.fetch,
      );
      assertStarting();
      const dependencies = {
        provider,
        account,
        native,
        administration,
        retainedConsent: createNativeRetainedConsentCaller(nativeConfiguration, globalThis.fetch),
        invitations: createNativeInvitationApi(nativeConfiguration, globalThis.fetch),
        diagnostic: (reason) => console.warn('BFF session lifecycle', { reason }),
      };
      assertStarting();
      bffRequests = installBffRequestRuntime(admission.holder);
      assertStarting();
      bffSessions = installBffSessionRuntime(dependencies, bffRequests.preparation);
      if (backchannelConfig.enabled) {
        const verifier = createLogoutTokenVerifier(bff, globalThis.fetch);
        backchannel = createNativeBackchannel({
          authority: backchannelConfig.authority,
          admission: backchannelAdmission,
          verify: verifier.verify,
          apply: bffSessions.applyProviderLogout,
          diagnostic: (category, stage) =>
            console.warn('BFF backchannel', {
              category,
              ...(stage ? { stage } : {}),
              ...backchannelAdmission.counts(),
              ...bffSessions.backchannelCounts(),
            }),
        });
      }
      nativeInvitationTarget = invitationTarget;
      nativeConsentTarget = consentTarget;
      consentHandler = createNativeConsentHandler({
        catalogues: { en: authEn.consents, tr: authTr.consents },
        store: bffSessions.store,
        responseLease: bffRequests.nativeConsentResponse,
      });
      invitationHandler = createNativeInvitationHandler({
        configuration,
        admission: admission.holder,
        store: bffSessions.store,
        responseLease: bffRequests.nativeResponse,
        render: createInvitationRenderer({ en: invitationEn, tr: invitationTr }),
        providerOrigin: new URL(bff.issuer).origin,
        transport: globalThis.fetch,
      });
      assertStarting();
    }
    // Next reads the same values; root/projection conflicts were refused before prepare.
    for (const key of PUBLIC_ENV_KEYS) process.env[key] = values[key];
    assertStarting();
    // Next attaches its automatic upgrade handler to this documented httpServer
    // option. The sink never listens: only the native TLS admission may emit to it.
    upgradeSink = createUpgradeSink();
    assertStarting();
    app = next({
      dev,
      hostname: '127.0.0.1',
      port: PUBLIC_HTTPS_PORT,
      httpServer: upgradeSink,
    });
    assertStarting();
    await ownSetup(app.prepare());
    assertStarting();
    const handle = app.getRequestHandler();
    assertStarting();
    server = createServer(
      {
        cert: readFileSync(configuration.certificate),
        key: readFileSync(configuration.privateKey),
        minVersion: 'TLSv1.2',
      },
      (request, response) => {
        if (stopping || control.stopRequested()) return refuseIngress(response);
        if (request.url?.split('?')[0] === BACKCHANNEL_PATH) {
          if (backchannel) return backchannel.handle(request, response);
          response.writeHead(404, { 'cache-control': 'no-store', 'content-length': '0' });
          response.end();
          request.resume();
          return;
        }
        if (!admitIncomingRequest(request, configuration.secret, bff.enabled))
          return refuseIngress(response);
        if (developmentAdmission)
          protectFrameworkDevelopmentResponse(response, developmentAdmission);
        const binding = verifyProvenance(request.headers[INGRESS_HEADER], configuration.secret);
        if (!binding) return refuseIngress(response);
        if (bff.enabled && binding.method === 'POST' && binding.target === '/api/auth/signout')
          protectBffLogoutResponse(response, bff.issuer, binding.host);
        // No request, credential or provider-error values enter launcher diagnostics.
        Promise.resolve()
          .then(() =>
            admission.run(binding, request, response, () =>
              bffRequests
                ? bffRequests.run(binding, request, response, () =>
                    nativeConsentTarget(binding.target)
                      ? consentHandler(binding, request, response)
                      : nativeInvitationTarget(binding.target)
                        ? invitationHandler(binding, request, response)
                        : handle(request, response),
                  )
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
    assertStarting();
    server.on('upgrade', (request, socket, head) => {
      if (
        stopping ||
        control.stopRequested() ||
        !dev ||
        !admitIncomingRequest(request, configuration.secret)
      )
        return socket.destroy();
      try {
        developmentAdmission.assert();
      } catch {
        socket.destroy();
        return;
      }
      upgradeSockets.add(socket);
      socket.once('close', () => upgradeSockets.delete(socket));
      delegateDevelopmentHmr(request, socket, head, upgradeSink);
    });
    server.on('connect', (_request, socket) => socket.destroy());
    assertStarting();
    const listenerFault = () => {
      failed = true;
      console.error('Public HTTPS listener failed');
      control.requestStop('listener-fault');
    };
    server.on('error', listenerFault);
    assertStarting();
    await ownSetup(
      new Promise((resolve, reject) => {
        const failedListen = () => reject(new Error('Public HTTPS listener failed'));
        server.once('error', failedListen);
        try {
          server.listen(PUBLIC_HTTPS_PORT, '127.0.0.1', () => {
            server.removeListener('error', failedListen);
            try {
              assertStarting();
              const address = server.address();
              if (
                !address ||
                typeof address === 'string' ||
                address.address !== '127.0.0.1' ||
                address.family !== 'IPv4' ||
                address.port !== PUBLIC_HTTPS_PORT
              )
                throw new Error('Public HTTPS listener failed');
              resolve();
            } catch (error) {
              if (error === startupStopped) resolve();
              else {
                listenerFault();
                reject(new Error('Public HTTPS listener failed'));
              }
            }
          });
        } catch {
          server.removeListener('error', failedListen);
          listenerFault();
          reject(new Error('Public HTTPS listener failed'));
        }
      }),
    );
    assertStarting();
    if (backchannelConfig.enabled) {
      const address = server.address();
      if (!address || typeof address === 'string') throw new Error('Public HTTPS listener failed');
      bridge = createBackchannelBridge(backchannelConfig, backchannelAdmission, address.port);
      assertStarting();
      bridge.server.on('error', listenerFault);
      await ownSetup(bridge.start());
      assertStarting();
    }
    const ready = control.listenerReady(server);
    assertStarting();
    if (!ready) throw new Error('Public HTTPS listener failed');
    console.warn(`Public HTTPS listener ready on 127.0.0.1:${PUBLIC_HTTPS_PORT}`);
  } catch (error) {
    if (error === startupStopped) return;
    failed = true;
    if (BffConfigurationError && error instanceof BffConfigurationError)
      console.error(`Public server BFF configuration refused: ${error.issue}`);
    else console.error('Public server configuration or startup failed');
    control.requestStop('startup-fault');
  }
}
