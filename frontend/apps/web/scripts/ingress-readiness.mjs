const READINESS_MILLISECONDS = 20000;
const phases = ['launched', 'preparing', 'prepared'];
const keywords = {
  compilation: ['Failed to compile'],
  missingModule: ['Module not found', 'Cannot find module'],
  typescript: ['TypeScript'],
  postcss: ['postcss'],
  symlink: ['symlink'],
  middleware: ['middleware'],
};

export function isReadinessNotification(message) {
  return ['bound', 'readiness', 'version-fetch'].includes(message?.kind);
}

function probeKind(error) {
  // Never project an error message, hostname, URL, stack or arbitrary code.
  let code;
  try {
    code = error?.code;
  } catch {
    return 'other';
  }
  switch (code) {
    case 'ECONNREFUSED':
      return 'connection_refused';
    case 'INGRESS_PROBE_TIMEOUT':
    case 'ETIMEDOUT':
      return 'timeout';
    case 'ECONNRESET':
      return 'connection_reset';
    case 'DEPTH_ZERO_SELF_SIGNED_CERT':
    case 'SELF_SIGNED_CERT_IN_CHAIN':
    case 'ERR_TLS_CERT_ALTNAME_INVALID':
    case 'UNABLE_TO_VERIFY_LEAF_SIGNATURE':
    case 'CERT_HAS_EXPIRED':
      return 'tls';
    default:
      return 'other';
  }
}

/** Bound only Next 15.5.27's one-argument informational lookup in this fixture. */
export function boundNextVersionFetch(delegate, notify) {
  return function (...args) {
    if (args.length !== 1 || args[0] !== 'https://registry.npmjs.org/-/package/next/dist-tags')
      return Reflect.apply(delegate, this, args);
    const started = performance.now();
    const controller = new AbortController();
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      controller.abort(new DOMException('Fixture version lookup timeout', 'TimeoutError'));
    }, 2000);
    timer.unref?.();
    const emit = (state, errorKind = 'none') => {
      try {
        notify({
          state,
          errorKind,
          elapsedMilliseconds: Math.min(
            120000,
            Math.max(0, Math.round(performance.now() - started)),
          ),
        });
      } catch {
        /* Observation cannot change the original fetch outcome. */
      }
    };
    emit('pending');
    let result;
    try {
      result = Reflect.apply(delegate, this, [args[0], { signal: controller.signal }]);
    } catch (error) {
      clearTimeout(timer);
      emit('failed', probeKind(error));
      throw error;
    }
    return Promise.resolve(result).then(
      (response) => {
        clearTimeout(timer);
        emit('settled');
        return response;
      },
      (error) => {
        clearTimeout(timer);
        emit('failed', timedOut ? 'timeout' : probeKind(error));
        throw error;
      },
    );
  };
}

/** One owned child's observations; absence of a bind message means unobserved. */
export function createIngressReadiness(child) {
  const launched = performance.now();
  const bindings = new Map();
  const flags = Object.fromEntries(Object.keys(keywords).map((key) => [key, false]));
  let phase = 'launched';
  let phaseStarted = launched;
  let spawnFailed = false;
  let outputOverlap = '';
  let versionFetch = 'unobserved';
  let versionFetchError = 'none';
  let versionFetchCount = 0;
  let versionFetchStarted = launched;
  let versionFetchMilliseconds = 0;
  child.once('error', () => {
    spawnFailed = true;
  });
  child.on('message', (message) => {
    if (child.pid === undefined || message?.pid !== child.pid) return;
    if (message.kind === 'readiness' && phases.indexOf(message.phase) > phases.indexOf(phase)) {
      phase = message.phase;
      phaseStarted = performance.now();
    }
    if (message.kind === 'bound' && [3000, 3011].includes(message.port))
      bindings.set(
        message.port,
        message.address === '127.0.0.1' ? 'owned_loopback' : 'other_observed',
      );
    if (
      message.kind === 'version-fetch' &&
      ['pending', 'settled', 'failed'].includes(message.state) &&
      ['none', 'connection_refused', 'timeout', 'connection_reset', 'tls', 'other'].includes(
        message.errorKind,
      ) &&
      Number.isSafeInteger(message.elapsedMilliseconds) &&
      message.elapsedMilliseconds >= 0 &&
      message.elapsedMilliseconds <= 120000
    ) {
      versionFetch = message.state;
      versionFetchError = message.errorKind;
      versionFetchMilliseconds = message.elapsedMilliseconds;
      if (message.state === 'pending') {
        versionFetchCount = Math.min(65536, versionFetchCount + 1);
        versionFetchStarted = performance.now();
      }
    }
  });
  const exit = () =>
    spawnFailed
      ? 'spawn_failed'
      : child.signalCode !== null
        ? 'signalled'
        : child.exitCode === null
          ? 'running'
          : child.exitCode === 0
            ? 'exited_zero'
            : 'exited_nonzero';
  const milliseconds = (started) => Math.max(0, Math.round(performance.now() - started));

  return {
    output(chunk) {
      const text = outputOverlap + chunk.toString();
      for (const [key, values] of Object.entries(keywords))
        flags[key] ||= values.some((value) => text.includes(value));
      outputOverlap = text.slice(-64);
    },
    async wait({ port, probe, assertActive }) {
      const started = performance.now();
      const deadline = started + READINESS_MILLISECONDS;
      let probeCount = 0;
      let responseCount = 0;
      let lastStatus = null;
      let lastProbeKind = 'none';
      let lastProbeMilliseconds = 0;
      const observation = () => ({
        phase,
        bind: bindings.get(port) ?? 'unobserved',
        exit: exit(),
        probeKind: lastProbeKind,
        probeCount,
        responseCount,
        lastStatus,
        waitMilliseconds: milliseconds(started),
        launchMilliseconds: milliseconds(launched),
        phaseMilliseconds: milliseconds(phaseStarted),
        lastProbeMilliseconds,
        versionFetch,
        versionFetchError,
        versionFetchCount,
        versionFetchMilliseconds:
          versionFetch === 'pending' ? milliseconds(versionFetchStarted) : versionFetchMilliseconds,
        ...flags,
      });
      const failure = (prefix) => new Error(prefix + ': ' + JSON.stringify(observation()));
      while (performance.now() < deadline) {
        assertActive();
        if (exit() !== 'running') throw failure('Fixture startup failed');
        const probeStarted = performance.now();
        probeCount++;
        lastProbeKind = 'pending';
        try {
          const response = await probe();
          responseCount++;
          lastStatus =
            Number.isInteger(response.status) && response.status >= 100 && response.status <= 599
              ? response.status
              : null;
          lastProbeKind = 'http_unhealthy';
          if (response.status === 200) {
            lastProbeKind = 'invalid_json';
            const healthy = JSON.parse(response.body).status === 'healthy';
            lastProbeKind = healthy ? 'healthy_unattributed' : 'http_unhealthy';
            if (healthy && bindings.get(port) === 'owned_loopback' && exit() === 'running') {
              lastProbeKind = 'healthy';
              lastProbeMilliseconds = milliseconds(probeStarted);
              return observation();
            }
          }
        } catch (error) {
          // A parser error is distinguished from a rejected transport probe.
          if (lastProbeKind !== 'invalid_json') lastProbeKind = probeKind(error);
        } finally {
          lastProbeMilliseconds = milliseconds(probeStarted);
        }
        await new Promise((resolve) => setTimeout(resolve, 100));
      }
      throw failure('Fixture did not become ready');
    },
  };
}
