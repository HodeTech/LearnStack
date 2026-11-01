// Pure copied-source ingress instrumentation; no fixture starts on import.
import assert from 'node:assert/strict';

export function instrumentCopiedIngressRuntime(sourceText) {
  const launcher = sourceText;
  const handlerAnchor = '    const handle = app.getRequestHandler();';
  const sinkAnchor = '    upgradeSink = createUpgradeSink();';
  const prepareAnchor = '    await ownSetup(app.prepare());';
  const importAnchor = '    const [\n      { delegateDevelopmentHmr },';
  const nativeUpgradeAnchor = "    server.on('upgrade', (request, socket, head) => {";
  for (const anchor of [
    handlerAnchor,
    sinkAnchor,
    prepareAnchor,
    importAnchor,
    nativeUpgradeAnchor,
  ])
    assert.equal(launcher.split(anchor).length, 2, 'Ingress instrumentation anchor must be unique');
  const transformed = launcher
    .replace(
      importAnchor,
      `    if (dev) {
      const { boundNextVersionFetch } = await ownSetup(import('./ingress-readiness.mjs'));
      assertStarting();
      globalThis.fetch = boundNextVersionFetch(globalThis.fetch, (observation, sequence) => {
        control.proof.emit({ kind: 'version-fetch', pid: process.pid, sequence, ...observation });
      });
    }
${importAnchor}`,
    )
    .replace(
      prepareAnchor,
      `    control.proof.emit({ kind: 'readiness', pid: process.pid, phase: 'preparing' });
  await ownSetup(app.prepare());
  control.proof.emit({ kind: 'readiness', pid: process.pid, phase: 'prepared' });`,
    )
    .replace(
      handlerAnchor,
      `    const nextHandle = app.getRequestHandler();
    const handle = (...args) => { nextCalls++; return nextHandle(...args); };
    const emit = upgradeSink.emit.bind(upgradeSink);
    upgradeSink.emit = (event, ...args) => {
      if (event === 'upgrade') sinkCalls++;
      return emit(event, ...args);
    };
    control.proof.onCommand(async (message) => {
      const marker = message.fence + ':' + message.id + ':end\\n';
      await Promise.all([process.stdout, process.stderr].map((stream) =>
        new Promise((resolve) => stream.write(marker, resolve))));
      control.proof.emit({ id: message.id, nextCalls, sinkCalls, debugDisabled: process.env.DEBUG === '-*', environmentSafe: process.env.NODE_TLS_REJECT_UNAUTHORIZED === '1' && ['NODE_OPTIONS', 'NODE_EXTRA_CA_CERTS', 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'INGRESS_ENV_CANARY'].every((key) => process.env[key] === undefined) });
    });`,
    )
    .replace(
      sinkAnchor,
      `    let nextCalls = 0;
    let sinkCalls = 0;
    const sanitizedUpgrade = (request) =>
      !request.headers['x-matched-path'] &&
      !request.headers['x-middleware-subrequest'] &&
      request.rawHeaders.filter((value, index) => index % 2 === 0 &&
        value.toLowerCase() === 'host').length === 1;
    upgradeSink = createUpgradeSink();
    const registerSink = upgradeSink.on.bind(upgradeSink);
    upgradeSink.on = (event, listener) => registerSink(event, event !== 'upgrade' ? listener :
      (request, socket, head) => {
        control.proof.emit({ surface: 'sink', sanitized: sanitizedUpgrade(request) });
        return listener(request, socket, head);
      });`,
    )
    .replace(
      nativeUpgradeAnchor,
      `    server.on('upgrade', (request, socket, head) => {
      control.proof.emit({ surface: 'native', sanitized: sanitizedUpgrade(request) });`,
    );
  assert.notEqual(transformed, launcher);
  return transformed;
}
