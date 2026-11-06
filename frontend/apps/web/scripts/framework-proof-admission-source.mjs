// Pure copied-source admission instrumentation; no fixture starts on import.
import assert from 'node:assert/strict';

function replaceText(source, needle, replacement) {
  assert.equal(source.split(needle).length, 2, 'Disposable instrumentation anchor is unique');
  return source.replace(needle, replacement);
}

export function instrumentCopiedAdmissionRuntime(sourceText) {
  let launcher = sourceText;
  launcher = replaceText(
    launcher,
    '      { installPublicAdmissionRuntime },',
    '      { createPublicAdmissionRuntime, installPublicAdmissionRuntime },',
  );
  launcher = replaceText(
    launcher,
    '    admission = installPublicAdmissionRuntime(bff.enabled);',
    `    admission = installPublicAdmissionRuntime(bff.enabled);
  const { createNativeCompletionObserver } = await ownSetup(import('./admission-completion.mjs'));
  assertStarting();
  const completions = createNativeCompletionObserver();
  const snapshotIds = new WeakMap();
  let snapshotsSeen = 0;
  let reads = 0;
  let immutable = true;
  let lateRefused = 0;
  let lateAccepted = 0;
  let finishLate;
  const originalOrigin = process.env.LEARNSTACK_PUBLIC_API_ORIGIN;
  const fixture = {
    mode: 'normal', finished: Promise.resolve(),
    record(site) {
      reads++;
      if (!snapshotIds.has(site)) snapshotIds.set(site, ++snapshotsSeen);
      immutable &&= Object.isFrozen(site) && Object.isFrozen(site.enabledLocales)
        && (site.theme === null || Object.isFrozen(site.theme));
      const name = site.displayName;
      const locale = site.enabledLocales[0];
      try { site.displayName = 'fixture mutation'; } catch {}
      try { site.enabledLocales[0] = 'fixture mutation'; } catch {}
      immutable &&= site.displayName === name && site.enabledLocales[0] === locale;
      if (site.theme !== null) {
        const primary = site.theme.primary;
        try { site.theme.primary = 'fixture mutation'; } catch {}
        immutable &&= site.theme.primary === primary;
      }
    },
    late(refused) { if (refused) lateRefused++; else lateAccepted++; }
  };
  Object.defineProperty(globalThis, Symbol.for('learnstack.fixture.admission'), { value: fixture });
  Object.defineProperty(globalThis, Symbol.for('learnstack.fixture.duplicate'), {
    value: createPublicAdmissionRuntime().holder, writable: false, enumerable: false, configurable: false
  });
  control.proof.onCommand(async (message) => {
    if (message.op === 'mode') {
      fixture.mode = message.value;
      process.env.LEARNSTACK_PUBLIC_API_ORIGIN = message.value === 'configuration' ? 'invalid' : originalOrigin;
      if (message.value === 'late') fixture.finished = new Promise((resolve) => { finishLate = resolve; });
    }
    if (message.op === 'settled') await completions.settled();
    control.proof.emit({ id: message.id, ...admission.counts(), snapshotsSeen, reads, immutable, lateRefused, lateAccepted });
  });`,
  );
  launcher = replaceText(
    launcher,
    '        if (!binding) return refuseIngress(response);',
    `        if (!binding) return refuseIngress(response);
      if (fixture.mode === 'late') response.once('finish', () => finishLate?.());`,
  );
  launcher = replaceText(
    launcher,
    `.then(() =>
            admission.run(binding, request, response, () =>
              bffRequests
                ? bffRequests.run(binding, request, response, () =>
                    nativeInvitationTarget(binding.target)
                      ? invitationHandler(binding, request, response)
                      : handle(request, response),
                  )
                : handle(request, response),
            ),
          )`,
    `.then(() => fixture.mode === 'missing'
          ? handle(request, response)
          : admission.run(fixture.mode === 'mismatch' ? { ...binding, target: '/mismatch' } : binding,
              request, response, () => {
                completions.track(response);
                return bffRequests
                  ? bffRequests.run(binding, request, response, () =>
                      nativeInvitationTarget(binding.target)
                        ? invitationHandler(binding, request, response)
                        : handle(request, response))
                  : handle(request, response);
              }))`,
  );
  launcher = replaceText(
    launcher,
    '      ownShutdown(() => admission.shutdown());',
    `      ownShutdown(async () => {
        await admission.shutdown();
        control.proof.emit({ kind: 'shutdown', counts: admission.counts() });
      });`,
  );

  return launcher;
}
